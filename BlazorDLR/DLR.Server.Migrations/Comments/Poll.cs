using DLR.Server.Data.Identity;

namespace DLR.Server.Data.Comments;

/// <summary>
/// A poll, hanging off the comment that <em>is</em> it (§17.5).
/// <para>
/// <strong>The primary key is the comment's id</strong>, not an identity of its own - a poll is
/// <c>Comment.Kind = Poll</c> with this record attached, not a parallel entity. That is worth being
/// deliberate about: it means polls inherit threading, pinning, reactions, permissions, reporting,
/// deletion, export and the whole realtime path without a line of new code for any of them. A
/// separate table would have needed its own copy of all of it.
/// </para>
/// <para>
/// The question is the comment's body. There is no second place to put text, and so no way for the
/// two to disagree.
/// </para>
/// </summary>
public sealed class Poll
{
	/// <summary>The comment this poll is. Also its primary key.</summary>
	public Guid CommentId { get; set; }

	/// <summary>Whether a voter may pick more than one option, chosen at creation.</summary>
	public bool AllowMultiple { get; set; }

	/// <summary>
	/// When it closes on its own, or null for one that stays open until somebody closes it.
	/// <para>
	/// <strong>Evaluated on read, never swept.</strong> A background job that flipped a flag would
	/// leave a window in which an elapsed poll still accepted votes - as wide as the job's interval,
	/// and widest exactly when the job is behind. Comparing against the clock at vote time has no
	/// such window and no job to fail.
	/// </para>
	/// </summary>
	public DateTimeOffset? ClosesUtc { get; set; }

	/// <summary>When somebody closed it early, or null.</summary>
	public DateTimeOffset? ClosedUtc { get; set; }

	/// <summary>Which author or organiser closed it.</summary>
	public Guid? ClosedByUserId { get; set; }

	/// <summary>The comment.</summary>
	public RideComment? Comment { get; set; }

	/// <summary>Who closed it.</summary>
	public AppUser? ClosedBy { get; set; }

	/// <summary>What can be voted for.</summary>
	public ICollection<PollOption> Options { get; set; } = [];

	/// <summary>
	/// Whether the poll is shut at the given instant - closed by hand, or past its own deadline.
	/// </summary>
	/// <param name="now">The server clock.</param>
	public bool IsClosed(DateTimeOffset now) =>
		ClosedUtc is not null || (ClosesUtc is { } closes && now >= closes);
}

/// <summary>One thing that can be voted for (§17.5).</summary>
public sealed class PollOption
{
	/// <summary>Row identifier. Its own, because a vote points at an option rather than a position.</summary>
	public Guid Id { get; set; }

	/// <summary>Which poll.</summary>
	public Guid CommentId { get; set; }

	/// <summary>Where it sits in the list, as the author wrote it.</summary>
	public int Ordinal { get; set; }

	/// <summary>The label. Plain text, like everything else in the thread (§17.2).</summary>
	public string Text { get; set; } = string.Empty;

	/// <summary>The poll, for cascade deletion.</summary>
	public Poll? Poll { get; set; }

	/// <summary>Who voted for it.</summary>
	public ICollection<PollVote> Votes { get; set; } = [];
}

/// <summary>
/// One person voting for one option (§17.5).
/// <para>
/// <strong>Attributed, and there is no anonymous mode.</strong> The question people actually ask is
/// <em>"who's coming on Saturday?"</em>, and an anonymous tally answers a different and less useful
/// one. It also means a vote needs no separate privacy story - it is visible to exactly the
/// audience the ride already has.
/// </para>
/// </summary>
public sealed class PollVote
{
	/// <summary>Which option.</summary>
	public Guid PollOptionId { get; set; }

	/// <summary>Who voted.</summary>
	public Guid UserId { get; set; }

	/// <summary>When.</summary>
	public DateTimeOffset CreatedUtc { get; set; }

	/// <summary>The option, for cascade deletion.</summary>
	public PollOption? Option { get; set; }

	/// <summary>The account, for cascade deletion (§10.1).</summary>
	public AppUser? User { get; set; }
}
