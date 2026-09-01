using DLR.Server.Data.Identity;

namespace DLR.Server.Data.Moderation;

/// <summary>What was reported (§17.7).</summary>
public enum ReportTargetKind
{
	/// <summary>A marker on a ride's map (§16).</summary>
	Marker = 0,

	/// <summary>A post in a ride's thread (§17).</summary>
	Comment = 1,
}

/// <summary>
/// Somebody reporting content (§16.5, §17.7).
/// <para>
/// <strong>One table for markers and comments, not two.</strong> v0.13 had a `MarkerReport`; the
/// thread then became the larger user-generated-content surface, and two report tables would have
/// drifted - the one nobody was looking at being the one that stopped snapshotting, or stopped
/// being swept.
/// </para>
/// <para>
/// <strong>This exists because of store review, and that is not a reason to build it badly.</strong>
/// Apple and Play require a way to report objectionable content and block its author, plus a stated
/// commitment to act. A ride's audience is small and organiser-admitted, which makes abuse
/// unlikely - it does not make the mechanism optional at submission (§10.2).
/// </para>
/// </summary>
public sealed class ContentReport
{
	/// <summary>Row identifier.</summary>
	public Guid Id { get; set; }

	/// <summary>Marker or comment.</summary>
	public ReportTargetKind TargetKind { get; set; }

	/// <summary>
	/// Which one.
	/// <para>
	/// <strong>Deliberately not a foreign key.</strong> A report has to outlive the thing it is
	/// about - an organiser deleting an abusive comment must not also delete the evidence for the
	/// report just filed against it - and a foreign key would either cascade the report away or
	/// refuse the deletion. Both are wrong.
	/// </para>
	/// </summary>
	public Guid TargetId { get; set; }

	/// <summary>Which ride the content was in, so an organiser can be told.</summary>
	public Guid? GroupRideId { get; set; }

	/// <summary>Who wrote the content. Kept beside the snapshot for the same reason it is.</summary>
	public Guid? AuthorId { get; set; }

	/// <summary>Who reported it.</summary>
	public Guid ReportedByUserId { get; set; }

	/// <summary>What they said was wrong with it.</summary>
	public string Reason { get; set; } = string.Empty;

	/// <summary>
	/// The content as it read when it was reported.
	/// <para>
	/// <strong>The point of the whole table.</strong> Hard-deleting a reported comment is exactly
	/// what an organiser should do, and it must not destroy what the operator needs to decide
	/// whether the author should still have an account. Purged with the resolved report by the
	/// nightly job (§7.11) after <c>Moderation:ReportRetentionDays</c> - a snapshot kept forever
	/// would be a copy of deleted content, which is its own privacy problem.
	/// </para>
	/// </summary>
	public string ContentSnapshot { get; set; } = string.Empty;

	/// <summary>When it was reported.</summary>
	public DateTimeOffset CreatedUtc { get; set; }

	/// <summary>When the operator dealt with it, or null while it is still open.</summary>
	public DateTimeOffset? ResolvedUtc { get; set; }

	/// <summary>The reporter.</summary>
	public AppUser? ReportedBy { get; set; }
}

/// <summary>
/// One rider hiding another (§16.5, §17.7).
/// <para>
/// <strong>One-directional, and the direction is the blocker's.</strong> Blocking somebody hides
/// <em>their</em> markers, posts and reactions from <em>you</em>. It is not a punishment applied to
/// them and it does not tell them anything - a block that announced itself would turn a quiet
/// "I would rather not read this person" into the confrontation it exists to avoid.
/// </para>
/// <para>
/// Distinct from <c>GroupRideJoinRequest.Blocked</c>, which is an <em>organiser</em> refusing a
/// requester entry to one ride. Same word, different mechanism, different actor.
/// </para>
/// </summary>
public sealed class UserBlock
{
	/// <summary>Who is doing the blocking.</summary>
	public Guid BlockerId { get; set; }

	/// <summary>Who is hidden from them.</summary>
	public Guid BlockedId { get; set; }

	/// <summary>When.</summary>
	public DateTimeOffset CreatedUtc { get; set; }

	/// <summary>The blocker.</summary>
	public AppUser? Blocker { get; set; }

	/// <summary>The blocked rider.</summary>
	public AppUser? Blocked { get; set; }
}
