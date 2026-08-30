using DLR.Core.Contracts.Rides;
using DLR.Server.Data;
using DLR.Server.Data.Rides;
using DLR.Server.Hubs;
using DLR.Server.Identity;
using DLR.Server.Positions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Rides;

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
public static class MembershipEndpoints
{
	/// <summary>Route name for setting one's own sharing.</summary>
	public const string SharingRouteName = "SetSharing";

	/// <summary>Route name for leaving.</summary>
	public const string LeaveRouteName = "LeaveRide";

	/// <summary>Route name for removing a member.</summary>
	public const string RemoveRouteName = "RemoveRideMember";

	/// <summary>Route name for the content switches.</summary>
	public const string PermissionsRouteName = "SetRidePermissions";

	/// <summary>A ride's switches as the wire sees them.</summary>
	internal static RidePermissions Describe(GroupRide ride) => new(
		ride.AllowMemberMarkers,
		ride.AllowMemberComments,
		ride.AllowMemberPhotos);
}

/// <summary>
/// Consent, leaving and removal — the three ways a rider stops broadcasting (§5.6).
/// <para>
/// They are grouped deliberately. Each one has the same obligation attached to it, and every one
/// of them discharges it by calling <see cref="PositionStore.StopSharing"/> rather than by
/// writing its own delete.
/// </para>
/// </summary>
[ApiController]
[Authorize]
public sealed class MembershipController : ControllerBase
{
	/// <summary>
	/// The organiser's three content switches (§5.8).
	/// <para>
	/// A whole-object PUT rather than three toggles, so the organiser's screen sends what it shows
	/// and two switches changed in one gesture cannot half-apply.
	/// </para>
	/// </summary>
	// Changeable at any time during the ride's life (§5.8), which is why this is its own
	// endpoint rather than a field on ride creation.
	[HttpPut("/api/v1/group-rides/{id:guid}/permissions", Name = MembershipEndpoints.PermissionsRouteName)]
	[EndpointSummary("Sets what ordinary members may add.")]
	public async Task<IActionResult> SetPermissionsAsync(
		[FromRoute] Guid id,
		[FromBody] RidePermissions request,
		[FromServices] DlrDbContext database,
		[FromServices] IHubContext<RideHub, IRideClient> hub)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		GroupRideMember? membership = await database
			.Set<GroupRideMember>()
			.Include(member => member.Ride)
			.SingleOrDefaultAsync(member => member.GroupRideId == id && member.UserId == userId);

		// 404 rather than 403 for somebody who is not in the ride at all, the same way every other
		// ride route answers: a ride id is shareable, so a distinguishable refusal is an oracle.
		if (membership?.Ride is null)
		{
			return NotFound();
		}

		if (membership.Role is not (GroupRideRole.Owner or GroupRideRole.Leader))
		{
			return Problem(
				statusCode: StatusCodes.Status403Forbidden,
				title: "Not yours to set",
				detail: "The organiser decides what members may add.");
		}

		GroupRide ride = membership.Ride;

		// Nothing is deleted here, and that is the rule rather than an omission (§5.8). Turning a
		// switch off stops new content; the markers and comments already posted stay exactly where
		// they are. Same reasoning as §7.3's profile sharing — revoking a permission is not an
		// instruction to destroy what was already permitted.
		ride.AllowMemberMarkers = request.AllowMemberMarkers;
		ride.AllowMemberComments = request.AllowMemberComments;
		ride.AllowMemberPhotos = request.AllowMemberPhotos;

		await database.SaveChangesAsync();

		RidePermissions permissions = MembershipEndpoints.Describe(ride);

		await hub.Clients.Group(RideHub.Group(id)).RidePermissionsChanged(permissions);

