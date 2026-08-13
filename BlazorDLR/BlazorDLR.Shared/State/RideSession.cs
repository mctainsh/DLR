using System.Net;
using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Rides;
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
	private readonly LocationBroadcastState? _broadcast;
	private readonly RideSnapshotCache? _cache;

	private readonly Dictionary<Guid, RiderPositionDto> _positions = new();
	private readonly Dictionary<Guid, MarkerDto> _markers = new();

	private Guid _rideId;
	private bool _joined;
	private bool _disposed;

	/// <summary>Opens a session over one ride.</summary>
	/// <param name="api">The REST client.</param>
	/// <param name="hub">The realtime channel (§5.3).</param>
	/// <param name="auth">Who is reading, which is what "mine" means throughout.</param>
	/// <param name="broadcast">
	/// The device's position broadcast (§5.7), or <c>null</c> for a caller that does not have one.
	/// <para>
	/// <strong>Why the sharing switch reaches the GPS from here.</strong> Sharing is a server flag
	/// and the receiver is a device concern, and the two have to agree or the app lies in one of
	/// two directions: a switch that is on with the GPS off shows the rider as sharing while their
	/// pin never moves, and a receiver left running for a ride nobody is sharing with is a
	/// foreground service and a battery bill for nothing. This is the one place both the toggle and
	/// the load of an already-sharing ride pass through, so it is the one place they are kept in
	/// step — rather than each of the two pages remembering to do it.
	/// </para>
	/// <para>
	/// Optional so a test that only cares about markers or members can leave it out.
	/// </para>
	/// </param>
	/// <param name="cache">
	/// This device's copy of the ride (§4.4), or <c>null</c> for a caller that keeps none.
	/// <para>
	/// A successful load writes through it, and a load that could not reach the server reads back
	/// from it — see <see cref="LoadAsync"/>. Optional for the same reason
	/// <paramref name="broadcast"/> is, and null on the browser hosts in all but name: the store
	/// behind it answers "nothing" there (§18.6).
	/// </para>
	/// </param>
	public RideSession(
		IApiClient api,
		IRideHubClient hub,
		AuthState auth,
		LocationBroadcastState? broadcast = null,
		RideSnapshotCache? cache = null)
	{
		_api = api;
		_hub = hub;
		_auth = auth;
		_broadcast = broadcast;
		_cache = cache;

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

	/// <summary>
	/// Whether the last load was told this ride is not there for this rider — it was deleted, or
	/// they are no longer a member of it (§5.2).
	/// <para>
	/// <strong>Not the same as <see cref="Error"/> being set.</strong> A tunnel, a dead Wi-Fi
	/// captive portal and a server restart all set an error too, and none of them is a reason to
	/// act as though the ride is gone. Only the server saying so — a 404, which is also the answer
	/// a non-member gets so that a ride id cannot be probed for existence, or a 403 — sets this.
	/// </para>
	/// <para>
	/// What acts on it is <c>CurrentRideState</c>: the rail's globe must not keep leading back to a
	/// ride the rider has been removed from for the rest of the app's life (§18.6).
	/// </para>
	/// </summary>
	public bool RideUnavailable { get; private set; }

	/// <summary>
	/// Whether what is on screen came out of this device's own copy rather than off the wire
	/// (§4.4) — the server could not be reached, and the last snapshot it gave us was drawn
	/// instead.
	/// <para>
	/// <strong>Screens must say so.</strong> A map of where everybody was twenty minutes ago is
	/// the most useful thing a rider who has lost signal can be shown and the most dangerous
	/// thing to mistake for live, and the difference between the two is entirely whether the app
	/// said which one it was. <see cref="CachedUtc"/> is the other half of that sentence.
	/// </para>
	/// <para>
	/// Cleared by the next load that reaches the server. Note that a hub that comes up while this
	/// is set will start moving pins around underneath it — that is a good thing, and it is why
	/// <see cref="CachedUtc"/> is stated as an age rather than the whole screen being labelled
	/// stale.
	/// </para>
	/// </summary>
	public bool LoadedFromCache { get; private set; }

	/// <summary>
	/// When the server last confirmed what is on screen, or <c>null</c> when it came off the wire
	/// just now. Only ever set alongside <see cref="LoadedFromCache"/>.
	/// </summary>
	public DateTimeOffset? CachedUtc { get; private set; }

	/// <summary>Whether a mutating call is in flight — the caller's cue to disable its controls.</summary>
	public bool Busy { get; private set; }

	/// <summary>Every sharing member's latest fix, keyed by user (§5.3).</summary>
	public IReadOnlyDictionary<Guid, RiderPositionDto> Positions => _positions;

	/// <summary>Every marker on the ride, keyed by id (§16.5).</summary>
	public IReadOnlyDictionary<Guid, MarkerDto> Markers => _markers;

	/// <summary>
	/// The ride's planned routes, oldest attachment first (§5.4). Empty until the snapshot lands,
	/// and empty for a ride nobody has attached anything to.
	/// </summary>
	public IReadOnlyList<RideRoute> Routes { get; private set; } = [];

	/// <summary>
	/// The points §5.4's gap list projects riders against, or null when the ride has no routes.
	/// <para>
	/// <strong>The first route, when there are several.</strong> "Distance along the route" needs
	/// one line to be along, and the oldest attachment is the stable choice — it does not move when
	/// the organiser adds the long option the night before, so nobody's place in the list changes
	/// because somebody else attached a file.
	/// </para>
	/// </summary>
	public IReadOnlyList<TrackPoint>? RoutePolyline { get; private set; }

	/// <summary>When the organiser's wind-down force-stops sharing, or null when none is open (§5.6).</summary>
	public DateTimeOffset? WindDownEndsUtc { get; private set; }

	/// <summary>Whether this rider is broadcasting to this ride.</summary>
	public bool Sharing { get; private set; }

	/// <summary>
	/// Whether this ride is at a point in its lifecycle where a published fix lands in it at all
	/// (§5.1).
	/// <para>
	/// <strong>The client mirrors the server's rule because the server's rule is silent.</strong>
	/// <c>PositionStore.PublishAsync</c> writes a fix only into rides that are <c>Live</c> or inside
	/// an unexpired wind-down; for anything earlier it accepts the publish and drops it. A device
	/// that broadcasts anyway gets no error, no pin and no explanation — it just runs a foreground
	/// service and a GPS on a rider's battery for fixes nothing keeps.
	/// </para>
	/// <para>
	/// Only <c>Draft</c> and <c>Open</c> are refused here, which is narrower than the server's test
	/// and deliberately so: <c>Completed</c> covers the wind-down (§5.6), and
	/// <see cref="RideDetail"/> carries no end time, so a client that relaunched mid-wind-down
	/// cannot tell a live one from an expired one. Refusing on that guess would take a rider off
	/// the map during exactly the window the wind-down exists to protect;
	/// <see cref="OnRideStateChanged"/> is what stops a ride that ended without one.
	/// </para>
	/// </summary>
	public bool RideCarriesPositions => Ride is { State: not (RideStateDto.Draft or RideStateDto.Open) };

	/// <summary>
	/// Whether this rider has turned sharing on for a ride that cannot carry positions yet — the
	/// state the ride screens say out loud rather than leaving a rider to infer it from a pin that
	/// never appears.
	/// </summary>
	public bool SharingPending => Sharing && !RideCarriesPositions;

	/// <summary>Whether this rider runs the ride. False before the snapshot lands.</summary>
	public bool IsOrganiser => Ride?.IsOrganiser == true;

	/// <summary>
	/// Who may attach or detach a planned route: the organiser, or a leader (§5.4).
	/// <para>
	/// Mirrors <c>RideRouteController.AuthoriseWriteAsync</c> so the controls are simply absent
	/// rather than there and 403-ing — the same arrangement as <see cref="CanDelete"/>, and for
	/// the same reason: the server stays the one that decides either way.
	/// </para>
	/// </summary>
	public bool CanManageRoutes =>
		IsOrganiser
		|| string.Equals(
			Ride?.Members.FirstOrDefault(member => member.UserId == _auth.UserId)?.Role,
			"Leader",
			StringComparison.Ordinal);

	/// <summary>
	/// One member of the ride, or null for a user id the snapshot does not know about.
	/// <para>
	/// The live map asks this per rider per repaint, for the name and the colour their marker is
	/// drawn in (§16.3) — neither of which travels with a position fix. A scan rather than an
	/// index: a ride is capped at fifty members, and an index would be a second copy of the member
	/// list to keep in step with every join, departure and sharing change on the hub.
	/// </para>
	/// </summary>
	/// <param name="userId">Which rider.</param>
	/// <returns>Their member row, or null before the snapshot lands.</returns>
	public RideMemberSummary? Member(Guid userId) =>
		Ride?.Members.FirstOrDefault(member => member.UserId == userId);

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
	/// <para>
	/// A load that succeeds writes what it got to this device (§4.4); a load that could not reach
	/// the server reads it back — see <see cref="LoadFromCacheAsync"/>. The two failure modes are
	/// kept strictly apart: the server <em>saying</em> the ride is not there is never answered
	/// from a cache, because the copy would be of a ride this rider is no longer on.
	/// </para>
	/// </summary>
	public async Task LoadAsync(Guid rideId)
	{
		await LeaveAsync();

		_rideId = rideId;
		Ride = null;
		Error = null;
		RideUnavailable = false;
		LoadedFromCache = false;
		CachedUtc = null;
		Routes = [];
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

			// The server is authoritative about sharing, so this is where a relaunched app learns
			// that its receiver should be on: the flag survived the process, the GPS did not. It is
			// also why the globe (§18.6) matters — opening the ride is what gets the rider back on
			// the map after the OS reclaimed the app mid-ride.
			//
			// Started, not awaited. Bringing a receiver up can put a modal on screen (the
			// background-location disclosure) and can wait on a platform permission dialog, and
			// neither is something a ride's snapshot should be held behind — the map has landed and
			// belongs on screen. The receiver reports its own progress through
			// LocationBroadcastState, which is what the ride screens render.
			if (Sharing)
			{
				StartBroadcast(rideId);
			}

			// Positions snapshot: on load we ask for what everyone's fix is now (§5.3).
			foreach (RiderPositionDto position in await _api.GetPositionsSnapshotAsync(rideId))
			{
				_positions[position.UserId] = position;
			}

			foreach (MarkerDto marker in await _api.ListRideMarkersAsync(rideId))
			{
				_markers[marker.Id] = marker;
			}

			// The ride-scoped route endpoint, not GET /tracks/{id} — that one is owner-scoped and
			// answers 404 to every member but the organiser (§15.4). Fetched here rather than
			// waiting for a hub event, so a ride opened with the hub unreachable still draws its
			// routes: §5.3's rule is that the snapshot is authoritative and the hub is the delta.
			ApplyRoutes(await _api.ListRideRoutesAsync(rideId));

			// Everything landed, so this is a whole ride and worth keeping. After the four calls
			// and before the hub, so the copy is exactly what the server said and carries none of
			// the deltas that arrive on top of it — §5.3's rule, applied to storage: the snapshot
			// is authoritative, and a snapshot with a few minutes of hub events folded into it is
			// no longer a snapshot of anything the server would agree with.
			//
			// Awaited, unlike the receiver above: a file write costs microseconds, and abandoning
			// it would leave an unobserved task racing the next load of a different ride for the
			// same store.
			if (_cache is not null)
			{
				await _cache.WriteAsync(Ride, [.. _markers.Values], Routes, [.. _positions.Values]);
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

			// The snapshot is the only call whose failure says anything about whether the ride is
			// still the caller's. The three that follow it are about parts of a ride we have
			// already been handed — a 404 from the marker list is a missing endpoint, not a
			// missing ride — but they cannot 404 for a member anyway, and the first call is the
			// one that fails when the ride is gone.
			RideUnavailable = apiException.Error.StatusCode
				is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone;

			if (RideUnavailable)
			{
				// The ride is gone, or this rider is not on it. The stored copy describes a ride
				// they may not look at any more, so it goes with the membership — the alternative
				// is a removed rider still opening the map from a cache, which is the one thing a
				// removal has to be able to stop.
				await ForgetCacheAsync(rideId);
			}
			else
			{
				// The server answered, but not with a ride: a 500, a 429, a captive portal's
				// interception. The ride still exists as far as anybody knows, so this is the same
				// situation as a tunnel and gets the same answer.
				await LoadFromCacheAsync(rideId);
			}

			Raise();
		}
		catch (Exception exception)
		{
			// A transport failure — no status, no statement from the server. It stays an error and
			// nothing more: a ride must never be forgotten because a phone went through a tunnel.
			Error = exception.Message;

			// This is the case the cache exists for (§4.4): the rider is in a dead zone and the
			// ride they were on is the screen they need. See LoadFromCacheAsync for what the
			// error above turns into when there is a copy to draw.
			await LoadFromCacheAsync(rideId);

			Raise();
		}
	}

	/// <summary>
	/// Draws this ride out of the device's own copy, when the server could not be reached (§4.4).
	/// <para>
	/// Called from both failure paths above, and does nothing at all on a host with no store or a
	/// ride this device has never held — which leaves the stated error exactly as it was, because
	/// "the request failed" is still the whole truth there.
	/// </para>
	/// <para>
	/// <strong>The hub is deliberately not attempted.</strong> Reaching here means the network is
	/// not answering, and a SignalR connect against a dead link spends its whole timeout budget
	/// before failing — with the map, which is already fully drawable, held behind it. A rider who
	/// gets signal back re-opens the ride and takes the live path.
	/// </para>
	/// <para>
	/// <strong>The receiver still comes up.</strong> A phone with no signal still has a GPS, and
	/// the rider's own mark is drawn from the device's fix rather than the ride's copy of it — so
	/// following themselves along a cached route is exactly what still works out here. The
	/// publishes fail and <c>LocationBroadcastState</c> says so, which is the honest report.
	/// </para>
	/// </summary>
	/// <param name="rideId">The ride that could not be fetched.</param>
	private async Task LoadFromCacheAsync(Guid rideId)
	{
		if (_cache is null)
		{
			return;
		}

		RideSnapshot? snapshot = await _cache.ReadAsync(rideId);

		if (snapshot is null)
		{
			return;
		}

		Ride = snapshot.Ride;
		LoadedFromCache = true;
		CachedUtc = snapshot.CachedUtc;

		// The error the caller stated is replaced rather than kept beside this. A screen showing a
		// ride does not also need a transport exception's wording over the top of it — being on a
		// cached copy is the fact that matters, and LoadedFromCache is how the screen says it.
		Error = null;

		foreach (RiderPositionDto position in snapshot.Positions)
		{
			_positions[position.UserId] = position;
		}

		foreach (MarkerDto marker in snapshot.Markers)
		{
			_markers[marker.Id] = marker;
		}

		ApplyRoutes(snapshot.Routes);

		Sharing = snapshot.Ride.Members.FirstOrDefault(member => member.UserId == _auth.UserId)?.Sharing ?? false;

		if (Sharing)
		{
			StartBroadcast(rideId);
		}
	}

	/// <summary>Drops this device's copy of a ride, tolerating a host that keeps none.</summary>
	private async Task ForgetCacheAsync(Guid rideId)
	{
		if (_cache is not null)
		{
			await _cache.ForgetAsync(rideId);
		}
	}

	// -- Actions ------------------------------------------------------------------------------

	/// <summary>
	/// Turns this rider's broadcast on or off (§5.6), and starts or stops the device's receiver
	/// with it (§5.7).
	/// <para>
	/// The server first, the GPS second. The flag is what decides whether a fix is copied into this
	/// ride at all, so a receiver started before it was set would publish fixes the server drops —
	/// and, worse on the way out, a receiver stopped before the flag was cleared would leave the
	/// rider's last position sitting on everyone's map with nothing arriving to move it.
	/// </para>
	/// </summary>
	/// <param name="share">True to broadcast to this ride, false to stop.</param>
	public Task SetSharingAsync(bool share) =>
		RunAsync(async () =>
		{
			await _api.SetSharingAsync(_rideId, new SetSharingRequest(share));
			Sharing = share;

			if (share)
			{
				// Not awaited — see LoadAsync. The switch has already done its job; leaving it
				// spinning while a permission dialog is up would disable the screen behind it.
				StartBroadcast(_rideId);
			}
			else if (_broadcast is not null)
			{
				await _broadcast.StopSharingAsync(_rideId);
			}
		});

	/// <summary>
	/// Brings the device's receiver up for a ride without waiting for it.
	/// <para>
	/// The continuation is what makes it safe to abandon: <see cref="LocationBroadcastState"/>
	/// states its own failures, but an exception escaping the start would otherwise be an
	/// unobserved task exception with nowhere to surface.
	/// </para>
	/// </summary>
	private void StartBroadcast(Guid rideId)
	{
		if (_broadcast is not { } broadcast)
		{
			return;
		}

		if (!RideCarriesPositions)
		{
			// Consent stands — the flag is on the server and stays there — but the receiver does
			// not come up for a ride that has not started. See RideCarriesPositions: everything it
			// would produce would be dropped, and the rider would be paying a foreground service
			// and a GPS for it while the app told them they were sharing.
			//
			// OnRideStateChanged starts it the moment the organiser starts the ride, so nobody has
			// to come back and toggle anything.
			return;
		}

		_ = broadcast.ShareWithAsync(rideId).ContinueWith(
			started => Error ??= started.Exception?.GetBaseException().Message,
			CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted,
			TaskScheduler.Default);
	}

	/// <summary>Organiser only: Open → Live (§5.1).</summary>
	public Task StartAsync() => RunAsync(() => _api.StartRideAsync(_rideId));

	/// <summary>
	/// Organiser or leader: attaches one of their own tracks as a planned route (§5.4).
	/// <para>
	/// The server broadcasts <c>RideRoutesChanged</c> and <see cref="OnRoutesChanged"/> would
	/// refetch — but only for a client whose hub connection came up, so the caller applies the
	/// answer it was given. Both paths end at the same list.
	/// </para>
	/// </summary>
	/// <param name="trackId">Which of the caller's tracks.</param>
	public Task AddRouteAsync(Guid trackId) =>
		RunAsync(async () =>
		{
			await _api.AddRideRouteAsync(_rideId, new AddRideRouteRequest(trackId));
			ApplyRoutes(await _api.ListRideRoutesAsync(_rideId));
		});

	/// <summary>
	/// Organiser or leader: detaches a planned route (§5.4). The track itself is untouched — this
	/// removes it from the ride, not from the owner's library.
	/// </summary>
	/// <param name="trackId">Which route.</param>
	public Task RemoveRouteAsync(Guid trackId) =>
		RunAsync(async () =>
		{
			await _api.RemoveRideRouteAsync(_rideId, trackId);
			ApplyRoutes(await _api.ListRideRoutesAsync(_rideId));
		});

	/// <summary>
	/// Leaves the ride for good: the membership row goes, and the server deletes this rider's
	/// stored position on the way out (<c>DELETE /members/me</c>).
	/// <para>
	/// <strong>Not <see cref="LeaveAsync"/></strong>, which is the hub group this client is
	/// subscribed to and is undone by opening the ride again. This one is the rider saying they
	/// are not on the ride any more, and getting back on means a join code or a request (§5.2).
	/// </para>
	/// <para>
	/// Answers whether it worked, unlike the other actions here, because the caller has a
	/// navigation to decide on: leaving means the ride's screens are no longer the caller's to
	/// show, and a failed leave must leave them exactly where they were with the server's
	/// sentence on screen. The organiser is the case that matters — the server answers 409 rather
	/// than dissolving a ride nobody runs — though the control is not offered to them (§5.2).
	/// </para>
	/// </summary>
	/// <returns>True when the server accepted it.</returns>
	public async Task<bool> LeaveRideAsync()
	{
		Busy = true;
		Raise();
		try
		{
			await _api.LeaveRideAsync(_rideId);

			// This rider is no longer a member, so the ride's screens must stop claiming to be
			// showing one — the same state a load that 404s leaves behind, and what tells the
			// caller's CurrentRideState to forget it.
			RideUnavailable = true;

			// And the device's copy goes with the membership. Leaving it would let the ride open
			// offline from a cache after the rider had walked away from it (§4.4).
			await ForgetCacheAsync(_rideId);

			// And the receiver stops caring about this ride. It keeps running if another ride is
			// still being shared with — see LocationBroadcastState.StopSharingAsync.
			if (_broadcast is not null)
			{
				await _broadcast.StopSharingAsync(_rideId);
			}

			return true;
		}
		catch (ApiException apiException)
		{
			Error = apiException.Error.Title;
			return false;
		}
		finally
		{
			Busy = false;
			Raise();
		}
	}

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
		_hub.RoutesChanged += OnRoutesChanged;
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
		_hub.RoutesChanged -= OnRoutesChanged;
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

		if (userId == _auth.UserId)
		{
			// The member who left is the one reading the screen — an organiser removed them (§5.2).
			// The snapshot on screen describes a ride that is no longer theirs, and everything on
			// it is about to start answering 404, so the ride goes rather than being left as a
			// facsimile they can still tap around in.
			//
			// The server does not broadcast this today; a removed rider finds out on their next
			// load, which sets the same two fields from the 404. This is here so the moment the
			// hub does say it, the client is already right.
			Ride = null;
			Error = "You are no longer on this ride.";
			RideUnavailable = true;
			_positions.Clear();
			_markers.Clear();

			// Nothing this device sends would be copied into this ride any more, so the receiver
			// stops running for it — a removed rider must not go on paying battery for a ride they
			// are not on.
			Sharing = false;
			_ = _broadcast?.StopSharingAsync(_rideId);

			// Same reasoning as LeaveRideAsync: the copy goes with the membership. Fire-and-forget
			// because this is a hub callback with no caller to await it, and a delete that loses a
			// race with the app closing is retried by the next load's 404 anyway.
			_ = ForgetCacheAsync(_rideId);

			Raise();
			return;
		}

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

		// A ride ended immediately (§5.6) is one where the server has already cleared every
		// member's sharing flag and deleted their positions — so a receiver still running for it
		// is publishing into a ride that drops the fix, on a rider's battery.
		//
		// A wind-down is the opposite case and must not stop: its whole point is that riders who
		// kept sharing carry on until they stop or the window closes. WindDownEndsUtc is what
		// tells the two apart, and it is set by the hub event that precedes this one.
		if (state == RideStateDto.Completed && WindDownEndsUtc is null && Sharing)
		{
			Sharing = false;
			_ = _broadcast?.StopSharingAsync(_rideId);
		}

		// The other end of the same story: the organiser has started the ride, so a rider whose
		// consent was already on — and whose receiver StartBroadcast therefore declined to bring up
		// — is now on a ride that keeps what it is sent. Started here so nobody has to go back to
		// the switch and toggle it twice to get on a map they already said yes to.
		//
		// Ride has been updated to the new state above, so RideCarriesPositions agrees, and
		// ShareWithAsync is idempotent — a device already broadcasting for this ride does nothing.
		if (state == RideStateDto.Live && Sharing)
		{
			StartBroadcast(_rideId);
		}

		Raise();
	}

	private async void OnRoutesChanged(Guid rideId)
	{
		if (rideId != _rideId) return;

		try
		{
			ApplyRoutes(await _api.ListRideRoutesAsync(_rideId));
		}
		catch
		{
			// Routes the client cannot fetch leave the ones it already had on screen. Dropping
			// them would blank the map because a single request failed, and the next change to
			// the set — or the next load of the ride — refetches anyway.
		}

		Raise();
	}

	/// <summary>
	/// Takes a fetched route list and derives what §5.4 projects riders against.
	/// <para>
	/// The lines arrive encoded (§15.5) and are decoded through <see cref="PolylineCodec"/>,
	/// which is the encoder the server used — a second decoder is how a Sydney ride ended up
	/// drawn off the Gulf of Guinea once already.
	/// </para>
	/// </summary>
	private void ApplyRoutes(IReadOnlyList<RideRoute> routes)
	{
		Routes = routes;

		// The first route, and only the first. "Distance along the route" needs one line to be
		// along; the oldest attachment is the one that does not move when the organiser adds
		// another option. A simplified line is fine here — GapCalculator's error against it is
		// bounded by the simplifier's tolerance (§15.5), well below the off-route threshold.
		RoutePolyline = routes.Count == 0
			? null
			: [.. PolylineCodec
				.DecodePoints(routes[0].EncodedPolyline)
				.Select(point => new TrackPoint(point.Latitude, point.Longitude))];
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
