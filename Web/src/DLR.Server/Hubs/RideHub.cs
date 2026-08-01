using System.Security.Claims;
using DLR.Core.Contracts.Rides;
using DLR.Server.Data;
using DLR.Server.Data.Rides;
using DLR.Server.Identity;
using DLR.Server.Positions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Hubs;

/// <summary>
/// What the server sends a connected client (§5.3).
/// <para>
/// Only the messages whose features exist. §5.3 lists markers, comments, reactions and polls as
/// well; those arrive with SRV-26 and Milestone F, and declaring them now would be a contract
/// nothing on either side implements.
/// </para>
/// </summary>
public interface IRideClient
{
	/// <summary>Every member's latest position, once per tick.</summary>
	/// <param name="batch">The positions.</param>
	Task PositionsUpdated(PositionBatch batch);

	/// <summary>A rider turned sharing on or off (§5.6).</summary>
	/// <param name="memberId">Which rider.</param>
	/// <param name="sharing">Their new state.</param>
	Task MemberSharingChanged(Guid memberId, bool sharing);

	/// <summary>The ride moved through the §5.1 lifecycle.</summary>
	/// <param name="state">Where it is now.</param>
	Task RideStateChanged(RideStateDto state);
}

/// <summary>
/// The live ride connection (§5.3, §7.6).
/// <para>
/// <strong>Authentication is not authorisation.</strong> A valid token proves who the user is, not
/// which rides they belong to. Since the confirmed-email gate was removed in v0.5, the membership
/// check in <see cref="JoinRide"/> is the <em>only</em> thing standing between an authenticated
/// account and a stranger's live location — so it is not optional, and it is tested directly.
/// </para>
/// </summary>
/// <param name="database">For the membership check.</param>
/// <param name="positions">Where a published fix goes.</param>
[Authorize]
public sealed class RideHub(DlrDbContext database, PositionStore positions) : Hub<IRideClient>
{
	/// <summary>The path the query-string token lift is scoped to (§7.6).</summary>
	public const string Path = "/hubs/ride";

	/// <summary>Subscribes to a ride's live positions.</summary>
	/// <param name="rideId">Which ride.</param>
	/// <exception cref="HubException">When the caller is not a member.</exception>
	public async Task JoinRide(Guid rideId)
	{
		Guid userId = CallerId();

		// Membership, not a join *request*. A pending requester has a row in
		// group_ride_join_request and none in group_ride_member, and checking the wrong one would
		// admit exactly the people the organiser has not yet decided about (§5.2).
		bool isMember = await database
			.Set<GroupRideMember>()
			.AnyAsync(member => member.GroupRideId == rideId && member.UserId == userId);

		if (!isMember)
		{
			// The same answer a ride that does not exist gets, for the same reason the join-code
			// path gives one (§5.2): a ride id is shareable, and a distinguishable refusal turns
			// this method into an oracle for who is in which ride.
			throw new HubException("No such ride.");
		}

		await Groups.AddToGroupAsync(Context.ConnectionId, Group(rideId));
	}

	/// <summary>Unsubscribes.</summary>
	/// <param name="rideId">Which ride.</param>
	public Task LeaveRide(Guid rideId) =>
		Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(rideId));

	/// <summary>
	/// Publishes one fix into every ride this rider is sharing with (§5.7).
	/// </summary>
	/// <param name="update">The fix. Carries no ride id, deliberately.</param>
	public async Task PublishPosition(PositionUpdate update) =>
		await positions.PublishAsync(CallerId(), update);

	/// <summary>The SignalR group name for a ride.</summary>
	/// <param name="rideId">Which ride.</param>
	/// <returns>The group name.</returns>
	public static string Group(Guid rideId) => $"ride:{rideId}";

	private Guid CallerId() =>
		(Context.User?.UserId())
		?? throw new HubException("Not signed in.");
}
