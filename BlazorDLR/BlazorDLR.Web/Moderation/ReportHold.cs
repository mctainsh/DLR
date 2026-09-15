using DLR.Server.Data;
using DLR.Server.Data.Comments;
using DLR.Server.Data.Markers;
using DLR.Server.Data.Moderation;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Moderation;

/// <summary>
/// Content that has been reported and not yet reviewed (§17.7, §10.2).
/// <para>
/// <strong>A report takes the content down immediately, before anybody has looked at it.</strong>
/// That is what App Store guideline 1.2 means by removing objectionable content from the feed
/// instantly, and it is the half a queue alone does not satisfy: a report that only opened a ticket
/// leaves the thing that was reported on screen for everybody until an operator wakes up.
/// </para>
/// <para>
/// <strong>The cost of that is real and is accepted.</strong> One report from one account hides a
/// post from everybody, so anybody can silence anybody for as long as the queue takes - which is
/// why the operator's queue carries a one-tap restore (see <c>AdminModerationController</c>) and
/// why the terms commit to reviewing within 24 hours. The alternative, leaving reported content up
/// until it is judged, is the arrangement that was rejected.
/// </para>
/// <para>
/// <strong>Derived, not a column.</strong> "Held" is exactly "has an unresolved report", so there
/// is no flag on <c>ride_comment</c> or <c>marker</c> to get out of step with the report table -
/// and resolving a report is the whole of restoring the content. The predicate is
/// <c>target_kind = @kind AND resolved_utc IS NULL</c>, which is what the partial
/// <c>ix_content_report_unresolved</c> is for; it stays small because resolved rows fall out of it.
/// </para>
/// <para>
/// <strong>Where the edge is.</strong> A photograph attached to held content is covered - see
/// <see cref="IsAttachedToHeldContentAsync"/>, which the serve path asks. What is not covered is
/// photographs in general: <c>GET /api/v1/photos/{id}</c> has no visibility rule of its own, so a
/// blocked author's pictures stay fetchable by id. That is pre-existing and wider than moderation.
/// </para>
/// </summary>
public static class ReportHold
{
	/// <summary>
	/// The posts of <paramref name="comments"/> that are not being held.
	/// <para>
	/// The filter rather than the ids, so a caller composes instead of remembering. That is the
	/// lesson <see cref="BlockList"/> states and did not get to apply to itself: a bare set of ids
	/// leaves every read path writing its own <c>!held.Contains(...)</c>, which is how the fourth
	/// one ends up without it - and being typed, it also means a comment query cannot be filtered
	/// with the marker kind by mistake.
	/// </para>
	/// <para>
	/// Composed into the caller's query rather than materialised: filtering after <c>Take</c> would
	/// return short pages whose length leaks how many were removed, which is the reason the block
	/// filter beside it gives.
	/// </para>
	/// </summary>
	/// <param name="database">The context the caller's query is being built on.</param>
	/// <param name="comments">The query to filter.</param>
	public static IQueryable<RideComment> Unheld(DlrDbContext database, IQueryable<RideComment> comments)
	{
		IQueryable<Guid> held = HeldIds(database, ReportTargetKind.Comment);

		return comments.Where(comment => !held.Contains(comment.Id));
	}

	/// <summary>The markers of <paramref name="markers"/> that are not being held. See <see cref="Unheld(DlrDbContext, IQueryable{RideComment})"/>.</summary>
	/// <param name="database">The context the caller's query is being built on.</param>
	/// <param name="markers">The query to filter.</param>
	public static IQueryable<Marker> Unheld(DlrDbContext database, IQueryable<Marker> markers)
	{
		IQueryable<Guid> held = HeldIds(database, ReportTargetKind.Marker);

		return markers.Where(marker => !held.Contains(marker.Id));
	}

	/// <summary>
	/// Whether one piece of content is being held.
	/// <para>
	/// For the paths that resolve a single row rather than filter a query - the mutation endpoints.
	/// A held post that could still be edited would be back on every open client the moment its
	/// author saved it, because an edit broadcasts and clients upsert what they are sent.
	/// </para>
	/// </summary>
	/// <param name="database">The context.</param>
	/// <param name="kind">Comment or marker.</param>
	/// <param name="targetId">Which one.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public static Task<bool> IsHeldAsync(
		DlrDbContext database,
		ReportTargetKind kind,
		Guid targetId,
		CancellationToken cancellationToken = default) =>
		database
			.Set<ContentReport>()
			.AnyAsync(
				report => report.TargetKind == kind
					&& report.TargetId == targetId
					&& report.ResolvedUtc == null,
				cancellationToken);

	/// <summary>
	/// Whether a photograph belongs to a comment or a marker that is being held.
	/// <para>
	/// Holding content removes every reference to its photograph, which is not the same as removing
	/// the photograph - <c>GET /api/v1/photos/{id}</c> asks only that the caller is signed in, so an
	/// id noted before the report would still fetch the bytes. For a photo report that is the whole
	/// of what was objected to, so the serve path asks this.
	/// </para>
	/// <para>
	/// <strong>A sub-query, not two awaited checks, because the serve path is the hottest read in
	/// the product.</strong> Every avatar, ride cover, comment photograph and marker photograph goes
	/// through it, and it already loads the <c>Photo</c> row - so this composes into that load and
	/// costs no round trip of its own. Written as two awaited <c>AnyAsync</c> calls it tripled the
	/// queries behind every image in the app.
	/// </para>
	/// <para>
	/// Both halves start from <see cref="HeldIds"/>, so the common case - nothing reported anywhere -
	/// is the partial <c>ix_content_report_unresolved</c> returning nothing, and the photo-id
	/// lookups never run.
	/// </para>
	/// </summary>
	/// <param name="database">The context the caller's query is being built on.</param>
	public static IQueryable<Guid> HeldPhotoIds(DlrDbContext database)
	{
		IQueryable<Guid> heldComments = HeldIds(database, ReportTargetKind.Comment);
		IQueryable<Guid> heldMarkers = HeldIds(database, ReportTargetKind.Marker);

		return database
			.Set<RideComment>()
			.Where(comment => comment.PhotoId != null && heldComments.Contains(comment.Id))
			.Select(comment => comment.PhotoId!.Value)
			.Concat(database
				.Set<Marker>()
				.Where(marker => marker.PhotoId != null && heldMarkers.Contains(marker.Id))
				.Select(marker => marker.PhotoId!.Value));
	}

	private static IQueryable<Guid> HeldIds(DlrDbContext database, ReportTargetKind kind) =>
		database
			.Set<ContentReport>()
			.Where(report => report.TargetKind == kind && report.ResolvedUtc == null)
			.Select(report => report.TargetId);
}
