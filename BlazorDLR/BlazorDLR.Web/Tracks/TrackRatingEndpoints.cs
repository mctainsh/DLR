using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.Server.Data;
using DLR.Server.Data.Tracks;
using DLR.Server.Identity;
using DLR.Server.Moderation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tracks;

/// <summary>Route names for the rating surface, quoted by the tests.</summary>
public static class TrackRatingEndpoints
{
	/// <summary>Route name for reading how a route stands.</summary>
	public const string ReadRatingRouteName = "TrackRating";

	/// <summary>Route name for rating a route.</summary>
	public const string RateRouteName = "RateTrack";

	/// <summary>Route name for withdrawing a rating.</summary>
	public const string ClearRatingRouteName = "ClearTrackRating";
}

/// <summary>
/// Stars on a shared route (§6.2).
/// <para>
/// <strong>Anyone signed in who can see the route can rate it</strong>, which is the same audience
/// that can post to its thread and for the same reason: a route on the browse list has been put in
/// front of every rider on the service, and a score only its owner's friends could give would be a
/// score nobody should read. §7.8's ladder still applies to the write - a brand-new account cannot
/// rate, exactly as it cannot share a route or post a comment - and that is the policy attribute
/// rather than anything in the bodies below.
/// </para>
/// <para>
/// The scale, the "one rating per rider" rule and the storage all belong to
/// <see cref="TrackRating"/> and <see cref="TrackRatings"/>; this file is the three verbs. Reading
/// is <c>GET</c>, setting is an idempotent <c>PUT</c> - rating again replaces rather than
/// accumulates, so there is nothing for a <c>POST</c> to mean - and withdrawing is
/// <c>DELETE</c>, never a zero, because a stored zero would count as the worst possible score
/// against every rider who changed their mind.
/// </para>
/// </summary>
[ApiController]
[Authorize]
public sealed class TrackRatingController : ControllerBase
{
	[HttpGet("/api/v1/tracks/{id:guid}/rating", Name = TrackRatingEndpoints.ReadRatingRouteName)]
	[EndpointSummary("How a shared route stands: the average, how many rated it, and the caller's own.")]
	public async Task<IActionResult> ReadAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		CancellationToken cancellationToken)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		if (!await VisibleAsync(database, id, userId, cancellationToken))
		{
			return NotFound();
		}

		return Ok(await TrackRatingReader.SummariseAsync(database, id, userId, cancellationToken));
	}

	[HttpPut("/api/v1/tracks/{id:guid}/rating", Name = TrackRatingEndpoints.RateRouteName)]
	[Authorize(Policy = AuthorizationPolicies.NotRestricted)]
	[EndpointSummary("Rates a shared route one to five stars, replacing the caller's previous rating.")]
	public async Task<IActionResult> RateAsync(
		[FromRoute] Guid id,
		[FromBody] RateTrackRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] TimeProvider clock,
		CancellationToken cancellationToken)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		if (!TrackRatings.IsValid(request.Stars))
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "Not a rating",
				detail: $"A rating is a whole number of stars from {TrackRatings.MinStars} to "
					+ $"{TrackRatings.MaxStars}. To withdraw one, delete it rather than sending a nought.");
		}

		if (!await VisibleAsync(database, id, userId, cancellationToken))
		{
			return NotFound();
		}

		TrackRating? existing = await database
			.Set<TrackRating>()
			.SingleOrDefaultAsync(row => row.TrackId == id && row.UserId == userId, cancellationToken);

		DateTimeOffset now = clock.GetUtcNow();

		if (existing is null)
		{
			database.Add(new TrackRating
			{
				TrackId = id,
				UserId = userId,
				Stars = (short)request.Stars,
				CreatedUtc = now,
			});
		}
		else
		{
			// Updated in place. The primary key already guarantees one row per rider per route, but
			// writing it this way is what makes changing your mind a single row rather than a
			// delete and an insert racing each other - CommentReaction's reasoning exactly.
			//
			// Stamped only when the number actually moves. Re-sending the same rating is what a
			// drained outbox does, and it must not read afterwards as though the rider went back
			// and reconsidered.
			if (existing.Stars != (short)request.Stars)
			{
				existing.Stars = (short)request.Stars;
				existing.UpdatedUtc = now;
			}
		}

		await database.SaveChangesAsync(cancellationToken);

		return Ok(await TrackRatingReader.SummariseAsync(database, id, userId, cancellationToken));
	}

	[HttpDelete("/api/v1/tracks/{id:guid}/rating", Name = TrackRatingEndpoints.ClearRatingRouteName)]
	[EndpointSummary("Withdraws the caller's rating.")]
	public async Task<IActionResult> ClearAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		CancellationToken cancellationToken)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		if (!await VisibleAsync(database, id, userId, cancellationToken))
		{
			return NotFound();
		}

		TrackRating? existing = await database
			.Set<TrackRating>()
			.SingleOrDefaultAsync(row => row.TrackId == id && row.UserId == userId, cancellationToken);

		// Withdrawing a rating that was never given is success, not a 404. A rider tapping their
		// own star again to clear it does not need to be told they had not rated it - and a phone
		// draining an outbox will send this twice.
		if (existing is not null)
		{
			database.Remove(existing);

			await database.SaveChangesAsync(cancellationToken);
		}

		return Ok(await TrackRatingReader.SummariseAsync(database, id, userId, cancellationToken));
	}

	/// <summary>
	/// Whether this caller may see this route at all, which is the whole of the permission check.
	/// <para>
	/// The same three rules <see cref="Comments.CommentThreadAccess.ForTrackAsync"/> applies, and
	/// deliberately the same answer for all three failures: a route that does not exist, one that
	/// is not shared, and one whose owner the caller has blocked are all <c>404</c>. A
	/// distinguishable refusal would turn a track id - which travels in links - into an oracle for
	/// which of them are real.
	/// </para>
	/// <para>
	/// The owner is included. Rating your own route is a strange thing to do and it is not this
	/// endpoint's business to forbid it: the alternative is a rule nobody asked for, and the
	/// average is shown beside the count precisely so that a score standing on one vote reads as
	/// one vote.
	/// </para>
	/// </summary>
	private static async Task<bool> VisibleAsync(
		DlrDbContext database,
		Guid trackId,
		Guid userId,
		CancellationToken cancellationToken)
	{
		Guid? ownerId = await database
			.Set<Track>()
			.AsNoTracking()
			.Where(track =>
				track.Id == trackId
				&& (track.Visibility == TrackVisibility.Public || track.OwnerId == userId))
			.Select(track => (Guid?)track.OwnerId)
			.SingleOrDefaultAsync(cancellationToken);

		if (ownerId is not { } owner)
		{
			return false;
		}

		return owner == userId
			|| !(await BlockList.HiddenFromAsync(database, userId, cancellationToken)).Contains(owner);
	}
}

