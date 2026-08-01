using System.Security.Claims;
using DLR.Core.Comments;
using DLR.Core.Contracts.Comments;
using DLR.Server.Data;
using DLR.Server.Data.Comments;
using DLR.Server.Data.Rides;
using DLR.Server.Identity;
using DLR.Server.Moderation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DLR.Server.Comments;

/// <summary>
/// Reactions and poll votes (§17.4, §17.5).
/// <para>
/// <strong>Neither is ever gated by the §5.8 content switches</strong>, and that is deliberate
/// rather than an oversight: a reaction carries no free text, no image and no storage cost worth
/// naming, and switching off the ability to answer a poll would break the poll rather than
/// moderate it. Membership is the only check.
/// </para>
/// </summary>
public static class ReactionEndpoints
{
	/// <summary>Route name for setting a reaction.</summary>
	public const string ReactRouteName = "SetReaction";

	/// <summary>Route name for voting.</summary>
	public const string VoteRouteName = "CastVote";

	/// <summary>Route name for closing a poll.</summary>
	public const string ClosePollRouteName = "ClosePoll";

	/// <summary>Maps the reaction and poll endpoints.</summary>
	public static IEndpointRouteBuilder MapReactions(this IEndpointRouteBuilder endpoints)
	{
		endpoints
			.MapPut("/api/v1/comments/{id:guid}/reaction", ReactAsync)
			.RequireAuthorization()
			.WithName(ReactRouteName)
			.WithSummary("Sets the caller's reaction, or clears it with a null.");

		endpoints
			.MapPost("/api/v1/comments/{id:guid}/votes", VoteAsync)
			.RequireAuthorization()
			.WithName(VoteRouteName)
			.WithSummary("Casts, changes or clears the caller's vote.");

		endpoints
			.MapPost("/api/v1/comments/{id:guid}/close-poll", ClosePollAsync)
			.RequireAuthorization()
			.WithName(ClosePollRouteName)
			.WithSummary("Closes a poll early.");

		return endpoints;
	}

	private static async Task<IResult> ReactAsync(
		Guid id,
		SetReactionRequest request,
		ClaimsPrincipal caller,
		DlrDbContext database,
		ReactionBroadcastService broadcast,
		CancellationToken cancellationToken)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		(RideComment? comment, GroupRideMember? membership) =
			await LoadAsync(database, id, userId, cancellationToken);

		if (comment is null || membership?.Ride is null)
		{
			return Results.NotFound();
		}

		if (membership.Ride.State is GroupRideState.Archived)
		{
			return Problem(
				StatusCodes.Status409Conflict,
				"Ride is archived",
				"An archived ride's thread is read-only.");
		}

		CommentReaction? existing = await database
			.Set<CommentReaction>()
			.SingleOrDefaultAsync(row => row.CommentId == id && row.UserId == userId, cancellationToken);

		if (request.Reaction is null)
		{
			// Cleared means the row goes, not that it is set to something empty. A "none" reaction
			// would be a row per person per comment they had ever looked at.
			if (existing is not null)
			{
				database.Remove(existing);
			}
		}
		else
		{
			// Length and character set, not membership — the same forward-compatibility rule as
			// marker icons (§16.2, §17.4). A newer client's key is stored and rendered generically.
			if (!ReactionKeys.IsStorable(request.Reaction))
			{
				return Problem(
					StatusCodes.Status400BadRequest,
					"Bad reaction key",
					$"A reaction key is up to {ReactionKeys.MaxLength} lowercase letters, digits and hyphens.");
			}

			if (existing is null)
			{
				database.Add(new CommentReaction
				{
					CommentId = id,
					UserId = userId,
					Reaction = request.Reaction,
				});
			}
			else
			{
				// Replaces, never accumulates — which the primary key already guarantees, but
				// updating in place is what makes that a single row rather than a delete and an
				// insert racing each other.
				existing.Reaction = request.Reaction;
			}
		}

		await database.SaveChangesAsync(cancellationToken);

		// Marked dirty rather than sent. Twelve members tapping the same thumbs-up would otherwise
		// be a message per tap per connection (§17.4).
		broadcast.ReactionChanged(id, comment.GroupRideId);

