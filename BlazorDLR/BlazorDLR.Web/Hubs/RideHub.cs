using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Rides;
using DLR.Server.Data;
using DLR.Server.Data.Rides;
using DLR.Server.Data.Tracks;
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

	/// <summary>
	/// A rider entered or left their own private area (§10.1, §5.6).
	/// <para>
	/// <strong>The message carries no coordinate and never has one to carry.</strong> Going private
	/// deletes the rider's stored position, so this is the notice that the row about to go empty is
	/// a choice rather than a tunnel — the distinction §5.6 keeps insisting on, applied to a third
	/// reason a pin can be missing. Where the circle is stays on the rider's own profile.
	/// </para>
	/// <para>
	/// The ride id rides along, unlike <see cref="MemberSharingChanged"/>'s, because a client holds
	/// one session per ride and process-wide events have to be able to say which one they are about.
	/// </para>
	/// </summary>
	/// <param name="rideId">Which ride.</param>
	/// <param name="memberId">Which rider.</param>
	/// <param name="isPrivate">Whether they are now private.</param>
	Task MemberPrivacyChanged(Guid rideId, Guid memberId, bool isPrivate);

	/// <summary>
	/// Somebody is now on the ride (§5.2, §5.3).
	/// <para>
	/// The whole member row rather than a nudge to refetch, unlike <see cref="RideRoutesChanged"/>:
	/// a member is a name, a role and three flags, and the screens that draw the list would
	/// otherwise all answer one join with one GET each.
	/// </para>
	/// <para>
	/// <strong>The new member does not receive their own.</strong> They join over REST and only
	/// then open the ride, so they are not in this group when it is sent — and they do not need to
	/// be, because the snapshot they load a moment later already has them in it (§5.3).
	/// </para>
	/// </summary>
	/// <param name="rideId">Which ride.</param>
	/// <param name="member">Who joined, as the member list draws them.</param>
	Task MemberJoined(Guid rideId, RideMemberSummary member);

	/// <summary>
	/// A planned route was attached to or detached from the ride (§5.4).
	/// <para>
	/// A signal, not the routes themselves. The lines are the largest thing a ride owns, they
	/// change rarely, and a client that has just been told the set moved is about to fetch it
	/// anyway — pushing several thousand encoded points to every connection to save one GET
	/// would be paying the fan-out cost for the payload §5.5 is careful about.
	/// </para>
	/// </summary>
	/// <param name="rideId">Which ride's routes changed.</param>
	Task RideRoutesChanged(Guid rideId);

	/// <summary>
	/// Somebody asked to join, on a ride the recipient may decide about (§5.2).
	/// <para>
	/// <strong>Sent to the deciders' group, never to the ride's.</strong> The payload carries the
	/// asker's handle and whatever they wrote, and a pending requester is somebody the organiser
	/// has not yet agreed to have on the ride — putting that in front of all fifty members would
	/// publish a request that may be about to be declined. The group is exactly the set
	/// <c>RideController.CanDecideAsync</c> would let call the list endpoint, so this tells nobody
	/// anything they could not already fetch.
	/// </para>
	/// </summary>
	/// <param name="rideId">Which ride. Carried, because a client holds one session per ride.</param>
	/// <param name="request">Who is asking, and what they said.</param>
	Task JoinRequestReceived(Guid rideId, JoinRequestSummary request);

	/// <summary>
	/// A waiting request was admitted or declined (§5.2).
	/// <para>
	/// The deciders' group again, and for a different reason from the message above: this one is
	/// what keeps the waiting count honest across an organiser's own devices, and on the device
	/// that made the decision. <strong>It is not how the asker finds out.</strong> They are not in
	/// either group — a pending requester is not a member and has never joined the ride's hub
	/// group — so their answer is the e-mail <c>RideNotifications</c> sends and the state their
	/// next load reads back.
	/// </para>
	/// </summary>
	/// <param name="rideId">Which ride.</param>
	/// <param name="result">The decision, and which request it was about.</param>
	Task JoinRequestDecided(Guid rideId, JoinResult result);

	/// <summary>
	/// Somebody took their own request back before it was answered (§5.2).
	/// <para>
	/// Its own message rather than a <see cref="JoinRequestDecided"/> with a false in it, even
	/// though the two move the waiting count the same way. Nobody decided anything here, and a
	/// client — or a later reader of this contract — that has to know a decline and a withdrawal
	/// apart would have no way to tell them apart from that payload.
	/// </para>
	/// <para>
	/// The deciders' group, like the other two: it exists so an organiser's badge stops counting a
	/// request that is not there any more, and the list behind that badge is theirs alone.
	/// </para>
	/// </summary>
	/// <param name="rideId">Which ride.</param>
	/// <param name="requestId">The request that no longer exists.</param>
	Task JoinRequestWithdrawn(Guid rideId, Guid requestId);

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
	/// Somebody posted to a thread — an adventure's or a shared route's (§17.8, §6.2).
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
/// <param name="clients">
/// The ride groups, for the one thing this hub announces itself. Everything else on
/// <see cref="IRideClient"/> is sent from an endpoint; privacy is sent from here because it is the
/// only state change a client makes over the hub rather than over REST.
/// </param>
[Authorize]
public sealed class RideHub(
	DlrDbContext database,
	PositionStore positions,
	IHubContext<RideHub, IRideClient> clients) : Hub<IRideClient>
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
		//
		// The role comes back with it rather than in a second query: the answer to "are they in?"
		// and the answer to "may they decide who else is?" are the same row, and reading it once
		// is what stops the two drifting apart on a connect.
		GroupRideRole? role = await database
			.Set<GroupRideMember>()
			.Where(member => member.GroupRideId == rideId && member.UserId == userId)
			.Select(member => (GroupRideRole?)member.Role)
			.SingleOrDefaultAsync();

		if (role is null)
		{
			// The same answer a ride that does not exist gets, for the same reason the join-code
			// path gives one (§5.2): a ride id is shareable, and a distinguishable refusal turns
			// this method into an oracle for who is in which ride.
			throw new HubException("No such adventure.");
		}

		await Groups.AddToGroupAsync(Context.ConnectionId, Group(rideId));

		// A second group for the people who decide who is on the ride (§5.2), so that a join
		// request can be announced live without being announced to the fifty people it is not
		// about. Mirrors RideController.CanDecideAsync exactly — if that ever admits a fourth
		// role, this has to move with it or the badge stops arriving for somebody who can act.
		if (role is GroupRideRole.Owner or GroupRideRole.Leader)
		{
			await Groups.AddToGroupAsync(Context.ConnectionId, DecidersGroup(rideId));
		}
	}

	/// <summary>Unsubscribes.</summary>
	/// <param name="rideId">Which ride.</param>
	public async Task LeaveRide(Guid rideId)
	{
		await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(rideId));

		// Unconditional, unlike the join. Removing a connection from a group it was never in is a
		// no-op, and asking the database what role this member holds on the way *out* would be a
		// query whose answer can only cost us a leaked subscription if it has changed since.
		await Groups.RemoveFromGroupAsync(Context.ConnectionId, DecidersGroup(rideId));
	}

	/// <summary>
	/// Subscribes to a shared route's thread (§6.2).
	/// </summary>
	/// <param name="trackId">Which route.</param>
	/// <exception cref="HubException">When the route is not on the public list.</exception>
	/// <remarks>
	/// The check is the same shape as <see cref="JoinRide"/>'s and for the same reason — a valid
	/// token proves who somebody is, not what they may watch — but the question it asks is
	/// different. A route on the browse list has been put in front of every signed-in rider on
	/// purpose, so "is it public?" is the whole of it; and the owner is admitted to their own
	/// route whatever its visibility, so that un-sharing does not cut them off from the
	/// conversation that is still there.
	/// <para>
	/// A blocked owner is not filtered here. The thread endpoint refuses that reader outright, so
	/// there is nothing for a live message to add to a screen they cannot open — and the block
	/// list is applied per reader on receipt, which is not something a group message can do
	/// (§17.7).
	/// </para>
	/// </remarks>
	public async Task JoinTrack(Guid trackId)
	{
		Guid userId = CallerId();

		bool visible = await database
			.Set<Track>()
			.AnyAsync(track =>
				track.Id == trackId
				&& (track.Visibility == TrackVisibility.Public || track.OwnerId == userId));

		if (!visible)
		{
			// The same answer a route that does not exist gets, on JoinRide's reasoning: a track
			// id travels in links, and a distinguishable refusal would make this an oracle for
			// which of them are real.
			throw new HubException("No such route.");
		}

		await Groups.AddToGroupAsync(Context.ConnectionId, TrackGroup(trackId));
	}

	/// <summary>Unsubscribes from a route's thread.</summary>
	/// <param name="trackId">Which route.</param>
	public Task LeaveTrack(Guid trackId) =>
		Groups.RemoveFromGroupAsync(Context.ConnectionId, TrackGroup(trackId));

	/// <summary>
	/// Publishes one fix into every ride this rider is sharing with (§5.7).
	/// </summary>
	/// <param name="update">The fix. Carries no ride id, deliberately.</param>
	public async Task PublishPosition(PositionUpdate update)
	{
		Guid userId = CallerId();
		PositionPublication published = await positions.PublishAsync(userId, update);

		if (published.LeftPrivateArea)
		{
			// A coordinate is proof the rider is out of their circle, so the flag is cleared on the
			// write path as well as on the device's own say-so (§10.1). Announcing it here is what
			// keeps a dropped "no longer private" call costing one tick rather than the rest of the
			// ride: the next batch is about to put their pin back, and a member list still reading
			// "private" beside a moving pin is worse than either state on its own.
			await clients.AnnouncePrivacyAsync(published.RideIds, userId, isPrivate: false);
		}
	}

	/// <summary>
	/// Takes this rider off every map they are on, or puts them back — the private area (§10.1).
	/// </summary>
	/// <param name="update">Which way they crossed the edge of their own circle.</param>
	/// <remarks>
	/// <strong>No coordinate, in either direction.</strong> The phone drops fixes from inside the
	/// circle where it read them and sends this instead, so the only thing that reaches the server is
	/// that the rider is somewhere they chose not to be observed. A jittered or edge-snapped point
	/// would be worse than nothing — a handful of them bound the true centre.
	/// <para>
	/// The hub rather than the REST endpoint is the ordinary path, for the reason every fix takes it:
	/// the connection is already open. The endpoint exists because this message is the one whose loss
	/// is expensive — it is sent once at a boundary rather than repeated every tick — so it must
	/// survive a reconnecting hub.
	/// </para>
	/// </remarks>
	public async Task PublishPrivacy(PositionPrivacyUpdate update)
	{
		Guid userId = CallerId();

		await clients.AnnouncePrivacyAsync(
			await positions.SetPrivateAsync(userId, update.Private),
			userId,
			update.Private);
	}

	/// <summary>The SignalR group name for a ride.</summary>
	/// <param name="rideId">Which ride.</param>
	/// <returns>The group name.</returns>
	public static string Group(Guid rideId) => $"ride:{rideId}";

	/// <summary>
	/// The SignalR group name for the people who decide who is on a ride — its organiser and its
	/// leaders (§5.2).
	/// <para>
	/// A subset of <see cref="Group"/>'s members, and a separate group rather than a filter on
	/// receipt, because the filtering has to happen before the payload leaves the server: a client
	/// told to ignore a message has still been sent it.
	/// </para>
	/// </summary>
	/// <param name="rideId">Which ride.</param>
	/// <returns>The group name.</returns>
	public static string DecidersGroup(Guid rideId) => $"ride-deciders:{rideId}";

	/// <summary>
	/// The SignalR group name for a shared route's thread.
	/// <para>
	/// Prefixed differently from a ride's, and that is load-bearing rather than tidy: both are
	/// guids, and a shared namespace would put every reader of a route into the group that
	/// carries a ride's live positions if the two identifiers ever collided.
	/// </para>
	/// </summary>
	/// <param name="trackId">Which route.</param>
	/// <returns>The group name.</returns>
	public static string TrackGroup(Guid trackId) => $"track:{trackId}";

	private Guid CallerId() =>
		(Context.User?.UserId())
		?? throw new HubException("Not signed in.");
}

