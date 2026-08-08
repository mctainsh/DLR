using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Rides;
using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;

namespace BlazorDLR.Shared.State;

/// <summary>
/// One group ride, held for as long as a screen is looking at it (§5.3).
/// <para>
/// The live-ride map and the ride's info page are two views of the same thing: the same
/// snapshot, the same hub deltas on top, the same organiser actions. Both used to be one
/// component; splitting the screen in two meant either duplicating ~200 lines of hub
/// wiring or lifting it here. This is that lift — the pages below it hold layout and
/// nothing else.
/// </para>
/// <para>
/// Not a DI service. A session is scoped to the ride a page is showing, so a page news one
/// up in <c>OnInitialized</c> and disposes it on the way out; registering it in the
/// container would make it either a singleton shared between rides or a transient nobody
/// owns. §5.3's rule holds throughout: the snapshot is authoritative and the hub is the
/// delta on top.
/// </para>
/// </summary>
public sealed class RideSession : IAsyncDisposable
{
	private readonly IApiClient _api;
	private readonly IRideHubClient _hub;
	private readonly AuthState _auth;

	private readonly Dictionary<Guid, RiderPositionDto> _positions = new();
	private readonly Dictionary<Guid, MarkerDto> _markers = new();

	private Guid _rideId;
	private bool _joined;
	private bool _disposed;

	public RideSession(IApiClient api, IRideHubClient hub, AuthState auth)
	{
		_api = api;
		_hub = hub;
		_auth = auth;

		WireHub();
	}

	/// <summary>Raised whenever anything below it changed. Pages re-render off this and nothing else.</summary>
	public event Action? Changed;

	/// <summary>Which ride, once <see cref="LoadAsync"/> has been called.</summary>
	public Guid RideId => _rideId;

	/// <summary>The ride as the server last described it, or null while loading or on error.</summary>
	public RideDetail? Ride { get; private set; }

	/// <summary>What went wrong, in the words the server used. Null when nothing has.</summary>
	public string? Error { get; private set; }

	/// <summary>Whether a mutating call is in flight — the caller's cue to disable its controls.</summary>
	public bool Busy { get; private set; }

	/// <summary>Every sharing member's latest fix, keyed by user (§5.3).</summary>
	public IReadOnlyDictionary<Guid, RiderPositionDto> Positions => _positions;

	/// <summary>Every marker on the ride, keyed by id (§16.5).</summary>
	public IReadOnlyDictionary<Guid, MarkerDto> Markers => _markers;

	/// <summary>
	/// The planned route's raw points, when the ride has one. §5.4's gap list needs a route to
	/// project against; today the hub tells us via RouteUpdated when the route changes but the
	/// initial RideDetail does not carry the route id, so the initial load path leaves this null
	/// until the route arrives on the hub — see SharedFrontend.md Phase 3 for the deferred server
	/// change that would let the initial fetch populate it immediately.
	/// </summary>
	public IReadOnlyList<TrackPoint>? RoutePolyline { get; private set; }

	/// <summary>When the organiser's wind-down force-stops sharing, or null when none is open (§5.6).</summary>
	public DateTimeOffset? WindDownEndsUtc { get; private set; }

	/// <summary>Whether this rider is broadcasting to this ride.</summary>
	public bool Sharing { get; private set; }

	/// <summary>Whether this rider runs the ride. False before the snapshot lands.</summary>
	public bool IsOrganiser => Ride?.IsOrganiser == true;

	/// <summary>
	/// Who may remove a marker: the rider who placed it, or the ride's organiser (§16.5).
	/// Mirrors <c>MarkerController.CanWriteAsync</c> so the button is simply absent rather than
	/// there and 403-ing — the server stays the one that decides either way.
	/// </summary>
	public bool CanDelete(MarkerDto marker) =>
		marker.CreatedByUserId == _auth.UserId || IsOrganiser;

	/// <summary>
	/// Oldest first, which is the order the list endpoint returns (§16.5). Sorting on read rather
	/// than keeping a parallel list means a marker arriving on the hub lands in the same place it
	/// would have on a reload, and an edit never shuffles the rows underneath the person reading.
	/// </summary>
	public IEnumerable<MarkerDto> OrderedMarkers => _markers.Values.OrderBy(marker => marker.CreatedUtc);

