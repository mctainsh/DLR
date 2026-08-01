using System.Globalization;
using System.Security.Claims;
using DLR.Core.Contracts.Comments;
using DLR.Server.Data;
using DLR.Server.Data.Comments;
using DLR.Server.Data.Photos;
using DLR.Server.Data.Rides;
using DLR.Server.Hubs;
using DLR.Server.Identity;
using DLR.Server.Moderation;
using DLR.Server.Rides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DLR.Server.Comments;

/// <summary>
/// The ride thread (§17).
/// <para>
/// <strong>The safety decision comes before the feature.</strong> The people this notifies are
/// operating vehicles, so §17.1's rules about what pushes are not a preference to be tuned later.
/// Nothing in this file pushes anything — the notification half is a client task — but the pinning
/// that §17.6 makes the one live-ride exception is here, and its cap is what keeps that exception
/// narrow.
/// </para>
/// </summary>
public static class CommentEndpoints
{
	/// <summary>Route name for the thread.</summary>
	public const string ThreadRouteName = "RideThread";

	/// <summary>Route name for posting.</summary>
	public const string PostRouteName = "PostComment";

	/// <summary>Route name for an edit.</summary>
	public const string EditRouteName = "EditComment";

	/// <summary>Route name for a deletion.</summary>
	public const string DeleteRouteName = "DeleteComment";

	/// <summary>Route name for pinning.</summary>
	public const string PinRouteName = "PinComment";

	/// <summary>Maps the comment endpoints.</summary>
	public static IEndpointRouteBuilder MapComments(this IEndpointRouteBuilder endpoints)
	{
		endpoints
			.MapGet("/api/v1/group-rides/{id:guid}/comments", ThreadAsync)
			.RequireAuthorization()
			.WithName(ThreadRouteName)
			.WithSummary("One page of a ride's thread, pinned posts first.");

		endpoints
			.MapPost("/api/v1/group-rides/{id:guid}/comments", PostAsync)
			.RequireAuthorization(AuthorizationPolicies.NotRestricted)
			.WithName(PostRouteName)
			.WithSummary("Posts to a ride's thread.");

		endpoints
			.MapPatch("/api/v1/comments/{id:guid}", EditAsync)
			.RequireAuthorization()
			.WithName(EditRouteName)
			.WithSummary("Edits one's own post, inside the edit window.");

		endpoints
			.MapDelete("/api/v1/comments/{id:guid}", DeleteAsync)
			.RequireAuthorization()
			.WithName(DeleteRouteName)
			.WithSummary("Removes a post.");

		endpoints
			.MapPost("/api/v1/comments/{id:guid}/pin", PinAsync)
			.RequireAuthorization()
			.WithName(PinRouteName)
			.WithSummary("Pins or unpins a post.");

		return endpoints;
	}

	private static async Task<IResult> ThreadAsync(
		Guid id,
		ClaimsPrincipal caller,
		DlrDbContext database,
		IOptions<CommentOptions> options,
		TimeProvider clock,
		string? cursor = null)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		CommentOptions limits = options.Value;

		// Membership is re-read on every request rather than trusted from the last one. A member
		// who was removed keeps their posts and loses the thread (§17.6), and this is where that
		// second half happens.
		bool isMember = await database
			.Set<GroupRideMember>()
			.AnyAsync(member => member.GroupRideId == id && member.UserId == userId);

		if (!isMember)
		{
			return Results.NotFound();
		}

		// Applied to the query, not to the rendered page — filtering after Take would return
		// short pages whose length leaked how many blocked authors were in the range (§17.7).
		IReadOnlySet<Guid> hidden = await BlockList.HiddenFromAsync(database, userId);

		bool firstPage = string.IsNullOrEmpty(cursor);

		// Only on the first page. They render above everything regardless of age, so re-sending
		// them with every page would just be the client de-duplicating (§17.6).
		DateTimeOffset now = clock.GetUtcNow();

		List<CommentDto> pinned = firstPage
			? await HydrateAsync(
				database,
				await Project(database
						.Set<RideComment>()
						.Where(comment => comment.GroupRideId == id && comment.IsPinned)
						.OrderByDescending(comment => comment.PinnedUtc),
					limits)
					.ToListAsync(),
				userId,
				now,
				hidden)
			: [];

