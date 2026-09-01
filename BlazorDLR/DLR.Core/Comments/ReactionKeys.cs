namespace DLR.Core.Comments;

/// <summary>
/// The fixed reaction set (§17.4).
/// <para>
/// Fixed for the same three reasons the marker icon set is (§16.2): it renders identically
/// everywhere, it needs no emoji-font negotiation across platforms, and it has no moderation
/// surface. A free-text reaction would be a second UGC field with none of §17.7's machinery
/// pointed at it.
/// </para>
/// <para>
/// <strong>Keys are strings and unknown ones are stored rather than rejected</strong>, exactly as
/// for icons. An app one version ahead sends a key this server has never heard of; storing it means
/// an older client renders a generic reaction instead of crashing, and the newer one still sees
/// what it sent. An enum ordinal would need a migration in lockstep with two app stores' release
/// cadences, which is not a thing that happens.
/// </para>
/// </summary>
public static class ReactionKeys
{
	/// <summary>Longest key the column accepts.</summary>
	public const int MaxLength = 32;

	/// <summary>The keys this version knows how to draw (§17.4).</summary>
	public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
	{
		"like", "love", "laugh", "wow", "sad", "thanks",
	};

	/// <summary>Whether a client of this version can draw the key.</summary>
	/// <param name="reaction">The key.</param>
	public static bool IsKnown(string reaction) => Known.Contains(reaction);

	/// <summary>
	/// Whether a key is <em>storable</em> - length and character set only, not membership.
	/// </summary>
	/// <param name="reaction">The key.</param>
	public static bool IsStorable(string? reaction) =>
		!string.IsNullOrWhiteSpace(reaction)
		&& reaction.Length <= MaxLength
		&& reaction.All(character =>
			char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');
}