/// <summary>
/// Telling a ride that its membership grew (§5.2, §5.3).
/// <para>
/// An extension rather than a method on the hub, on <see cref="RidePrivacyBroadcast"/>'s
/// pattern: the send happens from the REST endpoint that wrote the row, and the hub class is
/// where the group names and the message contract live.
/// </para>
/// </summary>
public static class RideMembershipBroadcast
{
	/// <summary>
	/// Sends <see cref="IRideClient.MemberJoined"/> to everybody already on the ride.
	/// <para>
	/// <strong>Swallowing, deliberately.</strong> The membership row is committed before this is
	/// called, so a hub that cannot deliver must not turn somebody's successful join into a 500
	/// for them (§7.12). The cost of a lost message is a member list that is one row short until
	/// its next load — §5.3's rule, again: the snapshot is authoritative and this is the delta.
	/// </para>
	/// </summary>
	/// <param name="hub">The connections.</param>
	/// <param name="rideId">Which ride.</param>
	/// <param name="member">Who joined.</param>
	/// <param name="logger">Where a failed announcement is recorded.</param>
	public static async Task AnnounceJoinedAsync(
		this IHubContext<RideHub, IRideClient> hub,
		Guid rideId,
		RideMemberSummary member,
		ILogger logger)
	{
		try
		{
			await hub.Clients.Group(RideHub.Group(rideId)).MemberJoined(rideId, member);
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "Could not announce a new member of {RideId}.", rideId);
		}
	}
}

/// <summary>
/// Telling a set of rides that one of their members went private, or stopped being (§10.1, §5.6).
/// </summary>
public static class RidePrivacyBroadcast
{
	/// <summary>
	/// Sends <see cref="IRideClient.MemberPrivacyChanged"/> to each ride in turn.
	/// </summary>
	/// <param name="hub">The connections.</param>
	/// <param name="rideIds">
	/// The rides to tell. Empty when nothing changed, which is the ordinary case for a device
	/// re-stating what the server already believes — and then this sends nothing at all.
	/// </param>
	/// <param name="userId">Which rider.</param>
	/// <param name="isPrivate">Their new state.</param>
	public static async Task AnnouncePrivacyAsync(
		this IHubContext<RideHub, IRideClient> hub,
		IReadOnlyList<Guid> rideIds,
		Guid userId,
		bool isPrivate)
	{
		foreach (Guid rideId in rideIds)
		{
			await hub.Clients.Group(RideHub.Group(rideId)).MemberPrivacyChanged(rideId, userId, isPrivate);
		}
	}
}