		IQueryable<RideComment> page = database
			.Set<RideComment>()
			.Where(comment => comment.GroupRideId == id && !hidden.Contains(comment.AuthorId));

		if (Cursor.TryParse(cursor, out DateTimeOffset before, out Guid beforeId))
		{
			// The tiebreak on Id is load-bearing. The fake clock does not tick unless a test moves
			// it and a real one has finite resolution, so two comments can share a PostedUtc — and
			// a cursor keyed on time alone would either skip one or serve it twice.
			page = page.Where(comment =>
				comment.PostedUtc < before
				|| (comment.PostedUtc == before && comment.Id.CompareTo(beforeId) < 0));
		}

		// One more than the page, so "is there another page" needs no second query.
		List<CommentDto> comments = await Project(
				page
					.OrderByDescending(comment => comment.PostedUtc)
					.ThenByDescending(comment => comment.Id)
					.Take(limits.PageSize + 1),
				limits)
			.ToListAsync();

		string? next = null;

		if (comments.Count > limits.PageSize)
		{
			comments.RemoveAt(comments.Count - 1);

			CommentDto last = comments[^1];

			next = Cursor.For(last.PostedUtc, last.Id);
		}

		return Results.Ok(new CommentPage(
			pinned,
			await HydrateAsync(database, comments, userId, now, hidden),
			next));
	}

	private static async Task<IResult> PostAsync(
		Guid id,
		PostCommentRequest request,
		ClaimsPrincipal caller,
		DlrDbContext database,
		IHubContext<RideHub, IRideClient> hub,
		RequestThrottle throttle,
		IOptions<CommentOptions> options,
		TimeProvider clock)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		CommentOptions limits = options.Value;

		GroupRideMember? membership = await database
			.Set<GroupRideMember>()
			.Include(member => member.Ride)
			.SingleOrDefaultAsync(member => member.GroupRideId == id && member.UserId == userId);

		if (membership?.Ride is null)
		{
			return Problem(
				StatusCodes.Status403Forbidden,
				"Not a member",
				"A ride's thread is visible to the people in it and nobody else.");
		}

		if (membership.Ride.State is GroupRideState.Archived)
		{
			return Problem(
				StatusCodes.Status409Conflict,
				"Ride is archived",
				"An archived ride's thread is read-only.");
		}

		if (!RideContentPermissions.Allows(membership.Ride, membership.Role, RideContent.Comment))
		{
			return RideContentPermissions.Refuse(RideContent.Comment);
		}

		// The photo switch is separate from the comment switch, so a member who may post text may
		// still be refused the image (§5.8). Checked before the body, because "photos are off" is
		// a more useful answer than "your post is empty" to somebody who only attached a picture.
		if (request.PhotoId is not null
			&& !RideContentPermissions.Allows(membership.Ride, membership.Role, RideContent.Photo))
		{
			return RideContentPermissions.Refuse(RideContent.Photo);
		}

		string? body = Clean(request.Body);

		if (body is null && request.PhotoId is null)
		{
			return Problem(
				StatusCodes.Status400BadRequest,
				"Nothing to post",
				"A comment carries text, a photograph, or both.");
		}

		if (body is not null && body.Length > limits.MaxChars)
		{
			return Problem(
				StatusCodes.Status400BadRequest,
				"Too long",
				$"A comment is at most {limits.MaxChars} characters.");
		}

		if (request.PhotoId is { } photoId)
		{
			// Their own upload, not merely one that exists — otherwise a guessed identifier posts
			// somebody else's photograph into a thread they chose.
			bool ownsIt = await database
				.Set<Photo>()
				.AnyAsync(photo => photo.Id == photoId && photo.OwnerId == userId);

			if (!ownsIt)
			{
				return Problem(
					StatusCodes.Status404NotFound,
					"No such photo",
					"Upload the image first, and attach one you uploaded.");
			}
		}

		// Idempotency before the throttle and before the cap: a re-sent post is not a new post, so
		// charging it against either would let a flaky connection exhaust a rider's own allowance.
		RideComment? existing = await database
			.Set<RideComment>()
			.SingleOrDefaultAsync(comment =>
				comment.GroupRideId == id
				&& comment.AuthorId == userId
				&& comment.ClientGuid == request.ClientGuid);

		if (existing is not null)
		{
			return Results.Ok(await DescribeAsync(database, existing.Id, limits, userId, clock.GetUtcNow()));
		}

		if (!throttle.TryAcquire(
			$"comment:{userId}:{id}",
			limits.PostsPerHourPerUserPerRide,
			TimeSpan.FromHours(1)))
		{
			return Results.StatusCode(StatusCodes.Status429TooManyRequests);
		}

		int inThread = await database
			.Set<RideComment>()
			.CountAsync(comment => comment.GroupRideId == id);

		if (inThread >= limits.MaxPerRide)
		{
			return Problem(
				StatusCodes.Status409Conflict,
				"Thread is full",
				$"This ride's thread already holds {inThread} posts.");
		}

		if (request.Poll is { } spec)
		{
			if (ValidatePoll(spec, body, limits) is { } badPoll)
			{
				return badPoll;
			}

			int polls = await database
				.Set<Poll>()
				.CountAsync(poll => poll.Comment!.GroupRideId == id);

			if (polls >= limits.MaxPollsPerRide)
			{
				return Problem(
					StatusCodes.Status409Conflict,
					"Too many polls",
					$"This ride already has {polls} polls.");
			}
		}

		DateTimeOffset now = clock.GetUtcNow();

		RideComment comment = new()
		{
			Id = Guid.NewGuid(),
			GroupRideId = id,
			AuthorId = userId,
			ClientGuid = request.ClientGuid,
			Kind = request.Poll is null ? RideCommentKind.Text : RideCommentKind.Poll,
			Body = body,
			PhotoId = request.PhotoId,

			// Clamped, never trusted. A client clock set to next year would otherwise pin this
			// post above everything for as long as the ride exists (§17.3). Ordering does not
			// depend on it — that is PostedUtc's job — but the "written at" line the UI shows does.
			CreatedUtc = request.CreatedUtc is { } authored && authored < now ? authored : now,
			PostedUtc = now,
		};

		database.Add(comment);

		// One insert, one transaction. A poll saved separately from its comment could leave a
		// Kind = Poll row with nothing hanging off it, which every reader would then have to
		// tolerate (§17.5).
		if (request.Poll is { } poll)
		{
			database.Add(new Poll
			{
				CommentId = comment.Id,
				AllowMultiple = poll.AllowMultiple,
				ClosesUtc = poll.ClosesUtc,
			});

			int ordinal = 0;

			foreach (string option in poll.Options)
			{
				database.Add(new PollOption
				{
					Id = Guid.NewGuid(),
					CommentId = comment.Id,
					Ordinal = ordinal++,
					Text = option.Trim(),
				});
			}
		}

		await database.SaveChangesAsync();

		CommentDto dto = await DescribeAsync(database, comment.Id, limits, userId, now);

		await hub.Clients.Group(RideHub.Group(id)).CommentPosted(dto);

		return Results.Created($"/api/v1/comments/{comment.Id}", dto);
	}

	private static async Task<IResult> EditAsync(
		Guid id,
		EditCommentRequest request,
		ClaimsPrincipal caller,
		DlrDbContext database,
		IHubContext<RideHub, IRideClient> hub,
		IOptions<CommentOptions> options,
		TimeProvider clock)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		CommentOptions limits = options.Value;

		(RideComment? comment, GroupRideMember? membership) = await LoadAsync(database, id, userId);

		if (comment is null || membership?.Ride is null)
		{
			return Results.NotFound();
		}

		if (comment.AuthorId != userId)
		{
			return Problem(
				StatusCodes.Status403Forbidden,
				"Not yours to edit",
				"Only the author edits a post. An organiser who wants it gone deletes it.");
		}

		if (membership.Ride.State is GroupRideState.Archived)
		{
			return Problem(
				StatusCodes.Status409Conflict,
				"Ride is archived",
				"An archived ride's thread is read-only.");
		}

		// Measured from when the server received it, not from when the rider claims to have
		// written it — otherwise a post composed offline four hours ago arrives already
		// un-editable, which is the opposite of what the window is for.
		DateTimeOffset now = clock.GetUtcNow();
		DateTimeOffset closes = comment.PostedUtc.AddMinutes(limits.EditWindowMinutes);

		if (now > closes)
		{
			return Problem(
				StatusCodes.Status409Conflict,
				"Edit window has closed",
				$"A post can be edited for {limits.EditWindowMinutes} minutes. After that, delete " +
				"and repost — a permanently editable thread lets somebody rewrite what a poll was " +
				"asking after people have voted on it.");
		}

		string? body = Clean(request.Body);

		if (body is null && comment.PhotoId is null)
		{
			return Problem(
				StatusCodes.Status400BadRequest,
				"Nothing to post",
				"A comment carries text, a photograph, or both.");
		}

		if (body is not null && body.Length > limits.MaxChars)
		{
			return Problem(
				StatusCodes.Status400BadRequest,
				"Too long",
				$"A comment is at most {limits.MaxChars} characters.");
		}

		comment.Body = body;
		comment.EditedUtc = now;

		await database.SaveChangesAsync();

		CommentDto dto = await DescribeAsync(database, comment.Id, limits, userId, now);

		await hub.Clients.Group(RideHub.Group(comment.GroupRideId)).CommentEdited(dto);

		return Results.Ok(dto);
	}

	private static async Task<IResult> DeleteAsync(
		Guid id,
		ClaimsPrincipal caller,
		DlrDbContext database,
		IHubContext<RideHub, IRideClient> hub)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		(RideComment? comment, GroupRideMember? membership) = await LoadAsync(database, id, userId);

		if (comment is null || membership?.Ride is null)
		{
			return Results.NotFound();
		}

		// The author, or the organiser and their leaders (§17.7). An organiser who removed
		// somebody for abuse needs to be able to take the posts down too.
		bool mayDelete =
			comment.AuthorId == userId
			|| membership.Role is GroupRideRole.Owner or GroupRideRole.Leader;

		if (!mayDelete)
		{
			return Problem(
				StatusCodes.Status403Forbidden,
				"Not yours to delete",
				"A post is removed by its author, or by the organiser.");
		}

		if (membership.Ride.State is GroupRideState.Archived)
		{
			return Problem(
				StatusCodes.Status409Conflict,
				"Ride is archived",
				"An archived ride's thread is read-only.");
		}

		Guid rideId = comment.GroupRideId;

		database.Remove(comment);

		await database.SaveChangesAsync();

		await hub.Clients.Group(RideHub.Group(rideId)).CommentRemoved(id);

		return Results.NoContent();
	}

	private static async Task<IResult> PinAsync(
		Guid id,
		PinCommentRequest request,
		ClaimsPrincipal caller,
		DlrDbContext database,
		IHubContext<RideHub, IRideClient> hub,
		IOptions<CommentOptions> options,
		TimeProvider clock)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		CommentOptions limits = options.Value;

		(RideComment? comment, GroupRideMember? membership) = await LoadAsync(database, id, userId);

		if (comment is null || membership?.Ride is null)
		{
			return Results.NotFound();
		}

		// Pinning is the deliberate act that says "this is worth a phone buzzing at 100 km/h"
		// (§17.1), so it belongs to the people who run the ride and to nobody else.
		if (membership.Role is not (GroupRideRole.Owner or GroupRideRole.Leader))
		{
			return Problem(
				StatusCodes.Status403Forbidden,
				"Not yours to pin",
				"The organiser and leaders keep the ride's noticeboard.");
		}

		if (membership.Ride.State is GroupRideState.Archived)
		{
			return Problem(
				StatusCodes.Status409Conflict,
				"Ride is archived",
				"An archived ride's thread is read-only.");
		}

		if (request.Pinned && !comment.IsPinned)
		{
			int alreadyPinned = await database
				.Set<RideComment>()
				.CountAsync(row => row.GroupRideId == comment.GroupRideId && row.IsPinned);

			if (alreadyPinned >= limits.MaxPinned)
			{
				return Problem(
					StatusCodes.Status409Conflict,
					"Too many pinned posts",
					$"A ride keeps at most {limits.MaxPinned} pinned posts. Pinning is the one thing " +
					"that still reaches a phone mid-ride, so a noticeboard of twenty is not a " +
					"noticeboard — unpin something first.");
			}
		}

		comment.IsPinned = request.Pinned;
		comment.PinnedByUserId = request.Pinned ? userId : null;
		comment.PinnedUtc = request.Pinned ? clock.GetUtcNow() : null;

		await database.SaveChangesAsync();

		await hub.Clients
			.Group(RideHub.Group(comment.GroupRideId))
			.CommentPinChanged(comment.Id, comment.IsPinned);

		return Results.Ok(await DescribeAsync(database, comment.Id, limits, userId, clock.GetUtcNow()));
	}

	/// <summary>
	/// The comment and the caller's membership of its ride, in one round trip. Both are needed by
	/// every write path, and a caller who is not in the ride must not be able to tell a comment
	/// that exists from one that does not.
	/// </summary>
	private static async Task<(RideComment?, GroupRideMember?)> LoadAsync(
		DlrDbContext database,
		Guid commentId,
		Guid userId)
	{
		RideComment? comment = await database
			.Set<RideComment>()
			.SingleOrDefaultAsync(row => row.Id == commentId);

		if (comment is null)
		{
			return (null, null);
		}

		GroupRideMember? membership = await database
			.Set<GroupRideMember>()
			.Include(member => member.Ride)
			.SingleOrDefaultAsync(member =>
				member.GroupRideId == comment.GroupRideId && member.UserId == userId);

		return (comment, membership);
	}

	/// <summary>
	/// A poll's shape, checked before anything is written (§17.5).
	/// </summary>
	private static IResult? ValidatePoll(PollSpec spec, string? question, CommentOptions limits)
	{
		// The question is the comment's body, so a poll with no body is a poll with no question.
		// There is no second field to fall back on, which is the point of §17.5's arrangement.
		if (question is null)
		{
			return Problem(
				StatusCodes.Status400BadRequest,
				"A poll needs a question",
				"The comment's body is the question.");
		}

		List<string> options = [.. spec.Options.Select(option => option.Trim())];

		// Two is the floor because one option is not a question, and a poll of one would render
		// as a button that does nothing.
		if (options.Count < 2 || options.Count > limits.MaxPollOptions)
		{
			return Problem(
				StatusCodes.Status400BadRequest,
				"Wrong number of options",
				$"A poll offers between 2 and {limits.MaxPollOptions} options.");
		}

		if (options.Any(string.IsNullOrEmpty))
		{
			return Problem(
				StatusCodes.Status400BadRequest,
				"An option is blank",
				"Every option needs a label.");
		}

		if (options.Any(option => option.Length > limits.PollOptionMaxChars))
		{
			return Problem(
				StatusCodes.Status400BadRequest,
				"An option is too long",
				$"An option label is at most {limits.PollOptionMaxChars} characters.");
		}

		return options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count
			? Problem(
				StatusCodes.Status400BadRequest,
				"Duplicate options",
				"Two options with the same label cannot be told apart in the results.")
			: null;
	}

	private static async Task<CommentDto> DescribeAsync(
		DlrDbContext database,
		Guid commentId,
		CommentOptions limits,
		Guid? forUser = null,
		DateTimeOffset now = default)
	{
		CommentDto dto = await Project(
				database.Set<RideComment>().Where(comment => comment.Id == commentId),
				limits)
			.SingleAsync();

		return (await HydrateAsync(database, [dto], forUser, now))[0];
	}

	/// <summary>
	/// Fills in reactions and poll results for a page of comments.
	/// <para>
	/// Two extra queries for the whole page rather than two per comment. A thread page is fifty
	/// posts, and a hundred round trips to render one screen is the N+1 that makes a fast feature
	/// feel broken. It is done after the projection rather than inside it because a tally is a
	/// grouped aggregate and a poll is three joined tables — neither belongs in the translated
	/// <c>Select</c> that builds the row.
	/// </para>
	/// </summary>
	private static async Task<List<CommentDto>> HydrateAsync(
		DlrDbContext database,
		List<CommentDto> comments,
		Guid? forUser,
		DateTimeOffset now,
		IReadOnlySet<Guid>? hidden = null)
	{
		if (comments.Count == 0)
		{
			return comments;
		}

		// The reader's own block list, if the caller did not already have it to hand. Blocking
		// hides a person's reactions and votes as well as their posts (§17.7), so the tally has to
		// know about it too — a count that still included them would be the one place their
		// presence leaked through.
		hidden ??= forUser is { } reader
			? await BlockList.HiddenFromAsync(database, reader)
			: new HashSet<Guid>();

		List<Guid> ids = [.. comments.Select(comment => comment.Id)];

		IReadOnlyDictionary<Guid, ReactionCounts> reactions =
			await CommentReactions.CountsAsync(database, ids, forUser, hidden);

		List<Guid> pollIds =
			[.. comments.Where(comment => comment.Kind is CommentKindDto.Poll).Select(comment => comment.Id)];

		IReadOnlyDictionary<Guid, PollResults> polls =
			await CommentPolls.ResultsAsync(database, pollIds, forUser, now, hidden);

		for (int i = 0; i < comments.Count; i++)
		{
			CommentDto comment = comments[i];

			comments[i] = comment with
			{
				Reactions = reactions.TryGetValue(comment.Id, out ReactionCounts? counts)
					? counts
					: ReactionCounts.None,
				Poll = polls.TryGetValue(comment.Id, out PollResults? poll) ? poll : null,
			};
		}

		return comments;
	}

	private static IQueryable<CommentDto> Project(IQueryable<RideComment> comments, CommentOptions limits) =>
		comments.AsNoTracking().Select(comment => new CommentDto(
			comment.Id,
			comment.GroupRideId,
			comment.AuthorId,
			comment.Author!.UserName!,

			// A comparison, not a cast. `Kind` is stored as a string (HasConversion<string>), and
			// `(CommentKindDto)comment.Kind` inside a translated projection compiles perfectly and
			// then asks PostgreSQL to cast the text 'Text' to an integer at runtime.
			comment.Kind == RideCommentKind.Poll ? CommentKindDto.Poll : CommentKindDto.Text,
			comment.Body,
			comment.PhotoId,
			comment.IsPinned,
			comment.CreatedUtc,
			comment.PostedUtc,
			comment.EditedUtc,
			comment.PostedUtc - comment.CreatedUtc > TimeSpan.FromMinutes(limits.StaleAuthorMinutes)));

	/// <summary>
	/// Trims and normalises empty to null. <strong>No sanitising, because nothing is rendered as
	/// markup</strong> — the body is plain text end to end (§17.2), and a "sanitiser" here would
	/// imply otherwise to the next person who reads it.
	/// </summary>
	private static string? Clean(string? body)
	{
		string? trimmed = body?.Trim();

		return string.IsNullOrEmpty(trimmed) ? null : trimmed;
	}

	private static IResult Problem(int status, string title, string detail) =>
		Results.Problem(new ProblemDetails { Status = status, Title = title, Detail = detail });

	/// <summary>
	/// The thread cursor: a receipt instant and the id that breaks its tie.
	/// <para>
	/// Opaque to the caller on purpose — it is a position in a result set, not a filter, and a
	/// client that took it apart would depend on the sort order never changing.
	/// </para>
	/// </summary>
	private static class Cursor
	{
		public static string For(DateTimeOffset postedUtc, Guid id) =>
			$"{postedUtc.UtcTicks.ToString(CultureInfo.InvariantCulture)}_{id:N}";

		public static bool TryParse(string? cursor, out DateTimeOffset before, out Guid beforeId)
		{
			before = default;
			beforeId = default;

			if (string.IsNullOrEmpty(cursor))
			{
				return false;
			}

			string[] parts = cursor.Split('_');

			if (parts.Length != 2
				|| !long.TryParse(parts[0], CultureInfo.InvariantCulture, out long ticks)
				|| !Guid.TryParseExact(parts[1], "N", out beforeId))
			{
				return false;
			}

			before = new DateTimeOffset(ticks, TimeSpan.Zero);

			return true;
		}
	}
}
