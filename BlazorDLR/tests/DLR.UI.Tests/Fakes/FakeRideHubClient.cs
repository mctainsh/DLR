using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Rides;

namespace DLR.UI.Tests.Fakes;

/// <summary>
/// A no-op <see cref="IRideHubClient"/> that records connect/join/leave calls and lets
/// tests fire events by hand. bUnit renders shared components against this so a hub
/// event coming from the wire and a test-driven event go through the same handler.
/// </summary>
public sealed class FakeRideHubClient : IRideHubClient
{
	public bool IsConnected { get; private set; }

	public int ConnectCount { get; private set; }
	public List<Guid> Joined { get; } = new();
	public List<Guid> Left { get; } = new();

	// The interface declares many events; tests raise them via the Raise* helpers below.
#pragma warning disable CS0067
	public event Action<PositionBatch>? PositionsUpdated;
	public event Action<Guid, RideMemberSummary>? MemberJoined;
	public event Action<Guid, Guid>? MemberLeft;
	public event Action<Guid>? RoutesChanged;
	public event Action<Guid, JoinRequestSummary>? JoinRequestReceived;
	public event Action<Guid, JoinResult>? JoinRequestDecided;
	public event Action<Guid, Guid>? JoinRequestWithdrawn;
	public event Action<Guid, MarkerDto>? MarkerAdded;
	public event Action<Guid, MarkerDto>? MarkerUpdated;
	public event Action<Guid, Guid>? MarkerRemoved;
	public event Action<CommentDto>? CommentPosted;
	public event Action<CommentDto>? CommentEdited;
	public event Action<Guid>? CommentRemoved;
	public event Action<Guid, bool>? CommentPinChanged;
	public event Action<Guid, ReactionCounts>? ReactionsUpdated;
	public event Action<Guid, PollResults>? PollUpdated;
	public event Action<Guid, RidePermissions>? PermissionsChanged;
	public event Action<Guid, Guid, bool>? MemberSharingChanged;
	public event Action<Guid, Guid, bool>? MemberPrivacyChanged;
#pragma warning restore CS0067

	/// <summary>Raised by <see cref="ConnectAsync"/> and by <see cref="SetConnected"/>.</summary>
	public event Action? ConnectionChanged;

	/// <summary>
	/// Drops or restores the connection, the way a tunnel does. Raises
	/// <see cref="ConnectionChanged"/> however the flag moved, so a screen watching it can take
	/// its warning down as well as put it up.
	/// </summary>
	/// <param name="connected">Whether the hub is up.</param>
	public void SetConnected(bool connected)
	{
		IsConnected = connected;
		ConnectionChanged?.Invoke();
	}

	public Task ConnectAsync(CancellationToken cancellationToken = default)
	{
		ConnectCount++;
		IsConnected = true;
		ConnectionChanged?.Invoke();
		return Task.CompletedTask;
	}

	public Task JoinRideAsync(Guid rideId, CancellationToken cancellationToken = default)
	{
		Joined.Add(rideId);
		return Task.CompletedTask;
	}

	public Task LeaveRideAsync(Guid rideId, CancellationToken cancellationToken = default)
	{
		Left.Add(rideId);
		return Task.CompletedTask;
	}

	/// <summary>Shared routes whose threads were joined, in order (§6.2).</summary>
	public List<Guid> JoinedTracks { get; } = [];

	/// <summary>Shared routes whose threads were left, in order.</summary>
	public List<Guid> LeftTracks { get; } = [];

	public Task JoinTrackAsync(Guid trackId, CancellationToken cancellationToken = default)
	{
		JoinedTracks.Add(trackId);
		return Task.CompletedTask;
	}

	public Task LeaveTrackAsync(Guid trackId, CancellationToken cancellationToken = default)
	{
		LeftTracks.Add(trackId);
		return Task.CompletedTask;
	}

	/// <summary>Every fix the device published through the hub, in order (§5.7).</summary>
	public List<PositionUpdate> Published { get; } = [];

	/// <summary>Set to make the hub refuse a publish — the reconnecting case the REST path covers.</summary>
	public Exception? PublishException { get; set; }

	/// <summary>
	/// Set to make a publish never answer — a socket that has gone quiet without closing, which
	/// is what a cell radio does at speed and what <c>LocationBroadcastState.SendTimeout</c> is
	/// for. The send completes only when the caller's own token cancels it.
	/// </summary>
	public bool PublishHangs { get; set; }

	/// <summary>
	/// How many position publishes have been *started* here, including ones that hung or threw.
	/// A test that needs a send to be in flight before it moves the clock waits on this — the
	/// deadline is armed immediately before the call, so an attempt observed is a timer running.
	/// </summary>
	public int PublishAttempts { get; private set; }

	public Task PublishPositionAsync(PositionUpdate update, CancellationToken cancellationToken = default)
	{
		PublishAttempts++;

		if (PublishHangs)
		{
			return Task.Delay(Timeout.Infinite, cancellationToken);
		}

		if (PublishException is not null)
		{
			return Task.FromException(PublishException);
		}

		Published.Add(update);
		return Task.CompletedTask;
	}

	/// <summary>Every private-area crossing the device announced through the hub, in order (§10.1).</summary>
	public List<PositionPrivacyUpdate> PublishedPrivacy { get; } = [];

	public Task PublishPrivacyAsync(PositionPrivacyUpdate update, CancellationToken cancellationToken = default)
	{
		if (PublishException is not null)
		{
			return Task.FromException(PublishException);
		}

		PublishedPrivacy.Add(update);
		return Task.CompletedTask;
	}

	public ValueTask DisposeAsync()
	{
		IsConnected = false;
		return ValueTask.CompletedTask;
	}

	// Test-raise helpers. Kept on the fake rather than on the interface, because a component
	// listening on the interface has no reason to be able to raise its own events.
	public void RaiseCommentPosted(CommentDto comment) => CommentPosted?.Invoke(comment);
	public void RaiseReactionsUpdated(Guid commentId, ReactionCounts counts) => ReactionsUpdated?.Invoke(commentId, counts);
	public void RaisePermissionsChanged(Guid rideId, RidePermissions permissions) => PermissionsChanged?.Invoke(rideId, permissions);
	public void RaiseRoutesChanged(Guid rideId) => RoutesChanged?.Invoke(rideId);
	public void RaiseMemberJoined(Guid rideId, RideMemberSummary member) => MemberJoined?.Invoke(rideId, member);

	/// <summary>The asker took their own request back (§5.2).</summary>
	public void RaiseJoinRequestWithdrawn(Guid rideId, Guid requestId) =>
		JoinRequestWithdrawn?.Invoke(rideId, requestId);
	public void RaiseMemberLeft(Guid rideId, Guid userId) => MemberLeft?.Invoke(rideId, userId);
	public void RaiseMemberSharingChanged(Guid rideId, Guid userId, bool sharing) => MemberSharingChanged?.Invoke(rideId, userId, sharing);
	public void RaiseMemberPrivacyChanged(Guid rideId, Guid userId, bool isPrivate) => MemberPrivacyChanged?.Invoke(rideId, userId, isPrivate);
	public void RaiseMarkerAdded(Guid rideId, MarkerDto marker) => MarkerAdded?.Invoke(rideId, marker);
	public void RaiseMarkerRemoved(Guid rideId, Guid markerId) => MarkerRemoved?.Invoke(rideId, markerId);
	public void RaisePositionsUpdated(PositionBatch batch) => PositionsUpdated?.Invoke(batch);
}
