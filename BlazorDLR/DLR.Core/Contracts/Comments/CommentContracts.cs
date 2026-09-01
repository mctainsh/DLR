namespace DLR.Core.Contracts.Comments;

/// <summary>What a comment is (§17.2).</summary>
public enum CommentKindDto
{
	/// <summary>Text, a photograph, or both.</summary>
	Text = 0,

	/// <summary>A poll (§17.5).</summary>
	Poll = 1,
}

/// <summary>
/// Posting to a ride's thread (§17.2, §17.3).
/// </summary>
/// <param name="ClientGuid">
/// Generated on the device before it had a server, so a drained outbox cannot duplicate the post
/// (§4.4). The unique index is what enforces it; this is just where it arrives.
/// </param>
/// <param name="Body">The text, or null when the post is a photograph on its own.</param>
/// <param name="PhotoId">An uploaded image (§16.4), or null.</param>
/// <param name="CreatedUtc">
/// When the rider wrote it, if that is not now. <strong>Clamped server-side</strong> so it can
/// never exceed receipt time - a client clock set to next year must not pin a comment to the top
/// of every thread forever (§17.3).
/// </param>
/// <param name="Poll">
/// Makes this post a poll (§17.5). One posting path for both kinds, so a poll inherits idempotency,
/// the caps, the rate limit and the content switches rather than needing its own copy of each.
/// </param>
public sealed record PostCommentRequest(
	Guid ClientGuid,
	string? Body = null,
	Guid? PhotoId = null,
	DateTimeOffset? CreatedUtc = null,
	PollSpec? Poll = null);

/// <summary>Editing one's own post, inside the window (§17.2).</summary>
/// <param name="Body">The replacement text.</param>
public sealed record EditCommentRequest(string? Body);

/// <summary>Pinning or unpinning (§17.6).</summary>
/// <param name="Pinned">Whether it should be pinned.</param>
public sealed record PinCommentRequest(bool Pinned);

/// <summary>
/// A post as the thread shows it (§17.2, §17.3).
/// </summary>
/// <param name="Id">Which comment.</param>
/// <param name="GroupRideId">
/// Which adventure's thread, or null when this post belongs to a shared route's thread instead.
/// <strong>Exactly one of this and <paramref name="TrackId"/> is set</strong> - a comment hangs off
/// one subject (§17.2), and the two threads are the same conversation machinery pointed at a
/// different one.
/// </param>
/// <param name="TrackId">Which shared route's thread, or null when it is an adventure's (§6.2).</param>
/// <param name="AuthorId">Who wrote it.</param>
/// <param name="AuthorUserName">
/// Their handle. Denormalised safely because §7.2 makes it immutable - there is no invalidation
/// to get wrong.
/// </param>
/// <param name="Kind">Text or poll.</param>
/// <param name="Body">The text. <strong>Plain text</strong> - never HTML, never Markdown, never linkified.</param>
/// <param name="PhotoId">The attached image, fetched separately.</param>
/// <param name="IsPinned">Whether it sits at the top of the thread.</param>
/// <param name="CreatedUtc">When the rider wrote it, clamped to receipt time.</param>
/// <param name="PostedUtc">When the server received it. <strong>This is the sort key.</strong></param>
/// <param name="EditedUtc">When it was last edited, or null.</param>
/// <param name="AuthoredEarlier">
/// Whether <paramref name="CreatedUtc"/> and <paramref name="PostedUtc"/> differ by more than
/// <c>Comments:StaleAuthorMinutes</c>, so the UI can show both - <em>"14:32 - written 10:04"</em>.
/// The threshold is decided here rather than in each client, because it is configuration (§14.5)
/// and three clients disagreeing about what counts as stale would be three different threads.
/// </param>
/// <param name="Reactions">
/// Aggregate counts plus the caller's own (§17.4). Never a list of who - that is a separate
/// question with its own answer, and carrying it on every post in a thread would be the bulk of
/// the payload for something almost nothing renders.
/// </param>
/// <param name="Poll">The poll, when <paramref name="Kind"/> is <see cref="CommentKindDto.Poll"/>.</param>
public sealed record CommentDto(
	Guid Id,
	Guid? GroupRideId,
	Guid? TrackId,
	Guid AuthorId,
	string AuthorUserName,
	CommentKindDto Kind,
	string? Body,
	Guid? PhotoId,
	bool IsPinned,
	DateTimeOffset CreatedUtc,
	DateTimeOffset PostedUtc,
	DateTimeOffset? EditedUtc,
	bool AuthoredEarlier,
	ReactionCounts? Reactions = null,
	PollResults? Poll = null);

/// <summary>
/// One page of a thread (§17.8).
/// <para>
/// Pinned posts come back in their own list rather than merged into the page, because they render
/// at the top <em>regardless of age</em> - a pinned post from three days ago belongs above today's
/// chatter, and a single ordered page cannot express that without re-sorting on the client.
/// </para>
/// </summary>
/// <param name="Pinned">The ride's noticeboard, newest pin first. Only on the first page.</param>
/// <param name="Comments">The page itself, newest first.</param>
/// <param name="NextCursor">
/// Pass back as <c>?cursor=</c> for the next page, or null when the thread is exhausted.
/// </param>
public sealed record CommentPage(
	IReadOnlyList<CommentDto> Pinned,
	IReadOnlyList<CommentDto> Comments,
	string? NextCursor);
