using DLR.Server.Data.Identity;

namespace DLR.Server.Data.Rides;

/// <summary>
/// How a valid code gets somebody in (§5.2).
/// <para>
/// Replaces the <c>RequiresApproval</c> boolean it grew out of, because the two paths differ in
/// what a code <em>does</em> rather than in whether approval exists.
/// </para>
/// </summary>
public enum JoinPolicy
{
	/// <summary>A valid code joins immediately. The organiser chose who to give it to.</summary>
	Open = 0,

	/// <summary>
	/// A valid code creates a pending request. Nobody enters until the organiser admits them.
	/// </summary>
	Approval = 1,
}

/// <summary>What a member may do (§5.2).</summary>
public enum GroupRideRole
{
	/// <summary>The organiser. Decides who is in.</summary>
	Owner = 0,

	/// <summary>Delegated authority over the thread and the ride's content.</summary>
	Leader = 1,

	/// <summary>An ordinary member.</summary>
	Rider = 2,

	/// <summary>Watching, not riding.</summary>
	Spectator = 3,
}

/// <summary>
/// A group ride (§5.1, §5.2).
/// <para>
/// <strong>The organiser controls both ways in.</strong> A rider reaches another person's live
/// location only because the organiser handed out a code or pressed <em>Admit</em> - which is a
/// stronger guarantee than the confirmed-email gate it replaced in v0.5, because a confirmed
/// email only ever proved somebody could read a mailbox (§7.2, §10.1).
/// </para>
/// </summary>
public sealed class GroupRide
{
	/// <summary>Row identifier.</summary>
	public Guid Id { get; set; }

	/// <summary>The organiser.</summary>
	public Guid OwnerId { get; set; }

	/// <summary>What the ride is called.</summary>
	public string Name { get; set; } = string.Empty;

	/// <summary>Where to meet, what to bring, and so on.</summary>
	public string? Description { get; set; }

	/// <summary>When it is planned to start.</summary>
	public DateTimeOffset StartUtc { get; set; }

	/// <summary>
	/// The six-character code, stored normalised (§5.2). Unique across live rides so a typed
	/// code resolves to exactly one.
	/// </summary>
	public string JoinCode { get; set; } = string.Empty;

	/// <summary>Whether a valid code joins or asks.</summary>
	public JoinPolicy JoinPolicy { get; set; }

	/// <summary>
	/// Hard cap on members, default 50. A ride large enough to need more is a different
	/// product, and the position fan-out (§5.5) is sized on this number.
	/// </summary>
	public int MemberCap { get; set; } = DefaultMemberCap;

	/// <summary>The §5.2 default.</summary>
	public const int DefaultMemberCap = 50;

	/// <summary>
	/// Whether ordinary members may place markers (§5.8, §16.5).
	/// <para>
	/// <strong>Defaults to on, here and in the column.</strong> A group ride is a group of people
	/// the organiser chose (§5.2); starting from silence would be a strange default for a product
	/// whose point is riding together. These exist for the ride that needs them - a large public
	/// charity ride, or one that has gone sideways.
	/// </para>
	/// <para>
	/// The default lives on the property <em>and</em> on the column, the same way §7.3's sharing
	/// switches do, so a row inserted by a path that never saw this class still gets it.
	/// </para>
	/// </summary>
	public bool AllowMemberMarkers { get; set; } = true;

	/// <summary>
	/// Whether ordinary members may post to the thread (§5.8, §17).
	/// <para>
	/// Off stops <em>posting</em> only. Everyone can still read, react and vote - a reaction carries
	/// no free text, no image and no storage cost worth naming, and switching off the ability to
	/// answer a poll would break the poll rather than moderate it.
	/// </para>
	/// </summary>
	public bool AllowMemberComments { get; set; } = true;

	/// <summary>
	/// Whether ordinary members may attach photographs (§5.8, §16.4).
	/// <para>
	/// <strong>Its own switch, not a consequence of the comment switch.</strong> Photos are the
	/// expensive and awkward half - storage on a 40 GB disk (§9.1), moderation (§17.7) - and the
	/// one thing an organiser is most likely to want to stop while leaving conversation alone.
	/// </para>
	/// </summary>
	public bool AllowMemberPhotos { get; set; } = true;

