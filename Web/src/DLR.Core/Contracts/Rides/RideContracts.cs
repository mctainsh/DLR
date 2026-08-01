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
/// Only ever sent to the organiser. It is the ride's entire access control (§5.2), so putting
/// it in a member's copy would let any member re-share the ride the organiser curated.
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
	IReadOnlyList<RideMemberSummary> Members);

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
public sealed record RideMemberSummary(
	Guid UserId,
	string UserName,
	string Role,
	DateTimeOffset JoinedUtc,
	bool Sharing = false,
	bool HasPosition = false);

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
