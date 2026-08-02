namespace DLR.Core.Contracts.Comments;

/// <summary>
/// A comment's reactions, as the wire carries them (§17.4).
/// <para>
/// <strong>Aggregated, never enumerated.</strong> A thread of two hundred posts in a ride of twelve
/// would otherwise carry a row per person per reaction, most of which nothing renders. Who reacted
/// is a question with its own answer when somebody asks it; the thread only needs the tally and
/// whether the reader is in it.
/// </para>
/// </summary>
/// <param name="Counts">Reaction key onto how many people chose it. Keys with no takers are absent.</param>
/// <param name="Mine">The caller's own reaction, or null if they have not reacted.</param>
public sealed record ReactionCounts(IReadOnlyDictionary<string, int> Counts, string? Mine)
{
	/// <summary>A comment nobody has reacted to.</summary>
	public static ReactionCounts None { get; } = new(new Dictionary<string, int>(), null);
}

/// <summary>Setting or clearing one's own reaction (§17.4).</summary>
/// <param name="Reaction">A key from the fixed set, or <strong>null to clear</strong>.</param>
public sealed record SetReactionRequest(string? Reaction);

/// <summary>
/// The poll half of a post (§17.5, §17.8).
/// <para>
/// Attached to <see cref="PostCommentRequest"/> rather than sent to an endpoint of its own, because
/// <strong>a poll is a comment</strong>. One posting path means polls inherit idempotency, the
/// caps, the rate limit, the content switches and the archived-ride rule without any of them being
/// written twice — which is the entire argument of §17.5, applied to the API surface as well as to
/// the schema. The question is the comment's body, so there is no second place for text to live.
/// </para>
/// </summary>
/// <param name="Options">Two or more labels, in the order they should render.</param>
/// <param name="AllowMultiple">Whether a voter may pick more than one.</param>
/// <param name="ClosesUtc">
/// When it stops accepting votes on its own, or null. <strong>Evaluated on read</strong>, so no
/// background job has to run for a deadline to be honoured.
/// </param>
public sealed record PollSpec(
	IReadOnlyList<string> Options,
	bool AllowMultiple = false,
	DateTimeOffset? ClosesUtc = null);

/// <summary>Casting a vote (§17.5).</summary>
/// <param name="OptionIds">
/// What to vote for. Single-select replaces whatever was there; multi-select is the full set the
/// voter now holds, so an empty list clears their vote entirely.
/// </param>
public sealed record CastVoteRequest(IReadOnlyList<Guid> OptionIds);

/// <summary>One option and who chose it (§17.5).</summary>
/// <param name="Id">Which option.</param>
/// <param name="Ordinal">Where it sits in the list.</param>
/// <param name="Text">The label.</param>
/// <param name="Votes">How many chose it.</param>
/// <param name="Voters">
/// <strong>Who</strong> chose it. Votes are attributed and there is no anonymous mode, because the
/// question people actually ask is <em>"who's coming on Saturday?"</em> and an anonymous tally
/// answers a different, less useful one (§17.5).
/// </param>
public sealed record PollOptionResult(
	Guid Id,
	int Ordinal,
	string Text,
	int Votes,
	IReadOnlyList<PollVoter> Voters);

/// <summary>Somebody who voted.</summary>
/// <param name="UserId">Which rider.</param>
/// <param name="UserName">Their handle (§7.2).</param>
public sealed record PollVoter(Guid UserId, string UserName);

/// <summary>
/// A poll and where it has got to (§17.5).
/// <para>
/// Results are <strong>always visible, before and after voting</strong> — this is a ride's
/// noticeboard, not a secret ballot.
/// </para>
/// </summary>
/// <param name="CommentId">The comment this poll is.</param>
/// <param name="Question">The comment's body.</param>
/// <param name="AllowMultiple">Whether more than one option may be chosen.</param>
/// <param name="ClosesUtc">Its deadline, if it has one.</param>
/// <param name="ClosedUtc">When somebody closed it early.</param>
/// <param name="IsClosed">
/// Whether it is shut <em>now</em> — closed by hand or past its deadline. Computed against the
/// server clock on every read rather than stored, so an elapsed poll is closed the instant it
/// elapses rather than when a job next runs.
/// </param>
/// <param name="Options">The options, with their tallies and voters.</param>
/// <param name="MyOptionIds">What the caller voted for.</param>
public sealed record PollResults(
	Guid CommentId,
	string? Question,
	bool AllowMultiple,
	DateTimeOffset? ClosesUtc,
	DateTimeOffset? ClosedUtc,
	bool IsClosed,
	IReadOnlyList<PollOptionResult> Options,
	IReadOnlyList<Guid> MyOptionIds);
