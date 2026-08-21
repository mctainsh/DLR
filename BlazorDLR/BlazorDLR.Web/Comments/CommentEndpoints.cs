using System.Globalization;
using DLR.Core.Contracts.Comments;
using DLR.Server.Data;
using DLR.Server.Data.Comments;
using DLR.Server.Data.Photos;
using DLR.Server.Data.Rides;
using DLR.Server.Hubs;
using DLR.Server.Identity;
using DLR.Server.Moderation;
using DLR.Server.Rides;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DLR.Server.Comments;

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
public static class CommentEndpoints
{
	/// <summary>Route name for the thread.</summary>
	public const string ThreadRouteName = "RideThread";

	/// <summary>Route name for posting.</summary>
	public const string PostRouteName = "PostComment";

	/// <summary>Route name for a shared route's thread (§6.2).</summary>
	public const string TrackThreadRouteName = "TrackThread";

	/// <summary>Route name for posting to a shared route's thread.</summary>
	public const string PostToTrackRouteName = "PostTrackComment";

	/// <summary>Route name for an edit.</summary>
	public const string EditRouteName = "EditComment";

	/// <summary>Route name for a deletion.</summary>
	public const string DeleteRouteName = "DeleteComment";

	/// <summary>Route name for pinning.</summary>
	public const string PinRouteName = "PinComment";
}

