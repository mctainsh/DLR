namespace DLR.Core.Contracts.Rides;

/// <summary>How a valid code gets somebody in (§5.2).</summary>
public enum JoinPolicyDto
{
	/// <summary>A valid code joins immediately.</summary>
	Open = 0,

	/// <summary>A valid code creates a pending request.</summary>
	Approval = 1,
}

/// <summary>Where a ride is in its lifecycle (§5.1).</summary>
public enum RideStateDto
{
	/// <summary>Not yet published.</summary>
	Draft = 0,

	/// <summary>Joinable, no live positions.</summary>
	Open = 1,

	/// <summary>Positions are flowing.</summary>
	Live = 2,

	/// <summary>Finished.</summary>
	Completed = 3,

	/// <summary>Read-only.</summary>
	Archived = 4,

	/// <summary>Called off.</summary>
	Cancelled = 5,
}

/// <summary><c>POST /api/v1/group-rides</c> (§5.2).</summary>
/// <param name="Name">What the ride is called.</param>
/// <param name="StartUtc">When it is planned to start.</param>
/// <param name="Description">Where to meet, what to bring.</param>
/// <param name="JoinPolicy">Whether a valid code joins or asks.</param>
/// <param name="MemberCap">Hard cap, defaulting to 50 when omitted.</param>
public sealed record CreateRideRequest(
	string Name,
	DateTimeOffset StartUtc,
	string? Description = null,
	JoinPolicyDto JoinPolicy = JoinPolicyDto.Approval,
	int? MemberCap = null);

/// <summary>
/// <c>POST /api/v1/group-rides/join</c> (§5.2).
/// </summary>
/// <param name="Code">
/// As typed. Case, spaces and hyphens are forgiven, and the characters Crockford base32 leaves
/// out are mapped to the ones they are mistaken for.
/// </param>
/// <param name="Message">Anything the rider wants the organiser to see, on an approval ride.</param>
public sealed record JoinByCodeRequest(string Code, string? Message = null);

/// <summary>
/// What a join attempt produced (§5.2).
/// <para>
/// Both paths end at the location-sharing prompt (§5.6) — joining a ride and agreeing to
/// broadcast are separate decisions, and the second one defaults to off.
/// </para>
/// </summary>
/// <param name="RideId">Which ride.</param>
/// <param name="Joined">Whether the rider is in, or only waiting.</param>
/// <param name="RequestId">The pending request, when there is one.</param>
public sealed record JoinResult(Guid RideId, bool Joined, Guid? RequestId);

/// <summary>A ride, as a member or the organiser sees it (§5.2).</summary>
/// <param name="Id">Which ride.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Description">The organiser's notes.</param>
/// <param name="StartUtc">When it starts.</param>
/// <param name="State">Where it is in the lifecycle.</param>
/// <param name="JoinPolicy">Whether a valid code joins or asks.</param>
/// <param name="MemberCap">The hard cap.</param>
/// <param name="MemberCount">How many are in.</param>
/// <param name="IsOrganiser">Whether the caller runs it.</param>
/// <param name="JoinCode">
/// The code that gets somebody into the ride (§5.2). Sent to every member, not only the
/// organiser: a rider who is already in wants to tell a friend how to follow along, and on an
/// approval ride the organiser still decides who is actually admitted.
/// </param>
/// <param name="Permissions">
/// What ordinary members may add (§5.8). Sent to everyone, not only the organiser: a client that
/// does not know a switch is off draws a compose surface that produces a 403 when used, which
/// reads as a broken app rather than as a decision somebody made.
/// </param>
/// <param name="Members">Who is in.</param>
public sealed record RideDetail(
	Guid Id,
	string Name,
	string? Description,
	DateTimeOffset StartUtc,
	RideStateDto State,
	JoinPolicyDto JoinPolicy,
	int MemberCap,
	int MemberCount,
	bool IsOrganiser,
	string? JoinCode,
	RidePermissions Permissions,
	IReadOnlyList<RideMemberSummary> Members);