	/// <summary>
	/// Fetches the snapshot for <paramref name="rideId"/> and joins its hub group. Safe to call
	/// again for a different ride — the previous ride's group is left first.
	/// </summary>
	public async Task LoadAsync(Guid rideId)
	{
		await LeaveAsync();

		_rideId = rideId;
		Ride = null;
		Error = null;
		RoutePolyline = null;
		WindDownEndsUtc = null;
		Sharing = false;
		_positions.Clear();
		_markers.Clear();
		Raise();

		try
		{
			Ride = await _api.GetRideAsync(rideId);
			Sharing = Ride.Members.FirstOrDefault(member => member.UserId == _auth.UserId)?.Sharing ?? false;

			// Positions snapshot: on load we ask for what everyone's fix is now (§5.3).
			foreach (RiderPositionDto position in await _api.GetPositionsSnapshotAsync(rideId))
			{
				_positions[position.UserId] = position;
			}

			foreach (MarkerDto marker in await _api.ListRideMarkersAsync(rideId))
			{
				_markers[marker.Id] = marker;
			}

			Raise();

			try
			{
				await _hub.ConnectAsync();
				await _hub.JoinRideAsync(rideId);
				_joined = true;
			}
			catch
			{
				// The realtime updates are a nice-to-have on top of the snapshot; if the hub is
				// unreachable the page still shows what the API returned. §5.3 sets the shape:
				// the snapshot is authoritative, the hub is the delta on top.
			}
		}
		catch (ApiException apiException)
		{
			Error = apiException.Error.Title;
			Raise();
		}
		catch (Exception exception)
		{
			Error = exception.Message;
			Raise();
		}
	}

	// -- Actions ------------------------------------------------------------------------------

	/// <summary>Turns this rider's broadcast on or off (§5.6).</summary>
	public Task SetSharingAsync(bool share) =>
		RunAsync(async () =>
		{
			await _api.SetSharingAsync(_rideId, new SetSharingRequest(share));
			Sharing = share;
		});

	/// <summary>Organiser only: Open → Live (§5.1).</summary>
	public Task StartAsync() => RunAsync(() => _api.StartRideAsync(_rideId));

	/// <summary>Organiser only: Live → Completed, immediately or on a wind-down (§5.6).</summary>
	public Task EndAsync(RideEndingDto ending) =>
		RunAsync(() => _api.EndRideAsync(_rideId, new EndRideRequest(ending)));

	/// <summary>
	/// Removes a marker. The server broadcasts MarkerRemoved to the ride group and
	/// <see cref="OnMarkerRemoved"/> would do exactly this — but only for a client whose hub
	/// connection came up, so the caller drops its own copy too. Both paths are one dictionary
	/// remove and are happy to run one after the other.
	/// </summary>
	public async Task DeleteMarkerAsync(Guid markerId)
	{
		try
		{
			await _api.DeleteMarkerAsync(markerId);
			_markers.Remove(markerId);
		}
		catch (ApiException apiException)
		{
			Error = apiException.Error.Title;
		}

		Raise();
	}

	/// <summary>Clears a stated error once the person has had a chance to read it.</summary>
	public void ClearError()
	{
		if (Error is null)
		{
			return;
		}

		Error = null;
		Raise();
	}

	private async Task RunAsync(Func<Task> action)
	{
		Busy = true;
		Raise();
		try
		{
			await action();
		}
		catch (ApiException apiException)
		{
			Error = apiException.Error.Title;
		}
		finally
		{
			Busy = false;
			Raise();
		}
	}

	// -- Hub deltas ---------------------------------------------------------------------------

	private void WireHub()
	{
		_hub.PositionsUpdated += OnPositions;
		_hub.MemberJoined += OnMemberJoined;
		_hub.MemberLeft += OnMemberLeft;
		_hub.MemberSharingChanged += OnMemberSharing;
		_hub.MarkerAdded += OnMarkerUpserted;
		_hub.MarkerUpdated += OnMarkerUpserted; // same treatment — upsert
		_hub.MarkerRemoved += OnMarkerRemoved;
		_hub.RouteUpdated += OnRouteUpdated;
		_hub.RideStateChanged += OnRideStateChanged;
		_hub.SharingWindDownStarted += OnWindDownStarted;
		_hub.PermissionsChanged += OnPermissionsChanged;
	}