/// <summary>
/// Reading rating tallies (§6.2).
/// <para>
/// One place, on <see cref="Comments.CommentReactions"/>'s reasoning: the three endpoints above,
/// the browse list and the detail page all need the same shape, and the one that drifted would be
/// whichever nothing looks at directly.
/// </para>
/// </summary>
public static class TrackRatingReader
{
	/// <summary>How one route stands.</summary>
	/// <param name="database">The one context.</param>
	/// <param name="trackId">Which route.</param>
	/// <param name="forUser">Whose own rating to report, or null.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public static async Task<TrackRatingSummary> SummariseAsync(
		DlrDbContext database,
		Guid trackId,
		Guid? forUser,
		CancellationToken cancellationToken = default)
	{
		IReadOnlyDictionary<Guid, TrackRatingSummary> all =
			await SummariseAsync(database, [trackId], forUser, cancellationToken);

		return all.TryGetValue(trackId, out TrackRatingSummary? summary)
			? summary
			: TrackRatingSummary.None;
	}

	/// <summary>
	/// How a whole page of routes stands, in one round trip.
	/// <para>
	/// A page at a time rather than a query per row, exactly as a thread page hydrates its
	/// reactions: twenty round trips to draw one browse page is the N+1 that makes a fast feature
	/// feel broken.
	/// </para>
	/// <para>
	/// Blocked accounts are deliberately <em>not</em> excluded from the tally, which is the one
	/// place this differs from a reaction count. A block hides what somebody wrote - their
	/// comments, their reactions, their votes are all authored content with a name on it (§17.7) -
	/// but a rating is anonymous by construction: nothing anywhere says who gave a route three
	/// stars. Filtering it would make one rider's average differ from another's for a number they
	/// are both being asked to trust, and would leak, in the difference, that a blocked rider had
	/// rated this route.
	/// </para>
	/// </summary>
	/// <param name="database">The one context.</param>
	/// <param name="trackIds">The page.</param>
	/// <param name="forUser">Whose own ratings to report, or null.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public static async Task<IReadOnlyDictionary<Guid, TrackRatingSummary>> SummariseAsync(
		DlrDbContext database,
		IReadOnlyCollection<Guid> trackIds,
		Guid? forUser,
		CancellationToken cancellationToken = default)
	{
		if (trackIds.Count == 0)
		{
			return new Dictionary<Guid, TrackRatingSummary>();
		}

		// Grouped in the database. Pulling every rating back to average them here would move a
		// route's whole audience across the wire to produce one number.
		var tallies = await database
			.Set<TrackRating>()
			.AsNoTracking()
			.Where(rating => trackIds.Contains(rating.TrackId))
			.GroupBy(rating => rating.TrackId)
			.Select(group => new
			{
				TrackId = group.Key,
				Average = group.Average(rating => (double)rating.Stars),
				Count = group.Count(),
			})
			.ToListAsync(cancellationToken);

		Dictionary<Guid, int> mine = [];

		if (forUser is { } userId)
		{
			mine = await database
				.Set<TrackRating>()
				.AsNoTracking()
				.Where(rating => trackIds.Contains(rating.TrackId) && rating.UserId == userId)
				.ToDictionaryAsync(
					rating => rating.TrackId,
					rating => (int)rating.Stars,
					cancellationToken);
		}

		Dictionary<Guid, TrackRatingSummary> result = [];

		foreach (Guid trackId in trackIds)
		{
			var tally = tallies.Find(row => row.TrackId == trackId);

			result[trackId] = new TrackRatingSummary(
				tally?.Average,
				tally?.Count ?? 0,
				mine.TryGetValue(trackId, out int own) ? own : null);
		}

		return result;
	}
}
