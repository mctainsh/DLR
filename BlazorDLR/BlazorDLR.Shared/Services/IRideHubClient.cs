using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Rides;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// The realtime seam (§5.3). One SignalR client, three hosts.
/// <para>
/// Reconnect refetches state; it never replays history (§5.3). The events below match
/// <c>IRideClient</c> on the server side of the hub.
/// </para>
/// </summary>
public interface IRideHubClient : IAsyncDisposable
{
	/// <summary>Whether the underlying connection is currently up.</summary>
	bool IsConnected { get; }

	/// <summary>
	/// <see cref="IsConnected"/> may have changed — the connection came up, dropped, or started
	/// trying again.
	/// <para>
	/// A property with no event is a property a screen can only read at the moment it happens to
	/// render, and a phone that loses signal mid-ride renders nothing at all afterwards: no
	/// batches arrive, so nothing re-renders, so the map goes on looking live while it silently
	/// stops being (§5.3). The live map's "no network" warning is driven from here.
	/// </para>
	/// <para>
	/// Raised on every transition rather than only on the losing one, so a subscriber that shows a
	/// warning has something to take it down again. Carries no payload: the answer is
	/// <see cref="IsConnected"/>, read fresh, which cannot go stale between the raise and the
	/// handler.
	/// </para>
	/// </summary>
	event Action? ConnectionChanged;

	/// <summary>Open the connection.</summary>
	Task ConnectAsync(CancellationToken cancellationToken = default);

	/// <summary>Subscribe to a ride's group so its members' events reach this client.</summary>
	Task JoinRideAsync(Guid rideId, CancellationToken cancellationToken = default);

	/// <summary>Unsubscribe.</summary>
	Task LeaveRideAsync(Guid rideId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Subscribe to a shared route's thread so its posts and reactions reach this client (§6.2).
	/// <para>
	/// A separate group from a ride's, and the server keeps them in separate namespaces: a route's
	/// thread is open to every signed-in rider, while a ride's group also carries live positions
	/// that only its members may see.
	/// </para>
	/// </summary>
	Task JoinTrackAsync(Guid trackId, CancellationToken cancellationToken = default);

	/// <summary>Unsubscribe from a route's thread.</summary>
	Task LeaveTrackAsync(Guid trackId, CancellationToken cancellationToken = default);

	/// <summary>Publish this device's position — one push, fanned out to every ride the rider is live in (§5.7).</summary>
	Task PublishPositionAsync(PositionUpdate update, CancellationToken cancellationToken = default);

	/// <summary>
	/// Say that this rider has entered — or left — their own private area (§10.1).
	/// <para>
	/// Sent <em>instead of</em> a position, never alongside one: a fix from inside the circle is
	/// dropped on the device, and this one bit goes in its place. It takes the rider off every other
	/// map they are on and leaves them on the member list, labelled.
	/// </para>
	/// </summary>
	/// <param name="update">Which way they crossed the edge.</param>
	/// <param name="cancellationToken">Abandons the send.</param>
	Task PublishPrivacyAsync(PositionPrivacyUpdate update, CancellationToken cancellationToken = default);

	// -- Server → client events (§5.3) ----------------------------------------------------

	/// <summary>A batch of every member's latest position for one ride (§5.3).</summary>
	event Action<PositionBatch>? PositionsUpdated;

	/// <summary>A new member joined this ride.</summary>
	event Action<Guid, RideMemberSummary>? MemberJoined;

	/// <summary>A member left, or was removed.</summary>
	event Action<Guid, Guid>? MemberLeft;

	/// <summary>The ride's lifecycle changed (§5.1).</summary>
	event Action<Guid, RideStateDto>? RideStateChanged;

	/// <summary>
	/// A route was attached to or detached from the ride (§5.4).
	/// <para>
	/// Carries the ride, not the routes — the receiver refetches, because the lines are the
	/// largest thing a ride owns and they change rarely. Replaces the single-route
	/// <c>RouteUpdated(rideId, trackId)</c> this used to declare: a ride carries a set of
	/// routes now, so "the route was replaced" is no longer a thing that can happen.
	/// </para>
	/// </summary>
	event Action<Guid>? RoutesChanged;

	/// <summary>A pending request arrived — organiser only (§5.2).</summary>
	event Action<Guid, JoinRequestSummary>? JoinRequestReceived;

	/// <summary>An organiser decided on a request the caller made.</summary>
	event Action<Guid, JoinResult>? JoinRequestDecided;

	/// <summary>
	/// The asker took their own request back (§5.2). Sent to the people who may decide, for the
	/// same reason the two above are: it is their waiting count it moves.
	/// </summary>
	event Action<Guid, Guid>? JoinRequestWithdrawn;

	/// <summary>A marker was added, updated or removed (§16.6).</summary>
	event Action<Guid, MarkerDto>? MarkerAdded;
	event Action<Guid, MarkerDto>? MarkerUpdated;
	event Action<Guid, Guid>? MarkerRemoved;

	/// <summary>A comment was posted / edited / removed / pinned (§17.8, §6.2).</summary>
	/// <remarks>
	/// No thread id is carried because the SignalR group already scopes every message to the
	/// adventure or the route the client joined via <see cref="JoinRideAsync"/> or
	/// <see cref="JoinTrackAsync"/>. A subscriber that is watching both — which nothing does
	/// today, but the events are process-wide — tells them apart by the <c>GroupRideId</c> and
	/// <c>TrackId</c> on the payload. Matches the server's
	/// <c>IRideClient.CommentPosted/Edited/Removed/PinChanged</c> exactly — a mismatch there
	/// used to leave subscribers silent because SignalR could not bind the incoming payload.
	/// </remarks>
	event Action<CommentDto>? CommentPosted;
	event Action<CommentDto>? CommentEdited;
	event Action<Guid>? CommentRemoved;
	event Action<Guid, bool>? CommentPinChanged;

	/// <summary>Reactions for one comment changed (§17.4). Coalesced by the server.</summary>
	event Action<Guid, ReactionCounts>? ReactionsUpdated;

	/// <summary>Poll tally for one comment changed (§17.5).</summary>
	event Action<Guid, PollResults>? PollUpdated;

	/// <summary>The organiser's content switches changed (§5.8).</summary>
	event Action<Guid, RidePermissions>? PermissionsChanged;

	/// <summary>The organiser opened a wind-down (§5.6).</summary>
	event Action<Guid, DateTimeOffset>? SharingWindDownStarted;

	/// <summary>A member turned sharing on or off (§5.6).</summary>
	event Action<Guid, Guid, bool>? MemberSharingChanged;

	/// <summary>
	/// A member entered or left their own private area (§10.1, §5.6) — ride, rider, and whether they
	/// are now private.
	/// <para>
	/// The payload is that one bit and nothing else. While it is set the ride holds no position for
	/// them at all, so this is the difference between a member row that reads "no signal" — wait at
	/// the junction — and one that reads "private", which is somebody at home who will be along.
	/// </para>
	/// </summary>
	event Action<Guid, Guid, bool>? MemberPrivacyChanged;
}
