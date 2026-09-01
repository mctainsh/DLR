using DLR.Core.Comments;
using DLR.Core.Contracts.Comments;
using DLR.Server.Data;
using DLR.Server.Data.Comments;
using DLR.Server.Identity;
using DLR.Server.Moderation;
using DLR.Server.Rides;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Comments;

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
public static class ReactionEndpoints
{
	/// <summary>Route name for setting a reaction.</summary>
	public const string ReactRouteName = "SetReaction";

	/// <summary>Route name for voting.</summary>
	public const string VoteRouteName = "CastVote";

	/// <summary>Route name for closing a poll.</summary>
	public const string ClosePollRouteName = "ClosePoll";
}

/// <summary>
/// Reactions and poll votes (§17.4, §17.5).
/// <para>
/// <strong>Neither is ever gated by the §5.8 content switches</strong>, and that is deliberate
/// rather than an oversight: a reaction carries no free text, no image and no storage cost worth
/// naming, and switching off the ability to answer a poll would break the poll rather than
/// moderate it. Reaching the thread at all is the only check.
/// </para>
/// <para>
/// Which also means this file did not have to learn that a shared route has a thread now (§6.2).
/// It asks <see cref="CommentThreadAccess"/> the same question it always asked and gets an answer
/// for either kind - the reactions, the votes, the coalescing and the tallies are unchanged.
/// </para>
/// </summary>
[ApiController]
[Authorize]
public sealed class ReactionController : ControllerBase
{
	[HttpPut("/api/v1/comments/{id:guid}/reaction", Name = ReactionEndpoints.ReactRouteName)]
	[EndpointSummary("Sets the caller's reaction, or clears it with a null.")]
	public async Task<IActionResult> ReactAsync(
		[FromRoute] Guid id,
		[FromBody] SetReactionRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] ReactionBroadcastService broadcast,
		CancellationToken cancellationToken)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		(RideComment? comment, ThreadAccess access) =
			await CommentThreadAccess.ForCommentAsync(database, id, userId, cancellationToken);

		if (comment is null)
		{
			return NotFound();
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
			// Length and character set, not membership - the same forward-compatibility rule as
			// marker icons (§16.2, §17.4). A newer client's key is stored and rendered generically.
			if (!ReactionKeys.IsStorable(request.Reaction))
			{
				return Problem(
					statusCode: StatusCodes.Status400BadRequest,
					title: "Bad reaction key",
					detail: $"A reaction key is up to {ReactionKeys.MaxLength} lowercase letters, digits and hyphens.");
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
				// Replaces, never accumulates - which the primary key already guarantees, but
				// updating in place is what makes that a single row rather than a delete and an
				// insert racing each other.
				existing.Reaction = request.Reaction;
			}
		}

		await database.SaveChangesAsync(cancellationToken);

		// Marked dirty rather than sent. Twelve members tapping the same thumbs-up would otherwise
		// be a message per tap per connection (§17.4).
		broadcast.ReactionChanged(id, access.HubGroup);

		return Ok(await CommentReactions.CountsAsync(
			database,
			id,
			userId,
			await BlockList.HiddenFromAsync(database, userId, cancellationToken),
			cancellationToken));
	}

	[HttpPost("/api/v1/comments/{id:guid}/votes", Name = ReactionEndpoints.VoteRouteName)]
	[EndpointSummary("Casts, changes or clears the caller's vote.")]
	public async Task<IActionResult> VoteAsync(
		[FromRoute] Guid id,
		[FromBody] CastVoteRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] ReactionBroadcastService broadcast,
		[FromServices] TimeProvider clock,
		CancellationToken cancellationToken)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		(RideComment? comment, ThreadAccess access) =
			await CommentThreadAccess.ForCommentAsync(database, id, userId, cancellationToken);

		if (comment is null)
		{
			return NotFound();
		}

		Poll? poll = await database
			.Set<Poll>()
			.Include(row => row.Options)
			.SingleOrDefaultAsync(row => row.CommentId == id, cancellationToken);

		if (poll is null)
		{
			return NotFound();
		}

		DateTimeOffset now = clock.GetUtcNow();

		// Closed by hand, or past its own deadline - decided here, against the clock, with no job
		// involved. A distinguishable 409, so a client can say "this poll has closed" rather than
		// "something went wrong" (§17.5).
		if (poll.IsClosed(now))
		{
			return new ObjectResult(new ProblemDetails
			{
				Status = StatusCodes.Status409Conflict,
				Title = "Poll has closed",
				Detail = poll.ClosedUtc is not null
					? "Somebody closed this poll."
					: "This poll's deadline has passed.",
				Extensions = { ["problem"] = "PollClosed" },
			})
			{
				StatusCode = StatusCodes.Status409Conflict,
				ContentTypes = { "application/problem+json" },
			};
		}

		HashSet<Guid> chosen = [.. request.OptionIds];

		// Options from *this* poll. Without the check a caller could vote on another ride's poll
		// through a comment id they legitimately hold.
		if (chosen.Any(optionId => poll.Options.All(option => option.Id != optionId)))
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "No such option",
				detail: "An option must belong to the poll being voted on.");
		}

		if (!poll.AllowMultiple && chosen.Count > 1)
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "One option only",
				detail: "This poll takes a single answer.");
		}

		List<Guid> optionIds = [.. poll.Options.Select(option => option.Id)];

		List<PollVote> held = await database
			.Set<PollVote>()
			.Where(vote => optionIds.Contains(vote.PollOptionId) && vote.UserId == userId)
			.ToListAsync(cancellationToken);

		// The request is the full set the voter now holds, for both kinds. Single-select therefore
		// replaces and multi-select toggles, without the endpoint needing two shapes - and an
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

		broadcast.PollChanged(id, access.HubGroup);

		return Ok(await CommentPolls.ResultsAsync(
			database,
			id,
			userId,
			now,
			await BlockList.HiddenFromAsync(database, userId, cancellationToken),
			cancellationToken));
	}

	[HttpPost("/api/v1/comments/{id:guid}/close-poll", Name = ReactionEndpoints.ClosePollRouteName)]
	[EndpointSummary("Closes a poll early.")]
	public async Task<IActionResult> ClosePollAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] ReactionBroadcastService broadcast,
		[FromServices] TimeProvider clock,
		CancellationToken cancellationToken)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		(RideComment? comment, ThreadAccess access) =
			await CommentThreadAccess.ForCommentAsync(database, id, userId, cancellationToken);

		if (comment is null)
		{
			return NotFound();
		}

		Poll? poll = await database
			.Set<Poll>()
			.SingleOrDefaultAsync(row => row.CommentId == id, cancellationToken);

		if (poll is null)
		{
			return NotFound();
		}

		// The author, or whoever runs the thread (§17.5).
		if (comment.AuthorId != userId && !access.CanModerate)
		{
			return Problem(
				statusCode: StatusCodes.Status403Forbidden,
				title: "Not yours to close",
				detail: "A poll is closed by the person who asked, or by whoever runs the thread.");
		}

		DateTimeOffset now = clock.GetUtcNow();

		// ??= so closing a poll twice does not move the moment it closed.
		poll.ClosedUtc ??= now;
		poll.ClosedByUserId ??= userId;

		await database.SaveChangesAsync(cancellationToken);

		broadcast.PollChanged(id, access.HubGroup);

		return Ok(await CommentPolls.ResultsAsync(
			database,
			id,
			userId,
			now,
			await BlockList.HiddenFromAsync(database, userId, cancellationToken),
			cancellationToken));
	}
}
