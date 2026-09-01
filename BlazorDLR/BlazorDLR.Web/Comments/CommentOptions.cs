namespace DLR.Server.Comments;

/// <summary>The §17.7 caps and windows, as settings (§14.5).</summary>
public sealed class CommentOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "Comments";

	/// <summary>Body length.</summary>
	public int MaxChars { get; set; } = 2_000;

	/// <summary>
	/// How long the author may edit their own post.
	/// <para>
	/// Bounded rather than open-ended: a permanently editable thread lets somebody rewrite what a
	/// poll was actually asking after people have voted on it (§17.2). After the window, delete
	/// and repost - which leaves a visible gap, as it should.
	/// </para>
	/// </summary>
	public int EditWindowMinutes { get; set; } = 15;

	/// <summary>
	/// How far apart the authored and received times must be before the UI shows both (§17.3).
	/// </summary>
	public int StaleAuthorMinutes { get; set; } = 10;

	/// <summary>How many posts one ride's thread may hold.</summary>
	public int MaxPerRide { get; set; } = 2_000;

	/// <summary>Posts per hour, per user, per ride.</summary>
	public int PostsPerHourPerUserPerRide { get; set; } = 30;

	/// <summary>
	/// How many posts may be pinned at once (§17.6).
	/// <para>
	/// Three, because pinning is the one thing that still pushes to a phone during a live ride. A
	/// noticeboard of twenty is not a noticeboard, and the cap is what keeps the exception in §17.1
	/// narrow enough to stay defensible.
	/// </para>
	/// </summary>
	public int MaxPinned { get; set; } = 3;

	/// <summary>How many comments one page of the thread carries.</summary>
	public int PageSize { get; set; } = 50;

	/// <summary>
	/// How long reaction and vote changes are gathered before one hub message goes out (§17.4).
	/// <para>
	/// Reactions are the highest-frequency, lowest-value event in the product, and a count arriving
	/// three seconds late has cost nobody anything.
	/// </para>
	/// </summary>
	public int ReactionCoalesceSeconds { get; set; } = 3;

	/// <summary>How many options a poll may offer (§17.5).</summary>
	public int MaxPollOptions { get; set; } = 6;

	/// <summary>Option label length.</summary>
	public int PollOptionMaxChars { get; set; } = 80;

	/// <summary>How many polls one ride may hold.</summary>
	public int MaxPollsPerRide { get; set; } = 20;
}