		return Ok(permissions);
	}

	// "me", not a user id. The route itself refuses to express "set somebody else's sharing",
	// which is the §5.6 asymmetry made structural: the organiser controls the ride, the rider
	// controls their location. An endpoint taking a user id would need a guard, and a guard
	// can be removed.
	[HttpPut("/api/v1/group-rides/{id:guid}/sharing/me", Name = MembershipEndpoints.SharingRouteName)]
	[EndpointSummary("The caller's own answer to the sharing prompt, for this ride.")]
	public async Task<IActionResult> SetSharingAsync(
		[FromRoute] Guid id,
		[FromBody] SetSharingRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] PositionStore positions)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		GroupRideMember? member = await database
			.Set<GroupRideMember>()
			.SingleOrDefaultAsync(row => row.GroupRideId == id && row.UserId == userId);

		if (member is null)
		{
			return NotFound();
		}

		member.ShareLocation = request.Share;

		if (!request.Share)
		{
			// The delete is the feature. Stopping the broadcast alone would leave a last-known
			// position at rest, which is exactly what the rider just asked you not to keep.
			positions.StopSharing(id, userId);
		}

		await database.SaveChangesAsync();

		bool hasPosition = request.Share && positions.Located(id).Contains(userId);

		return Ok(new SharingState(request.Share, hasPosition));
	}

	[HttpDelete("/api/v1/group-rides/{id:guid}/members/me", Name = MembershipEndpoints.LeaveRouteName)]
	[EndpointSummary("Leaves the ride and deletes the caller's position.")]
	public async Task<IActionResult> LeaveAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] PositionStore positions,
		[FromServices] RideConnections connections)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		GroupRideMember? member = await database
			.Set<GroupRideMember>()
			.SingleOrDefaultAsync(row => row.GroupRideId == id && row.UserId == userId);

		if (member is null)
		{
			return NotFound();
		}

		if (member.Role == GroupRideRole.Owner)
		{
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "The organiser cannot leave",
				detail: "Delete the adventure instead — an adventure nobody organises has nobody to " +
					"decide who is in it.");
		}

		positions.StopSharing(id, userId);

		database.Remove(member);

		await database.SaveChangesAsync();

		// The client calls LeaveRide too, and that is not enough to rely on: a rider who leaves
		// from one device and has the ride open on another would keep the feed on the second.
		await connections.EvictAsync(id, userId);

		return NoContent();
	}

	[HttpDelete("/api/v1/group-rides/{id:guid}/members/{userId:guid}", Name = MembershipEndpoints.RemoveRouteName)]
	[EndpointSummary("Removes a member, deleting their position.")]
	public async Task<IActionResult> RemoveAsync(
		[FromRoute] Guid id,
		[FromRoute] Guid userId,
		[FromServices] DlrDbContext database,
		[FromServices] PositionStore positions,
		[FromServices] RideConnections connections)
	{
		if (User.UserId() is not { } callerId)
		{
			return Unauthorized();
		}

		bool canDecide = await database
			.Set<GroupRideMember>()
			.AnyAsync(row =>
				row.GroupRideId == id
				&& row.UserId == callerId
				&& (row.Role == GroupRideRole.Owner || row.Role == GroupRideRole.Leader));

		if (!canDecide)
		{
			return NotFound();
		}

		GroupRideMember? member = await database
			.Set<GroupRideMember>()
			.SingleOrDefaultAsync(row => row.GroupRideId == id && row.UserId == userId);

		if (member is null)
		{
			return NotFound();
		}

		if (member.Role == GroupRideRole.Owner)
		{
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "The organiser cannot be removed",
				detail: "Delete the adventure instead.");
		}

		positions.StopSharing(id, userId);

		database.Remove(member);

		await database.SaveChangesAsync();

		// The row is what ends their REST access; this is what ends the rest of it. JoinRide's
		// membership check ran when they connected and nothing re-runs it, so without this the
		// person just removed keeps receiving every position batch until their connection drops.
		await connections.EvictAsync(id, userId);

		// Their posts stay (§17.6). Deleting half a conversation makes the other half nonsense,
		// and an organiser who actually wants that can delete the posts explicitly.
		return NoContent();
	}

}
