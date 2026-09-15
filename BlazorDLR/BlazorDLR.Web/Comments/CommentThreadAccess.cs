using DLR.Server.Data;
using DLR.Server.Data.Comments;
using DLR.Server.Data.Rides;
using DLR.Server.Data.Tracks;
using DLR.Server.Hubs;
using DLR.Server.Moderation;
using DLR.Server.Rides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Comments;

/// <summary>
/// What one caller may do in one thread, whichever kind of thread it is (§17.2, §6.2).
/// <para>
/// <strong>This type exists so that <see cref="CommentController"/> does not grow a second copy
/// of itself.</strong> A shared route's thread and an adventure's are the same conversation -
/// same posts, same reactions, same polls, same edit window, same pinning, same reporting - and
/// they differ in exactly one place: who is allowed in and who runs it. Pulling that one
/// difference out into a resolver means the endpoints stay written once, and the two answers to
/// "may they?" sit side by side where they can be compared.
/// </para>
/// <para>
/// Every field is a decision already made. The endpoints ask this object questions; they never
/// re-read a ride or a track to second-guess it, because a permission check written twice is a
/// permission check that will be changed once.
/// </para>
/// </summary>
/// <param name="Exists">
/// Whether the thread is one the caller may know about at all. False is answered as <c>404</c>
/// and never as <c>403</c> - a distinguishable refusal turns a thread id into an oracle for what
/// exists and who is in it (§5.2).
/// </param>
/// <param name="GroupRideId">The adventure this thread belongs to, or null when it is a route's.</param>
/// <param name="TrackId">The route this thread belongs to, or null when it is an adventure's.</param>
/// <param name="HubGroup">Which SignalR group hears about changes here.</param>
/// <param name="CanPost">Whether the caller may add to it.</param>
/// <param name="CanAttachPhoto">
/// Whether the caller may attach an image. Separate from <paramref name="CanPost"/> because §5.8
/// lets an organiser switch off photographs while leaving text on, and "photos are off" is a more
/// useful answer than "your post is empty" to somebody who only attached a picture.
/// </param>
/// <param name="CanModerate">
/// Whether the caller may delete somebody else's post and pin things - the organiser of an
/// adventure, the owner of a route.
/// </param>
/// <param name="Refusal">
/// Why <paramref name="CanPost"/> is false, in words the caller can act on, or null when they may
/// post. Carried rather than re-derived: the endpoint that refuses is not the code that decided.
/// </param>
/// <param name="PhotoRefusal">Why <paramref name="CanAttachPhoto"/> is false, or null.</param>
public sealed record ThreadAccess(
	bool Exists,
	Guid? GroupRideId,
	Guid? TrackId,
	string HubGroup,
	bool CanPost,
	bool CanAttachPhoto,
	bool CanModerate,
	ProblemDetails? Refusal,
	ProblemDetails? PhotoRefusal)
{
	/// <summary>The answer for a thread the caller may not know exists.</summary>
	public static readonly ThreadAccess None =
		new(false, null, null, string.Empty, false, false, false, null, null);
}

