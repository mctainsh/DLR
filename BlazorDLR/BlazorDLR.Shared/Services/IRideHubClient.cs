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

	/// <summary>Open the connection.</summary>
	Task ConnectAsync(CancellationToken cancellationToken = default);

	/// <summary>Subscribe to a ride's group so its members' events reach this client.</summary>
	Task JoinRideAsync(Guid rideId, CancellationToken cancellationToken = default);

	/// <summary>Unsubscribe.</summary>
	Task LeaveRideAsync(Guid rideId, CancellationToken cancellationToken = default);

	/// <summary>Publish this device's position — one push, fanned out to every ride the rider is live in (§5.7).</summary>
	Task PublishPositionAsync(PositionUpdate update, CancellationToken cancellationToken = default);

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

	/// <summary>A marker was added, updated or removed (§16.6).</summary>
	event Action<Guid, MarkerDto>? MarkerAdded;
	event Action<Guid, MarkerDto>? MarkerUpdated;
	event Action<Guid, Guid>? MarkerRemoved;

	/// <summary>A comment was posted / edited / removed / pinned (§17.8).</summary>
	/// <remarks>
	/// The ride id is not carried because the SignalR group already scopes every message to
	/// the ride the client joined via <see cref="JoinRideAsync"/>. Matches the server's
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
}
