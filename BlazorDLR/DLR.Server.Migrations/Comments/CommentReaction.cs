using DLR.Server.Data.Identity;

namespace DLR.Server.Data.Comments;

/// <summary>
/// One person's reaction to one post (§17.4).
/// <para>
/// <strong>The primary key is the rule.</strong> <c>(CommentId, UserId)</c> means one reaction per
/// user per comment as a shape rather than as something every write path has to remember, so
/// reacting again replaces rather than accumulates. "Who reacted" is then a trivial query, and the
/// storage cost of the whole feature is one narrow row per person per comment they cared about.
/// </para>
/// </summary>
public sealed class CommentReaction
{
	/// <summary>Which post.</summary>
	public Guid CommentId { get; set; }

	/// <summary>Who reacted.</summary>
	public Guid UserId { get; set; }

	/// <summary>
	/// A key from the fixed set (§17.4).
	/// <para>
	/// Stored as a string, and stored even when this version does not know it - the same
	/// forward-compatibility rule as marker icons (§16.2). A newer client's reaction renders as a
	/// generic one on an older client rather than crashing it, and survives the round trip.
	/// </para>
	/// </summary>
	public string Reaction { get; set; } = string.Empty;

	/// <summary>The post, for cascade deletion.</summary>
	public RideComment? Comment { get; set; }

	/// <summary>The account, for cascade deletion (§10.1).</summary>
	public AppUser? User { get; set; }
}