/// <summary>
/// Resolves <see cref="ThreadAccess"/> for the two kinds of thread there are.
/// </summary>
public static class CommentThreadAccess
{
	/// <summary>
	/// An adventure's thread (§17.1, §17.6).
	/// <para>
	/// Membership is re-read on every request rather than trusted from the last one. A member who
	/// was removed keeps their posts and loses the thread, and this is where that second half
	/// happens.
	/// </para>
	/// </summary>
	/// <param name="database">The one context.</param>
	/// <param name="rideId">Which adventure.</param>
	/// <param name="userId">Who is asking.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public static async Task<ThreadAccess> ForRideAsync(
		DlrDbContext database,
		Guid rideId,
		Guid userId,
		CancellationToken cancellationToken = default)
	{
		GroupRideMember? membership = await database
			.Set<GroupRideMember>()
			.Include(member => member.Ride)
			.SingleOrDefaultAsync(
				member => member.GroupRideId == rideId && member.UserId == userId,
				cancellationToken);

		if (membership?.Ride is not { } ride)
			return ThreadAccess.None;

		bool mayComment = RideContentPermissions.Allows(ride, membership.Role, RideContent.Comment);
		bool mayPhoto = RideContentPermissions.Allows(ride, membership.Role, RideContent.Photo);

		return new ThreadAccess(
			Exists: true,
			GroupRideId: rideId,
			TrackId: null,
			HubGroup: RideHub.Group(rideId),
			CanPost: mayComment,
			CanAttachPhoto: mayPhoto,
			CanModerate: membership.Role is GroupRideRole.Owner or GroupRideRole.Leader,
			Refusal: mayComment ? null : RideContentPermissions.Describe(RideContent.Comment),
			PhotoRefusal: mayPhoto ? null : RideContentPermissions.Describe(RideContent.Photo));
	}

	/// <summary>
	/// A shared route's thread (§6.2).
	/// <para>
	/// <strong>Anyone signed in, because that is what sharing a route means.</strong> An
	/// adventure's thread is visible to the people the organiser admitted and nobody else; a route
	/// on the browse list has been put in front of every rider on the service on purpose, and a
	/// conversation about it that only its owner could join would be a comment box with one person
	/// in it. The permission ladder that still applies is §7.8's - a brand-new account cannot
	/// post, exactly as it cannot share a route - and that is carried by the endpoint's policy
	/// attribute rather than repeated here.
	/// </para>
	/// <para>
	/// Two things take a route's thread away again. Un-sharing it makes the whole route invisible
	/// to everybody but its owner, thread included - the posts survive, so re-sharing brings the
	/// conversation back rather than starting a new one. And blocking its owner takes it off the
	/// blocker's screen entirely (§17.7), for the same reason the browse list already drops their
	/// routes: a block that hid somebody's routes but left their comment thread reachable would be
	/// a block with a hole in it.
	/// </para>
	/// </summary>
	/// <param name="database">The one context.</param>
	/// <param name="trackId">Which route.</param>
	/// <param name="userId">Who is asking.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public static async Task<ThreadAccess> ForTrackAsync(
		DlrDbContext database,
		Guid trackId,
		Guid userId,
		CancellationToken cancellationToken = default)
	{
		var route = await database
			.Set<Track>()
			.AsNoTracking()
			.Where(track => track.Id == trackId)
			.Select(track => new { track.OwnerId, track.Visibility })
			.SingleOrDefaultAsync(cancellationToken);

		if (route is null)
			return ThreadAccess.None;

		bool mine = route.OwnerId == userId;

		// The owner reaches their own route's thread whatever its visibility, so that taking a
		// route back off the list does not lock them out of what was said about it. Everybody else
		// needs it to be on the list.
		if (!mine && route.Visibility != TrackVisibility.Public)
			return ThreadAccess.None;

		if (!mine && (await BlockList.HiddenFromAsync(database, userId, cancellationToken)).Contains(route.OwnerId))
			return ThreadAccess.None;

		return new ThreadAccess(
			Exists: true,
			GroupRideId: null,
			TrackId: trackId,
			HubGroup: RideHub.TrackGroup(trackId),
			CanPost: true,
			CanAttachPhoto: true,

			// The route's owner keeps their own thread, on the organiser's reasoning exactly: the
			// person who published the thing is the person who has to be able to take an abusive
			// post off it, and to pin the one worth reading first.
			CanModerate: mine,
			Refusal: null,
			PhotoRefusal: null);
	}

	/// <summary>
	/// The thread an existing comment belongs to, resolved for the caller.
	/// <para>
	/// One helper, because every write path - edit, delete, pin, react, vote, close a poll -
	/// begins by loading a comment and asking the same question about it. The comment comes back
	/// with the answer so the caller does not read the row twice.
	/// </para>
	/// </summary>
	/// <para>
	/// <strong>A held post is not there.</strong> Reported content is hidden from every read until
	/// an operator has judged it (§17.7), and this is the chokepoint every write path already goes
	/// through - so the hold belongs here rather than being remembered at each of them. It was not,
	/// briefly, and react, vote and close-poll all let a hidden post be acted on and broadcast.
	/// </para>
	/// <para>
	/// <strong>What the hold stops is content coming <em>back</em>.</strong> Editing, pinning,
	/// reacting and voting all put a hidden post in front of somebody again; deleting it does not,
	/// and an organiser removing an abusive comment is exactly what should happen while the report
	/// sits in the queue - see <c>ContentReport</c>, whose snapshot exists so that deletion does not
	/// destroy the evidence. <see cref="ForDeletionAsync"/> and <see cref="ForReportAsync"/> are the
	/// two callers that see past the hold, for that reason and for reporting twice.
	/// </para>
	/// </summary>
	/// <param name="database">The one context.</param>
	/// <param name="commentId">Which comment.</param>
	/// <param name="userId">Who is asking.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>
	/// The comment and what the caller may do in its thread. The comment is null - and the access
	/// is <see cref="ThreadAccess.None"/> - when it does not exist, when it is held, or when the
	/// caller may not know that it does, which are deliberately the same answer.
	/// </returns>
	public static Task<(RideComment? Comment, ThreadAccess Access)> ForCommentAsync(
		DlrDbContext database,
		Guid commentId,
		Guid userId,
		CancellationToken cancellationToken = default) =>
		ResolveAsync(database, commentId, userId, includeAuthor: false, seesHeld: false, cancellationToken);

	/// <summary>
	/// The same resolution for the one path that files a report (§17.7).
	/// <para>
	/// Two things differ and both are peculiar to reporting: it must see a post that is already
	/// held - somebody else reporting it first must not stop a second report - and it needs the
	/// author, because it snapshots the username. A named method rather than two flags on
	/// <see cref="ForCommentAsync"/>: the exception is one caller, and a boolean on the shared
	/// helper is a thing every other caller has to read past.
	/// </para>
	/// </summary>
	/// <param name="database">The one context.</param>
	/// <param name="commentId">Which comment.</param>
	/// <param name="userId">Who is asking.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public static Task<(RideComment? Comment, ThreadAccess Access)> ForReportAsync(
		DlrDbContext database,
		Guid commentId,
		Guid userId,
		CancellationToken cancellationToken = default) =>
		ResolveAsync(database, commentId, userId, includeAuthor: true, seesHeld: true, cancellationToken);

	/// <summary>
	/// The same resolution for deleting a post (§17.7).
	/// <para>
	/// Sees held content, because removing it is the outcome the hold is waiting for rather than
	/// something it should prevent. The organiser is the fastest moderator a thread has, and a
	/// delete that answered 404 because somebody else had already reported it would take that away
	/// at the one moment it matters.
	/// </para>
	/// </summary>
	/// <param name="database">The one context.</param>
	/// <param name="commentId">Which comment.</param>
	/// <param name="userId">Who is asking.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public static Task<(RideComment? Comment, ThreadAccess Access)> ForDeletionAsync(
		DlrDbContext database,
		Guid commentId,
		Guid userId,
		CancellationToken cancellationToken = default) =>
		ResolveAsync(database, commentId, userId, includeAuthor: false, seesHeld: true, cancellationToken);

	private static async Task<(RideComment? Comment, ThreadAccess Access)> ResolveAsync(
		DlrDbContext database,
		Guid commentId,
		Guid userId,
		bool includeAuthor,
		bool seesHeld,
		CancellationToken cancellationToken)
	{
		IQueryable<RideComment> comments = database.Set<RideComment>();

		if (includeAuthor)
		{
			comments = comments.Include(row => row.Author);
		}

		if (!seesHeld)
		{
			comments = ReportHold.Unheld(database, comments);
		}

		RideComment? comment = await comments
			.SingleOrDefaultAsync(row => row.Id == commentId, cancellationToken);

		if (comment is null)
			return (null, ThreadAccess.None);

		ThreadAccess access = comment.GroupRideId is { } rideId
			? await ForRideAsync(database, rideId, userId, cancellationToken)
			: comment.TrackId is { } trackId
				? await ForTrackAsync(database, trackId, userId, cancellationToken)
				: ThreadAccess.None;

		return access.Exists ? (comment, access) : (null, ThreadAccess.None);
	}
}