/// <summary>
/// Threads (§17, §6.2).
/// <para>
/// <strong>Two subjects, one thread implementation.</strong> An adventure has a thread and so does
/// a shared route, and below the question of who is allowed in they are the same conversation —
/// the same plain text, the same photograph, the same six reactions, the same polls, the same
/// fifteen-minute edit window, the same pinning cap, the same reporting and blocking. So there is
/// one controller and one table, and the single thing that differs is resolved once by
/// <see cref="CommentThreadAccess"/> before any of it runs. Every endpoint from
/// <see cref="EditAsync"/> down never learns which kind of thread it is working in, which is why
/// adding the second kind did not add a second set of bugs.
/// </para>
/// <para>
/// Nothing in this file pushes anything — the notification half is a client task. Pinning lives
/// here, and its cap is what keeps the noticeboard short (§17.6); it is no longer the one
/// exception to a live-ride silence, because that silence has been removed. Comments still never
/// reach a car screen (§4.6, §17.1), which is the safety rule that remains structural.
/// </para>
/// </summary>
[ApiController]
[Authorize]
public sealed class CommentController : ControllerBase
{
	[HttpGet("/api/v1/group-rides/{id:guid}/comments", Name = CommentEndpoints.ThreadRouteName)]
	[EndpointSummary("One page of an adventure's thread, pinned posts first.")]
	public async Task<IActionResult> ThreadAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] IOptions<CommentOptions> options,
		[FromServices] TimeProvider clock,
		[FromQuery] string? cursor = null)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		return await PageAsync(
			await CommentThreadAccess.ForRideAsync(database, id, userId),
			database,
			options.Value,
			clock,
			userId,
			cursor);
	}

	[HttpGet("/api/v1/tracks/{id:guid}/comments", Name = CommentEndpoints.TrackThreadRouteName)]
	[EndpointSummary("One page of a shared route's thread, pinned posts first.")]
	public async Task<IActionResult> TrackThreadAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] IOptions<CommentOptions> options,
		[FromServices] TimeProvider clock,
		[FromQuery] string? cursor = null)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		return await PageAsync(
			await CommentThreadAccess.ForTrackAsync(database, id, userId),
			database,
			options.Value,
			clock,
			userId,
			cursor);
	}

	/// <summary>
	/// One page of whichever thread the access object points at (§17.8).
	/// <para>
	/// The paging, the pinned section, the block filter and the cursor are identical for both
	/// kinds and always were — only the "may they read it?" above differs, and that has already
	/// been answered by the time this runs.
	/// </para>
	/// </summary>
	private async Task<IActionResult> PageAsync(
		ThreadAccess access,
		DlrDbContext database,
		CommentOptions limits,
		TimeProvider clock,
		Guid userId,
		string? cursor)
	{
		if (!access.Exists)
		{
			return NotFound();
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
				await Project(InThread(database, access)
						.Where(comment => comment.IsPinned)
						.OrderByDescending(comment => comment.PinnedUtc),
					limits)
					.ToListAsync(),
				userId,
				now,
				hidden)
			: [];

		IQueryable<RideComment> page = InThread(database, access)
			.Where(comment => !hidden.Contains(comment.AuthorId));

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

		return Ok(new CommentPage(
			pinned,
			await HydrateAsync(database, comments, userId, now, hidden),
			next));
	}

	[HttpPost("/api/v1/group-rides/{id:guid}/comments", Name = CommentEndpoints.PostRouteName)]
	[Authorize(Policy = AuthorizationPolicies.NotRestricted)]
	[EndpointSummary("Posts to an adventure's thread.")]
	public async Task<IActionResult> PostAsync(
		[FromRoute] Guid id,
		[FromBody] PostCommentRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] IHubContext<RideHub, IRideClient> hub,
		[FromServices] RequestThrottle throttle,
		[FromServices] IOptions<CommentOptions> options,
		[FromServices] TimeProvider clock)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		return await AddAsync(
			await CommentThreadAccess.ForRideAsync(database, id, userId),
			request,
			database,
			hub,
			throttle,
			options.Value,
			clock,
			userId);
	}

	/// <summary>
	/// Posts to a shared route's thread (§6.2).
	/// <para>
	/// Same policy attribute as an adventure's, and that is the point: §7.8's ladder holds a
	/// brand-new account back from every social surface at once, and a route's thread is the most
	/// public one there is — it is read by every rider on the service rather than by the dozen an
	/// organiser admitted.
	/// </para>
	/// </summary>
	[HttpPost("/api/v1/tracks/{id:guid}/comments", Name = CommentEndpoints.PostToTrackRouteName)]
	[Authorize(Policy = AuthorizationPolicies.NotRestricted)]
	[EndpointSummary("Posts to a shared route's thread.")]
	public async Task<IActionResult> PostToTrackAsync(
		[FromRoute] Guid id,
		[FromBody] PostCommentRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] IHubContext<RideHub, IRideClient> hub,
		[FromServices] RequestThrottle throttle,
		[FromServices] IOptions<CommentOptions> options,
		[FromServices] TimeProvider clock)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		return await AddAsync(
			await CommentThreadAccess.ForTrackAsync(database, id, userId),
			request,
			database,
			hub,
			throttle,
			options.Value,
			clock,
			userId);
	}

	/// <summary>
	/// Adds one post to whichever thread the access object points at (§17.2, §17.3).
	/// <para>
	/// Everything below the permission check was already thread-kind agnostic — the idempotency
	/// key, the throttle, the caps, the clamped authoring time, the poll that rides along on the
	/// same request — so the second kind of thread inherited all of it rather than getting a
	/// second, slightly different copy.
	/// </para>
	/// </summary>
	private async Task<IActionResult> AddAsync(
		ThreadAccess access,
		PostCommentRequest request,
		DlrDbContext database,
		IHubContext<RideHub, IRideClient> hub,
		RequestThrottle throttle,
		CommentOptions limits,
		TimeProvider clock,
		Guid userId)
	{
		if (!access.Exists)
		{
			return Problem(
				statusCode: StatusCodes.Status403Forbidden,
				title: "Not yours to post to",
				detail: "An adventure's thread is visible to the people in it and nobody else, and a "
					+ "route's is visible while it is shared.");
		}

		if (!access.CanPost)
		{
			return RideContentPermissions.AsResult(access.Refusal!);
		}

		// The photo switch is separate from the post switch, so a member who may post text may
		// still be refused the image (§5.8). Checked before the body, because "photos are off" is
		// a more useful answer than "your post is empty" to somebody who only attached a picture.
		if (request.PhotoId is not null && !access.CanAttachPhoto)
		{
			return RideContentPermissions.AsResult(access.PhotoRefusal!);
		}

		string? body = Clean(request.Body);

		if (body is null && request.PhotoId is null)
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "Nothing to post",
				detail: "A comment carries text, a photograph, or both.");
		}

		if (body is not null && body.Length > limits.MaxChars)
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "Too long",
				detail: $"A comment is at most {limits.MaxChars} characters.");
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
					statusCode: StatusCodes.Status404NotFound,
					title: "No such photo",
					detail: "Upload the image first, and attach one you uploaded.");
			}
		}

		// Idempotency before the throttle and before the cap: a re-sent post is not a new post, so
		// charging it against either would let a flaky connection exhaust a rider's own allowance.
		RideComment? existing = await InThread(database, access)
			.SingleOrDefaultAsync(comment =>
				comment.AuthorId == userId && comment.ClientGuid == request.ClientGuid);

		if (existing is not null)
		{
			return Ok(await DescribeAsync(database, existing.Id, limits, userId, clock.GetUtcNow()));
		}

		// Keyed on the thread rather than on the ride, so a rider's allowance in an adventure and
		// their allowance on a route are separate buckets — thirty posts an hour is a limit on
		// flooding one conversation, and spending it on somebody's route should not silence you
		// in the ride you are actually on.
		if (!throttle.TryAcquire(
			$"comment:{userId}:{ThreadKey(access)}",
			limits.PostsPerHourPerUserPerRide,
			TimeSpan.FromHours(1)))
		{
			return StatusCode(StatusCodes.Status429TooManyRequests);
		}

		int inThread = await InThread(database, access).CountAsync();

		if (inThread >= limits.MaxPerRide)
		{
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "Thread is full",
				detail: $"This thread already holds {inThread} posts.");
		}

		if (request.Poll is { } spec)
		{
			if (ValidatePoll(spec, body, limits) is { } badPoll)
			{
				return badPoll;
			}

			int polls = await database
				.Set<Poll>()
				.CountAsync(poll =>
					poll.Comment!.GroupRideId == access.GroupRideId
					&& poll.Comment.TrackId == access.TrackId);

			if (polls >= limits.MaxPollsPerRide)
			{
				return Problem(
					statusCode: StatusCodes.Status409Conflict,
					title: "Too many polls",
					detail: $"This thread already has {polls} polls.");
			}
		}

		DateTimeOffset now = clock.GetUtcNow();

		RideComment comment = new()
		{
			Id = Guid.NewGuid(),
			GroupRideId = access.GroupRideId,
			TrackId = access.TrackId,
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

		await hub.Clients.Group(access.HubGroup).CommentPosted(dto);

		return Created($"/api/v1/comments/{comment.Id}", dto);
	}

	[HttpPatch("/api/v1/comments/{id:guid}", Name = CommentEndpoints.EditRouteName)]
	[EndpointSummary("Edits one's own post, inside the edit window.")]
	public async Task<IActionResult> EditAsync(
		[FromRoute] Guid id,
		[FromBody] EditCommentRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] IHubContext<RideHub, IRideClient> hub,
		[FromServices] IOptions<CommentOptions> options,
		[FromServices] TimeProvider clock)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		CommentOptions limits = options.Value;

		(RideComment? comment, ThreadAccess access) =
			await CommentThreadAccess.ForCommentAsync(database, id, userId);

		if (comment is null)
		{
			return NotFound();
		}

		if (comment.AuthorId != userId)
		{
			return Problem(
				statusCode: StatusCodes.Status403Forbidden,
				title: "Not yours to edit",
				detail: "Only the author edits a post. Whoever runs the thread deletes it instead.");
		}

		if (access.ReadOnly)
		{
			return RideContentPermissions.AsResult(access.Refusal!);
		}

		// Measured from when the server received it, not from when the rider claims to have
		// written it — otherwise a post composed offline four hours ago arrives already
		// un-editable, which is the opposite of what the window is for.
		DateTimeOffset now = clock.GetUtcNow();
		DateTimeOffset closes = comment.PostedUtc.AddMinutes(limits.EditWindowMinutes);

		if (now > closes)
		{
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "Edit window has closed",
				detail: $"A post can be edited for {limits.EditWindowMinutes} minutes. After that, delete " +
				"and repost — a permanently editable thread lets somebody rewrite what a poll was " +
				"asking after people have voted on it.");
		}

		string? body = Clean(request.Body);

		if (body is null && comment.PhotoId is null)
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "Nothing to post",
				detail: "A comment carries text, a photograph, or both.");
		}

		if (body is not null && body.Length > limits.MaxChars)
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "Too long",
				detail: $"A comment is at most {limits.MaxChars} characters.");
		}

		comment.Body = body;
		comment.EditedUtc = now;

		await database.SaveChangesAsync();

		CommentDto dto = await DescribeAsync(database, comment.Id, limits, userId, now);

		await hub.Clients.Group(access.HubGroup).CommentEdited(dto);

		return Ok(dto);
	}

	[HttpDelete("/api/v1/comments/{id:guid}", Name = CommentEndpoints.DeleteRouteName)]
	[EndpointSummary("Removes a post.")]
	public async Task<IActionResult> DeleteAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] IHubContext<RideHub, IRideClient> hub)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		(RideComment? comment, ThreadAccess access) =
			await CommentThreadAccess.ForCommentAsync(database, id, userId);

		if (comment is null)
		{
			return NotFound();
		}

		// The author, or whoever runs the thread (§17.7) — the organiser and their leaders in an
		// adventure, the owner of a shared route. Somebody who removed a person for abuse needs to
		// be able to take the posts down too.
		if (comment.AuthorId != userId && !access.CanModerate)
		{
			return Problem(
				statusCode: StatusCodes.Status403Forbidden,
				title: "Not yours to delete",
				detail: "A post is removed by its author, or by whoever runs the thread — the "
					+ "organiser of an adventure, the owner of a route.");
		}

		if (access.ReadOnly)
		{
			return RideContentPermissions.AsResult(access.Refusal!);
		}

		database.Remove(comment);

		await database.SaveChangesAsync();

		await hub.Clients.Group(access.HubGroup).CommentRemoved(id);

		return NoContent();
	}

	[HttpPost("/api/v1/comments/{id:guid}/pin", Name = CommentEndpoints.PinRouteName)]
	[EndpointSummary("Pins or unpins a post.")]
	public async Task<IActionResult> PinAsync(
		[FromRoute] Guid id,
		[FromBody] PinCommentRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] IHubContext<RideHub, IRideClient> hub,
		[FromServices] IOptions<CommentOptions> options,
		[FromServices] TimeProvider clock)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		CommentOptions limits = options.Value;

		(RideComment? comment, ThreadAccess access) =
			await CommentThreadAccess.ForCommentAsync(database, id, userId);

		if (comment is null)
		{
			return NotFound();
		}

		// Pinning is the deliberate act that says "this is worth a phone buzzing at 100 km/h"
		// (§17.1), so it belongs to whoever runs the thread and to nobody else.
		if (!access.CanModerate)
		{
			return Problem(
				statusCode: StatusCodes.Status403Forbidden,
				title: "Not yours to pin",
				detail: "The noticeboard belongs to the organiser and leaders of an adventure, and to "
					+ "the owner of a route.");
		}

		if (access.ReadOnly)
		{
			return RideContentPermissions.AsResult(access.Refusal!);
		}

		if (request.Pinned && !comment.IsPinned)
		{
			int alreadyPinned = await InThread(database, access).CountAsync(row => row.IsPinned);

			if (alreadyPinned >= limits.MaxPinned)
			{
				return Problem(
					statusCode: StatusCodes.Status409Conflict,
					title: "Too many pinned posts",
					detail: $"A thread keeps at most {limits.MaxPinned} pinned posts. Pinning is the one thing " +
					"that still reaches a phone mid-trip, so a noticeboard of twenty is not a " +
					"noticeboard — unpin something first.");
			}
		}

		comment.IsPinned = request.Pinned;
		comment.PinnedByUserId = request.Pinned ? userId : null;
		comment.PinnedUtc = request.Pinned ? clock.GetUtcNow() : null;

		await database.SaveChangesAsync();

		await hub.Clients
			.Group(access.HubGroup)
			.CommentPinChanged(comment.Id, comment.IsPinned);

		return Ok(await DescribeAsync(database, comment.Id, limits, userId, clock.GetUtcNow()));
	}

	/// <summary>
	/// Every post in one thread, whichever kind it is.
	/// <para>
	/// Written once so that "which thread?" has one answer in this file. Both columns are compared
	/// even though one of them is always null — <c>track_id IS NULL</c> is what stops an
	/// adventure's thread from being the union of itself and every route comment ever written, and
	/// leaving it out would be a filter that looked complete and was not.
	/// </para>
	/// </summary>
	/// <param name="database">The one context.</param>
	/// <param name="access">Which thread.</param>
	private static IQueryable<RideComment> InThread(DlrDbContext database, ThreadAccess access) =>
		database
			.Set<RideComment>()
			.Where(comment =>
				comment.GroupRideId == access.GroupRideId && comment.TrackId == access.TrackId);

	/// <summary>
	/// The thread as a rate-limit bucket key. Prefixed by kind, so a route and an adventure that
	/// happen to share an identifier do not share an allowance.
	/// </summary>
	/// <param name="access">Which thread.</param>
	private static string ThreadKey(ThreadAccess access) =>
		access.GroupRideId is { } rideId ? $"ride:{rideId}" : $"track:{access.TrackId}";

	/// <summary>
	/// A poll's shape, checked before anything is written (§17.5).
	/// </summary>
	private IActionResult? ValidatePoll(PollSpec spec, string? question, CommentOptions limits)
	{
		// The question is the comment's body, so a poll with no body is a poll with no question.
		// There is no second field to fall back on, which is the point of §17.5's arrangement.
		if (question is null)
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "A poll needs a question",
				detail: "The comment's body is the question.");
		}

		List<string> options = [.. spec.Options.Select(option => option.Trim())];

		// Two is the floor because one option is not a question, and a poll of one would render
		// as a button that does nothing.
		if (options.Count < 2 || options.Count > limits.MaxPollOptions)
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "Wrong number of options",
				detail: $"A poll offers between 2 and {limits.MaxPollOptions} options.");
		}

		if (options.Any(string.IsNullOrEmpty))
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "An option is blank",
				detail: "Every option needs a label.");
		}

		if (options.Any(option => option.Length > limits.PollOptionMaxChars))
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "An option is too long",
				detail: $"An option label is at most {limits.PollOptionMaxChars} characters.");
		}

		return options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count
			? Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "Duplicate options",
				detail: "Two options with the same label cannot be told apart in the results.")
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
			comment.TrackId,
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