	/// <summary>When the ride was created.</summary>
	public DateTimeOffset CreatedUtc { get; set; }

	/// <summary>The organiser's account, for cascade deletion.</summary>
	public AppUser? Owner { get; set; }

	/// <summary>Everybody in.</summary>
	public ICollection<GroupRideMember> Members { get; set; } = [];

	/// <summary>
	/// The planned routes, oldest attachment first (§5.4). Several, not one - a day out is
	/// commonly the short option and the long option, or the way out and the way home.
	/// </summary>
	public ICollection<GroupRideRoute> Routes { get; set; } = [];
}

/// <summary>Somebody who is in a ride (§5.2).</summary>
public sealed class GroupRideMember
{
	/// <summary>Which ride.</summary>
	public Guid GroupRideId { get; set; }

	/// <summary>Which rider.</summary>
	public Guid UserId { get; set; }

	/// <summary>What they may do.</summary>
	public GroupRideRole Role { get; set; }

	/// <summary>When they joined.</summary>
	public DateTimeOffset JoinedUtc { get; set; }

	/// <summary>
	/// Whether this rider broadcasts their position <em>to this ride</em> (§5.6).
	/// <para>
	/// <strong>Defaults to false, structurally.</strong> Joining a ride and agreeing to broadcast
	/// are two separate decisions, and a prompt that treats a swipe-away as consent is not a
	/// consent prompt. The default lives here rather than in the endpoint so that every path that
	/// creates a member - code, approval, or anything added later - inherits it.
	/// </para>
	/// <para>
	/// Per ride, not per account: a rider who shares with their regular Sunday group and not with
	/// a charity ride full of strangers is expressing something sensible, and one global switch
	/// could not express it.
	/// </para>
	/// </summary>
	public bool ShareLocation { get; set; }

	/// <summary>The ride, for cascade deletion.</summary>
	public GroupRide? Ride { get; set; }

	/// <summary>The account, for cascade deletion.</summary>
	public AppUser? User { get; set; }
}

/// <summary>Where a request to join has got to (§5.2).</summary>
public enum JoinRequestStatus
{
	/// <summary>Waiting on the organiser.</summary>
	Pending = 0,

	/// <summary>Admitted; the member row exists.</summary>
	Approved = 1,

	/// <summary>Refused.</summary>
	Declined = 2,

	/// <summary>Withdrawn by the rider.</summary>
	Withdrawn = 3,
}

/// <summary>
/// A rider asking to be let in (§5.2, §7.13).
/// <para>
/// Decided rows are kept rather than deleted: a declined-and-blocked request is what stops the
/// same person asking again, and deleting the history would be deleting the block.
/// </para>
/// </summary>
public sealed class GroupRideJoinRequest
{
	/// <summary>Row identifier.</summary>
	public Guid Id { get; set; }

	/// <summary>Which ride.</summary>
	public Guid GroupRideId { get; set; }

	/// <summary>Who is asking.</summary>
	public Guid UserId { get; set; }

	/// <summary>Where it has got to.</summary>
	public JoinRequestStatus Status { get; set; }

	/// <summary>Anything the rider wanted to say.</summary>
	public string? Message { get; set; }

	/// <summary>When they asked.</summary>
	public DateTimeOffset RequestedUtc { get; set; }

	/// <summary>When the organiser decided.</summary>
	public DateTimeOffset? DecidedUtc { get; set; }

	/// <summary>Which organiser or leader decided.</summary>
	public Guid? DecidedBy { get; set; }

	/// <summary>
	/// Whether the decline also blocks. A blocked rider cannot ask again - the organiser's
	/// answer to somebody who will not take a no.
	/// </summary>
	public bool Blocked { get; set; }

	/// <summary>The ride, for cascade deletion.</summary>
	public GroupRide? Ride { get; set; }

	/// <summary>The account, for cascade deletion.</summary>
	public AppUser? User { get; set; }
}