/// <summary>
/// The organiser's three content switches (§5.8).
/// <para>
/// All default <strong>on</strong>. A group ride is a group of people the organiser chose, so
/// starting from silence would be a strange default for a product whose point is riding together;
/// these exist for the ride that needs them — a large public charity ride, or one that has gone
/// sideways.
/// </para>
/// <para>
/// <strong>Turning one off stops new content and deletes nothing.</strong> Same rule as §7.3's
/// profile sharing, for the same reason — revoking a permission is not an instruction to destroy
/// what was already permitted.
/// </para>
/// </summary>
/// <param name="AllowMemberMarkers">Whether ordinary members may place markers (§16.5).</param>
/// <param name="AllowMemberComments">
/// Whether ordinary members may post. Off leaves reading, reacting and voting alone — a reaction
/// carries no free text, and switching off the ability to answer a poll would break the poll
/// rather than moderate it.
/// </param>
/// <param name="AllowMemberPhotos">
/// Whether ordinary members may attach photographs, to comments or to markers. Its own switch
/// rather than a consequence of the comment one, because photos are the expensive half.
/// </param>
public sealed record RidePermissions(
	bool AllowMemberMarkers = true,
	bool AllowMemberComments = true,
	bool AllowMemberPhotos = true);

/// <summary>One row of the member list (§5.2).</summary>
/// <param name="UserId">Which rider.</param>
/// <param name="UserName">The immutable handle, which is also the map label (§7.2).</param>
/// <param name="Role">What they may do.</param>
/// <param name="JoinedUtc">When they joined.</param>
/// <param name="Sharing">
/// Whether they broadcast to this ride (§5.6). A rider may be in a ride without sharing, and the
/// control is <em>visibility, not enforcement</em> — the list makes the asymmetry legible so a
/// group that cares can say something, which is a social fix for a social problem.
/// </param>
/// <param name="HasPosition">
/// Whether a position is stored. Kept separate from <paramref name="Sharing"/> because
/// <em>not sharing</em> and <em>no signal</em> mean completely different things to somebody
/// waiting at a junction, and collapsing them is the kind of small ambiguity that gets someone
/// left behind.
/// </param>
/// <param name="MarkerColour">
/// The background this member's marker is drawn in on the live map, as <c>#rrggbb</c>, or null
/// for the default (§16.3).
/// <para>
/// It rides on the member row rather than on the position batch: the batch is sent to every
/// member once per tick and a colour changes about as often as a username does, so repeating it
/// five times a second would be bytes spent on something that does not move. The map reads it
/// from here and applies it to whichever fix arrives for that rider.
/// </para>
/// </param>
public sealed record RideMemberSummary(
	Guid UserId,
	string UserName,
	string Role,
	DateTimeOffset JoinedUtc,
	bool Sharing = false,
	bool HasPosition = false,
	string? MarkerColour = null);

/// <summary>
/// One row of the "my rides" landing (§5.2). A summary rather than a full <see cref="RideDetail"/>
/// because the list needs neither members nor permissions to render, and shipping either on every
/// list entry costs bandwidth for values a screen does not read.
/// </summary>
/// <param name="Id">Which ride.</param>
/// <param name="Name">What it is called.</param>
/// <param name="StartUtc">When it starts.</param>
/// <param name="State">Where it is in the lifecycle.</param>
/// <param name="IsOrganiser">Whether the caller runs it.</param>
/// <param name="MemberCount">How many are in.</param>
/// <param name="JoinCode">
/// Same rule as <see cref="RideDetail.JoinCode"/> — sent to every member, so a joined ride shows
/// its code on the list as an organised one does.
/// </param>
public sealed record RideSummary(
	Guid Id,
	string Name,
	DateTimeOffset StartUtc,
	RideStateDto State,
	bool IsOrganiser,
	int MemberCount,
	string? JoinCode);

/// <summary>
/// The caller's rides, split by role (§5.2). Split on the wire rather than reconstructed on the
/// client so a member of one ride and the organiser of another gets two lists rather than one
/// list plus a filter.
/// </summary>
/// <param name="Organised">Rides the caller created.</param>
/// <param name="Joined">Rides the caller was admitted to.</param>
public sealed record MyRides(
	IReadOnlyList<RideSummary> Organised,
	IReadOnlyList<RideSummary> Joined);

/// <summary>A request waiting on the organiser (§5.2).</summary>
/// <param name="Id">Which request.</param>
/// <param name="UserId">Who is asking.</param>
/// <param name="UserName">Their handle.</param>
/// <param name="Message">Anything they said.</param>
/// <param name="RequestedUtc">When they asked.</param>
public sealed record JoinRequestSummary(
	Guid Id,
	Guid UserId,
	string UserName,
	string? Message,
	DateTimeOffset RequestedUtc);

/// <summary>The organiser's answer (§5.2).</summary>
/// <param name="Admit">True to let them in, false to decline.</param>
/// <param name="Block">
/// On a decline, whether they may ask again. The organiser's answer to somebody who will not
/// take a no.
/// </param>
public sealed record DecideJoinRequest(bool Admit, bool Block = false);
