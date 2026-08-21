using DLR.Server.Data.Identity;
using DLR.Server.Data.Photos;
using DLR.Server.Data.Rides;
using DLR.Server.Data.Tracks;

namespace DLR.Server.Data.Comments;

/// <summary>What a comment is (§17.2).</summary>
public enum RideCommentKind
{
	/// <summary>Text, a photograph, or both.</summary>
	Text = 0,

	/// <summary>
	/// A poll. <strong>A kind of comment, not a separate entity</strong> (§17.5) — so it inherits
	/// threading, pinning, reactions, permissions and moderation without any of them being written
	/// twice. The body carries the question.
	/// </summary>
	Poll = 1,
}

/// <summary>
/// One post in a thread (§17.2, §17.9).
/// <para>
/// <strong>One table for both threads that exist.</strong> An adventure has a thread (§17.1) and,
/// since routes went on a public list, so does a shared route (§6.2) — and they are the same
/// conversation: the same plain-text body, the same photograph, the same six reactions, the same
/// polls, the same edit window, the same report and block machinery. A second table would have
/// been a second copy of every one of those, and the copy that drifted would be whichever one had
/// fewer tests. <see cref="GroupRideId"/> and <see cref="TrackId"/> say which subject this post
/// hangs off; exactly one of them is set, and the database enforces that rather than trusting
/// every write path to remember.
/// </para>
/// <para>
/// <strong>Two timestamps, and they are not redundant.</strong> <see cref="CreatedUtc"/> is when
/// the rider wrote it; <see cref="PostedUtc"/> is when the server received it. A comment composed
/// at 10:04 in a valley with no signal and drained out of the outbox at 14:32 has both, and the
/// thread orders on the second — dropping four-hour-old text into the middle of a conversation that
/// has moved on is confusing, and ordering on a client-supplied time makes the thread only as
/// trustworthy as the least accurate clock in the group, or the most malicious one (§17.3).
/// </para>
/// <para>
/// The body is <strong>plain text, always</strong>. Never HTML, never Markdown, never linkified,
/// and no link preview is ever fetched — a tapable link inside a trusted ride thread is a phishing
/// surface, and fetching a preview server-side is the same SSRF hole §16.6 refused for GPX
/// <c>&lt;link&gt;</c> elements.
/// </para>
/// </summary>
public sealed class RideComment
{
	/// <summary>Row identifier.</summary>
	public Guid Id { get; set; }

	/// <summary>Which adventure's thread, or null when this post belongs to a route's (§17.1).</summary>
	public Guid? GroupRideId { get; set; }

	/// <summary>
	/// Which shared route's thread, or null when this post belongs to an adventure's (§6.2).
	/// <para>
	/// A route's thread has a wider audience than an adventure's on purpose: an adventure's is
	/// visible to the people in it and nobody else, while a route that has been put in front of
	/// every rider on the service is read and answered by any of them. That difference lives in
	/// the access check, not in the shape of a post.
	/// </para>
	/// </summary>
	public Guid? TrackId { get; set; }

	/// <summary>Who wrote it.</summary>
	public Guid AuthorId { get; set; }

	/// <summary>
	/// The identifier the client generated before it had a server (§4.4, §17.3).
	/// <para>
	/// What makes the post idempotent. A phone draining its outbox over a flaky connection will
	/// re-send a comment it never saw acknowledged, and without this the thread grows a duplicate
	/// every time somebody rides through a tunnel — the same reasoning as a track upload (§15).
	/// </para>
	/// </summary>
	public Guid ClientGuid { get; set; }

	/// <summary>Text or poll.</summary>
	public RideCommentKind Kind { get; set; }

	/// <summary>The text, or null when the post is a photograph on its own.</summary>
	public string? Body { get; set; }

	/// <summary>The attached image (§16.4), or null.</summary>
	public Guid? PhotoId { get; set; }

	/// <summary>Whether it sits at the top of the thread (§17.6).</summary>
	public bool IsPinned { get; set; }

	/// <summary>Which organiser or leader pinned it.</summary>
	public Guid? PinnedByUserId { get; set; }

	/// <summary>When it was pinned.</summary>
	public DateTimeOffset? PinnedUtc { get; set; }

	/// <summary>
	/// When the rider wrote it, <strong>clamped so it can never exceed <see cref="PostedUtc"/></strong>.
	/// A client clock set to next year must not pin a comment to the top of every thread forever.
	/// </summary>
	public DateTimeOffset CreatedUtc { get; set; }

	/// <summary>When the server received it. The thread's sort key (§17.3).</summary>
	public DateTimeOffset PostedUtc { get; set; }

	/// <summary>
	/// When it was last edited, or null. Shown by the UI — an edit that left no trace would let
	/// somebody rewrite what a poll was asking after people had voted on it (§17.2).
	/// </summary>
	public DateTimeOffset? EditedUtc { get; set; }

	/// <summary>The ride, for cascade deletion.</summary>
	public GroupRide? Ride { get; set; }

	/// <summary>The route, for cascade deletion.</summary>
	public Track? Track { get; set; }

	/// <summary>The author.</summary>
	public AppUser? Author { get; set; }

	/// <summary>The attached image.</summary>
	public Photo? Photo { get; set; }
}