	private void UnwireHub()
	{
		_hub.PositionsUpdated -= OnPositions;
		_hub.MemberJoined -= OnMemberJoined;
		_hub.MemberLeft -= OnMemberLeft;
		_hub.MemberSharingChanged -= OnMemberSharing;
		_hub.MarkerAdded -= OnMarkerUpserted;
		_hub.MarkerUpdated -= OnMarkerUpserted;
		_hub.MarkerRemoved -= OnMarkerRemoved;
		_hub.RouteUpdated -= OnRouteUpdated;
		_hub.RideStateChanged -= OnRideStateChanged;
		_hub.SharingWindDownStarted -= OnWindDownStarted;
		_hub.PermissionsChanged -= OnPermissionsChanged;
	}

	private void OnPositions(PositionBatch batch)
	{
		if (batch.RideId != _rideId) return;

		foreach (PositionFix fix in batch.Positions)
		{
			_positions[fix.UserId] = new RiderPositionDto(
				fix.UserId,
				_positions.TryGetValue(fix.UserId, out RiderPositionDto? existing) ? existing.UserName : string.Empty,
				fix.Lat, fix.Lon, fix.SpeedMps, fix.HeadingDeg, fix.RecordedUtc);
		}

		Raise();
	}

	private void OnMemberJoined(Guid rideId, RideMemberSummary member)
	{
		if (rideId != _rideId || Ride is null) return;
		Ride = Ride with { Members = Ride.Members.Concat(new[] { member }).ToList() };
		Raise();
	}

	private void OnMemberLeft(Guid rideId, Guid userId)
	{
		if (rideId != _rideId || Ride is null) return;
		Ride = Ride with { Members = Ride.Members.Where(member => member.UserId != userId).ToList() };
		_positions.Remove(userId);
		Raise();
	}

	private void OnMemberSharing(Guid rideId, Guid userId, bool sharing)
	{
		if (rideId != _rideId || Ride is null) return;

		Ride = Ride with
		{
			Members = Ride.Members
				.Select(member => member.UserId == userId ? member with { Sharing = sharing } : member)
				.ToList(),
		};

		if (!sharing)
		{
			_positions.Remove(userId);
		}

		Raise();
	}

	private void OnMarkerUpserted(Guid rideId, MarkerDto marker)
	{
		if (rideId != _rideId) return;
		_markers[marker.Id] = marker;
		Raise();
	}

	private void OnMarkerRemoved(Guid rideId, Guid markerId)
	{
		if (rideId != _rideId) return;
		_markers.Remove(markerId);
		Raise();
	}

	private void OnRideStateChanged(Guid rideId, RideStateDto state)
	{
		if (rideId != _rideId || Ride is null) return;
		Ride = Ride with { State = state };
		Raise();
	}

	private async void OnRouteUpdated(Guid rideId, Guid trackId)
	{
		if (rideId != _rideId) return;

		try
		{
			// The polyline the server hands us here is simplified for display, and that is fine
			// for the gap list — GapCalculator's error against a simplified line is bounded by
			// the simplifier's tolerance (§15.5), which is well below the off-route threshold.
			TrackDetail detail = await _api.GetTrackAsync(trackId);
			RoutePolyline = detail.Polyline;
		}
		catch
		{
			// A route the client cannot fetch is treated the same as no route: the gap list stays
			// empty rather than showing wrong numbers. A retry happens automatically on the next
			// RouteUpdated event.
			RoutePolyline = null;
		}

		Raise();
	}

	private void OnWindDownStarted(Guid rideId, DateTimeOffset endsUtc)
	{
		if (rideId != _rideId) return;
		WindDownEndsUtc = endsUtc;
		Raise();
	}

	private void OnPermissionsChanged(Guid rideId, RidePermissions permissions)
	{
		if (rideId != _rideId || Ride is null) return;
		Ride = Ride with { Permissions = permissions };
		Raise();
	}

	private void Raise() => Changed?.Invoke();

	private async Task LeaveAsync()
	{
		if (!_joined)
		{
			return;
		}

		_joined = false;
		try { await _hub.LeaveRideAsync(_rideId); }
		catch { /* connection may already be down */ }
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		UnwireHub();
		Changed = null;
		await LeaveAsync();
	}
}
