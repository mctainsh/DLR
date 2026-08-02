using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Markers;
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

	/// <summary>Somebody placed a marker (§16.6).</summary>
	/// <param name="marker">The marker.</param>
	Task MarkerAdded(MarkerDto marker);

	/// <summary>A marker was edited.</summary>
	/// <param name="marker">The marker as it now is.</param>
	Task MarkerUpdated(MarkerDto marker);

	/// <summary>A marker was removed.</summary>
	/// <param name="markerId">Which one.</param>
	Task MarkerRemoved(Guid markerId);

	/// <summary>
	/// The organiser changed what members may add (§5.8).
	/// <para>
	/// A courtesy so the UI does not lie — a client greys out the compose surface on this rather
	/// than discovering the change when a post comes back 403. The server-side check is what makes
	/// it true; this only makes it visible.
	/// </para>
	/// </summary>
	/// <param name="permissions">The switches as they now stand.</param>
	Task RidePermissionsChanged(RidePermissions permissions);

	/// <summary>
	/// Somebody posted to the thread (§17.8).
	/// <para>
	/// <strong>Delivering it is not notifying about it.</strong> The post arrives on every open
	/// connection so the thread stays live; whether a phone is allowed to buzz is §17.1's table,
	/// and during a `Live` ride the answer for an ordinary comment is no.
	/// </para>
	/// </summary>
	/// <param name="comment">The post.</param>
	Task CommentPosted(CommentDto comment);

	/// <summary>A post was edited inside its window.</summary>
	/// <param name="comment">The post as it now reads.</param>
	Task CommentEdited(CommentDto comment);

	/// <summary>A post was removed.</summary>
	/// <param name="commentId">Which one.</param>
	Task CommentRemoved(Guid commentId);

	/// <summary>A post was pinned or unpinned (§17.6).</summary>
	/// <param name="commentId">Which one.</param>
	/// <param name="isPinned">Its new state.</param>
	Task CommentPinChanged(Guid commentId, bool isPinned);

	/// <summary>
	/// A comment's reaction tally changed (§17.4).
	/// <para>
	/// <strong>Coalesced, never one message per tap.</strong> Twelve members thumbs-upping the same
	/// photo, each tap relayed to the other eleven, is the O(n²) fan-out this hub already refused
	/// for positions — for a payload that is a number.
	/// </para>
	/// <para>
	/// <see cref="ReactionCounts.Mine"/> is null here of necessity: a group message has one body,
	/// and "mine" is different for every connection in the group. Each client knows what it sent.
	/// </para>
	/// </summary>
	/// <param name="commentId">Which comment.</param>
	/// <param name="counts">The new tally.</param>
	Task ReactionsUpdated(Guid commentId, ReactionCounts counts);

	/// <summary>A poll's votes changed (§17.5). Coalesced on the same timer as reactions.</summary>
	/// <param name="commentId">Which poll.</param>
	/// <param name="results">Where it now stands. <c>MyOptionIds</c> is empty, for the same reason.</param>
	Task PollUpdated(Guid commentId, PollResults results);
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