		return Results.Ok(await CommentReactions.CountsAsync(
			database,
			id,
			userId,
			await BlockList.HiddenFromAsync(database, userId, cancellationToken),
			cancellationToken));
	}

	private static async Task<IResult> VoteAsync(
		Guid id,
		CastVoteRequest request,
		ClaimsPrincipal caller,
		DlrDbContext database,
		ReactionBroadcastService broadcast,
		TimeProvider clock,
		CancellationToken cancellationToken)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		(RideComment? comment, GroupRideMember? membership) =
			await LoadAsync(database, id, userId, cancellationToken);

		if (comment is null || membership?.Ride is null)
		{
			return Results.NotFound();
		}

		Poll? poll = await database
			.Set<Poll>()
			.Include(row => row.Options)
			.SingleOrDefaultAsync(row => row.CommentId == id, cancellationToken);

		if (poll is null)
		{
			return Results.NotFound();
		}

		DateTimeOffset now = clock.GetUtcNow();

		// Closed by hand, or past its own deadline — decided here, against the clock, with no job
		// involved. A distinguishable 409, so a client can say "this poll has closed" rather than
		// "something went wrong" (§17.5).
		if (poll.IsClosed(now))
		{
			return Results.Problem(new ProblemDetails
			{
				Status = StatusCodes.Status409Conflict,
				Title = "Poll has closed",
				Detail = poll.ClosedUtc is not null
					? "Somebody closed this poll."
					: "This poll's deadline has passed.",
				Extensions = { ["problem"] = "PollClosed" },
			});
		}

		HashSet<Guid> chosen = [.. request.OptionIds];

		// Options from *this* poll. Without the check a caller could vote on another ride's poll
		// through a comment id they legitimately hold.
		if (chosen.Any(optionId => poll.Options.All(option => option.Id != optionId)))
		{
			return Problem(
				StatusCodes.Status400BadRequest,
				"No such option",
				"An option must belong to the poll being voted on.");
		}

		if (!poll.AllowMultiple && chosen.Count > 1)
		{
			return Problem(
				StatusCodes.Status400BadRequest,
				"One option only",
				"This poll takes a single answer.");
		}

		List<Guid> optionIds = [.. poll.Options.Select(option => option.Id)];

		List<PollVote> held = await database
			.Set<PollVote>()
			.Where(vote => optionIds.Contains(vote.PollOptionId) && vote.UserId == userId)
			.ToListAsync(cancellationToken);

		// The request is the full set the voter now holds, for both kinds. Single-select therefore
		// replaces and multi-select toggles, without the endpoint needing two shapes — and an
		// empty list clears, which is the only way to un-vote.
		foreach (PollVote vote in held.Where(vote => !chosen.Contains(vote.PollOptionId)))
		{
			database.Remove(vote);
		}

		foreach (Guid optionId in chosen.Where(optionId => held.All(vote => vote.PollOptionId != optionId)))
		{
			database.Add(new PollVote
			{
				PollOptionId = optionId,
				UserId = userId,
				CreatedUtc = now,
			});
		}

		await database.SaveChangesAsync(cancellationToken);

		broadcast.PollChanged(id, comment.GroupRideId);

		return Results.Ok(await CommentPolls.ResultsAsync(
			database,
			id,
			userId,
			now,
			await BlockList.HiddenFromAsync(database, userId, cancellationToken),
			cancellationToken));
	}

	private static async Task<IResult> ClosePollAsync(
		Guid id,
		ClaimsPrincipal caller,
		DlrDbContext database,
		ReactionBroadcastService broadcast,
		TimeProvider clock,
		CancellationToken cancellationToken)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		(RideComment? comment, GroupRideMember? membership) =
			await LoadAsync(database, id, userId, cancellationToken);

		if (comment is null || membership?.Ride is null)
		{
			return Results.NotFound();
		}

		Poll? poll = await database
			.Set<Poll>()
			.SingleOrDefaultAsync(row => row.CommentId == id, cancellationToken);

		if (poll is null)
		{
			return Results.NotFound();
		}

		// The author, or the organiser and their leaders (§17.5).
		bool mayClose =
			comment.AuthorId == userId
			|| membership.Role is GroupRideRole.Owner or GroupRideRole.Leader;

		if (!mayClose)
		{
			return Problem(
				StatusCodes.Status403Forbidden,
				"Not yours to close",
				"A poll is closed by the person who asked, or by the organiser.");
		}

		DateTimeOffset now = clock.GetUtcNow();

		// ??= so closing a poll twice does not move the moment it closed.
		poll.ClosedUtc ??= now;
		poll.ClosedByUserId ??= userId;

		await database.SaveChangesAsync(cancellationToken);

		broadcast.PollChanged(id, comment.GroupRideId);

		return Results.Ok(await CommentPolls.ResultsAsync(
			database,
			id,
			userId,
			now,
			await BlockList.HiddenFromAsync(database, userId, cancellationToken),
			cancellationToken));
	}

	private static async Task<(RideComment?, GroupRideMember?)> LoadAsync(
		DlrDbContext database,
		Guid commentId,
		Guid userId,
		CancellationToken cancellationToken)
	{
		RideComment? comment = await database
			.Set<RideComment>()
			.SingleOrDefaultAsync(row => row.Id == commentId, cancellationToken);

		if (comment is null)
		{
			return (null, null);
		}

		GroupRideMember? membership = await database
			.Set<GroupRideMember>()
			.Include(member => member.Ride)
			.SingleOrDefaultAsync(
				member => member.GroupRideId == comment.GroupRideId && member.UserId == userId,
				cancellationToken);

		return (comment, membership);
	}

	private static IResult Problem(int status, string title, string detail) =>
		Results.Problem(new ProblemDetails { Status = status, Title = title, Detail = detail });
}
