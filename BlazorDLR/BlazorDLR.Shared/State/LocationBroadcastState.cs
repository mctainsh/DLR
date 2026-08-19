using BlazorDLR.Shared.Diagnostics;
using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Rides;

namespace BlazorDLR.Shared.State;

/// <summary>
/// The device's own answer to "am I on the map right now?" — the one place a fix travels from the
/// GPS to the ride (§4.2, §4.3, §5.7).
/// <para>
/// <strong>One broadcast for every ride, not one per ride.</strong> A fix carries no ride id
/// (<see cref="PositionUpdate"/>): the device publishes once and the <em>server</em> copies it into
/// the rides this rider has consented to. So this state is a device-wide switch with a set of
/// reasons behind it — each ride that has sharing on is one reason — and it runs the GPS while at
/// least one reason stands. Publishing per ride would multiply a phone's uplink and battery by the
/// number of rides it is in, and would put the consent decision on the client, which is the side
/// that can get it wrong in the direction that leaks.
/// </para>
/// <para>
/// <strong>The order of the gates is the privacy model.</strong> Every fix passes
/// <see cref="PrivateAreaState.HidesLocation(LocationFix)"/> <em>before</em> anything else looks at
/// it (§10.1) — before the accuracy filter, before the cadence filter, and long before the network.
/// A fix from inside the rider's private area is not filtered, not queued and not retried: it is
/// dropped where it was read. And because that state answers "hide" until it has read the device,
/// a race at startup fails closed.
/// </para>
/// <para>
/// <strong>Only the MAUI host registers this.</strong> The web hosts register no GPS seam at all
/// (§18.6) — they used to bind a stub so the ride screens could <c>@inject</c> one, and the whole
/// of that arrangement bought a status line reading "this device cannot share its location". The
/// screens now resolve it with <c>GetService</c> and render their no-receiver branch when it comes
/// back null, which is how they ask about GPS without knowing which host they are on. On a MAUI
/// target with no receiver — the Windows and macOS heads — this is still registered over
/// <see cref="Platform.NoopLocationProvider"/> and reports
/// <see cref="LocationBroadcastStatus.NotSupported"/>.
/// </para>
/// </summary>
public sealed class LocationBroadcastState : IAsyncDisposable, IDisposable
{
	/// <summary>
	/// Marks that this device has been shown the background-location disclosure and accepted it.
	/// Device-local: it is a statement made to the person holding the phone, and a new phone has
	/// not been told anything.
	/// </summary>
	public const string DisclosureStorageKey = "dlr.location-disclosure";

	private readonly ILocationProvider _provider;
	private readonly IRideHubClient _hub;
	private readonly IApiClient _api;
	private readonly PrivateAreaState _privateAreas;
	private readonly GpsProfileState _profile;
	private readonly TrackRecordingState _recording;
	private readonly IDeviceSettings _settings;
	private readonly ConfirmService _confirm;
	private readonly TimeProvider _clock;

	/// <summary>
	/// The rides asking for this device to broadcast. A set rather than a counter: a ride whose
	/// page is opened twice must not leave a count that never returns to zero, and "which rides"
	/// is what the settings screen shows a rider who wants to know why their GPS is on.
	/// </summary>
	private readonly HashSet<Guid> _reasons = [];

	private CancellationTokenSource? _running;
	private Task? _pump;
	private bool _disposed;

	/// <summary>Creates the broadcaster over one host's seams.</summary>
	/// <param name="provider">The platform GPS. Non-functional on the web hosts (§18.6).</param>
	/// <param name="hub">The realtime channel a fix goes out on (§5.7).</param>
	/// <param name="api">The REST fallback for a fix the hub could not carry.</param>
	/// <param name="privateAreas">The §10.1 gate. Consulted before anything else touches a fix.</param>
	/// <param name="profile">The rider's accuracy profile (§4.2).</param>
	/// <param name="recording">The rider's own track (§15.1). Fed before either publish gate.</param>
	/// <param name="settings">Where the disclosure acknowledgement is remembered.</param>
	/// <param name="confirm">The app's one dialog, used for the disclosure below.</param>
	/// <param name="clock">Never the ambient clock (§10.4) — this stamps "last published".</param>
	public LocationBroadcastState(
		ILocationProvider provider,
		IRideHubClient hub,
		IApiClient api,
		PrivateAreaState privateAreas,
		GpsProfileState profile,
		TrackRecordingState recording,
		IDeviceSettings settings,
		ConfirmService confirm,
		TimeProvider clock)
	{
		_provider = provider;
		_hub = hub;
		_api = api;
		_privateAreas = privateAreas;
		_profile = profile;
		_recording = recording;
		_settings = settings;
		_confirm = confirm;
		_clock = clock;

		// A change of profile changes the cadence the platform was asked for, which is fixed when
		// the watch is started — so the watch is restarted rather than reinterpreted.
		_profile.Changed += OnProfileChanged;
	}

	/// <summary>Fired whenever anything below it moved. The UI re-renders off this and nothing else.</summary>
	public event Action? Changed;

	/// <summary>Where the broadcast has got to, in the terms the ride screens report it.</summary>
	public LocationBroadcastStatus Status { get; private set; } = LocationBroadcastStatus.Off;

	/// <summary>
	/// The last thing that went wrong, in the words it was said in, or <c>null</c> when nothing has.
	/// Kept beside <see cref="Status"/> rather than folded into it: "publishing failed" and *why*
	/// are different amounts of information, and only one of them is safe to put in a status enum.
	/// </summary>
	public string? Detail { get; private set; }

	/// <summary>Whether this host can produce fixes at all. False in a browser (§18.6).</summary>
	public bool IsSupported => _provider.IsSupported;

	/// <summary>Whether at least one ride is asking for this device to broadcast.</summary>
	public bool IsRequested => _reasons.Count > 0;

	/// <summary>Which rides are asking. What the settings screen names when a rider asks why the GPS is on.</summary>
	public IReadOnlyCollection<Guid> Rides => _reasons;

	/// <summary>
	/// The last fix the platform produced, published or not — including one refused by the gate or
	/// suppressed by the private area. It is what "the GPS is alive" is drawn from; a screen that
	/// only ever saw published fixes could not tell a warming-up receiver from a dead one.
	/// </summary>
	public LocationFix? LastFix { get; private set; }

	/// <summary>
	/// Where this device believes it is, for drawing on <em>this</em> rider's own map — or
	/// <c>null</c> when there is nothing it may draw.
	/// <para>
	/// <strong>This is the device's own read, and it deliberately does not wait for the server.</strong>
	/// The rider pins on the live map come back from the ride (§5.3), which means they only exist
	/// once a ride is <c>Live</c>, once a fix has cleared the gate, and once the next 5 s fan-out
	/// tick has run. None of that is a reason for somebody to be unable to see where they are
	/// standing, and a phone that has a fix in hand and draws nothing reads as a broken app.
	/// </para>
	/// <para>
	/// <strong>Suppressed answers <c>null</c>, and that is the point.</strong> Inside the rider's
	/// private area (§10.1) nothing is sent — and nothing is drawn either, so the map agrees with
	/// the setting rather than showing a dot whose position the rider has asked the app not to know
	/// about. <see cref="LastFix"/> still holds it; this is the property the map is allowed to read.
	/// </para>
	/// </summary>
	public LocationFix? OwnFix => Status is LocationBroadcastStatus.Suppressed ? null : LastFix;

	/// <summary>When a fix last reached the server, or <c>null</c> if none has this run.</summary>
	public DateTimeOffset? LastPublishedUtc { get; private set; }

	/// <summary>Why the last fix was not published, when it was not.</summary>
	public PositionGateReason LastGateReason { get; private set; } = PositionGateReason.Accepted;

	/// <summary>
	/// Whether the rider has to do something about this — go into the phone's settings, or read a
	/// failure. What the ride screens use to decide between a line in a menu and a strip over the
	/// map: a state nobody can act on does not earn the interruption.
	/// </summary>
	public bool NeedsAttention => Status
		is LocationBroadcastStatus.PermissionNeeded
		or LocationBroadcastStatus.PermissionBlocked
		or LocationBroadcastStatus.Failed;

	/// <summary>
	/// The receiver's state in one sentence a rider can act on.
	/// <para>
	/// Here rather than on each screen, so the ride's menu, the info page's sharing panel and the
	/// settings screen cannot drift into describing the same receiver three different ways — and
	/// so "suppressed" in particular is always stated as the setting doing its job rather than as a
	/// fault (§10.1).
	/// </para>
	/// </summary>
	public string Describe() => Status switch
	{
		LocationBroadcastStatus.Off => IsSupported
			? "Not sharing your location."
			: "This device does not share its location.",
		LocationBroadcastStatus.Starting => "Starting the GPS…",
		LocationBroadcastStatus.WaitingForFix => "Waiting for a GPS fix…",
		LocationBroadcastStatus.Broadcasting => "Sharing your location.",
		LocationBroadcastStatus.Suppressed => "Inside your private area — nothing is being sent.",
		LocationBroadcastStatus.PermissionNeeded => "This app needs permission to use your location.",
		LocationBroadcastStatus.PermissionBlocked =>
			"Location permission is off for this app. Turn it on in the phone's settings.",
		LocationBroadcastStatus.NotSupported => "This device cannot share its location.",
		LocationBroadcastStatus.Failed => Detail is { Length: > 0 } problem
			? $"Your location is not reaching the adventure: {problem}"
			: "Your location is not reaching the adventure.",
		_ => "",
	};

	/// <summary>
	/// Registers one ride's sharing as a reason to broadcast, and starts the GPS if it is not
	/// already running.
	/// <para>
	/// Idempotent — a ride whose page is opened, left and opened again asks every time, and the
	/// second ask must not start a second watch.
	/// </para>
	/// </summary>
	/// <param name="rideId">The ride this rider has consented to share with (§5.6).</param>
	public Task ShareWithAsync(Guid rideId)
	{
		if (_disposed || !_reasons.Add(rideId))
		{
			return Task.CompletedTask;
		}

		return EnsureRunningAsync();
	}

	/// <summary>
	/// Drops one ride's reason. The GPS keeps running while any other ride still wants it, and
	/// stops when the last one goes — a rider sharing with two rides who leaves one is still on
	/// the other, and a receiver that stopped there would take them off a map they are on.
	/// </summary>
	/// <param name="rideId">The ride whose sharing was turned off, or that was left.</param>
	public Task StopSharingAsync(Guid rideId)
	{
		if (!_reasons.Remove(rideId) || _reasons.Count > 0)
		{
			Raise();
			return Task.CompletedTask;
		}

		return StopAsync();
	}

	/// <summary>
	/// Stops broadcasting outright and forgets every reason. What signing out calls: the fixes
	/// belong to an account, and the next one to sign in on this phone is not that account.
	/// </summary>
	public Task StopAllAsync()
	{
		_reasons.Clear();
		return StopAsync();
	}

	private async Task EnsureRunningAsync()
	{
		if (!_provider.IsSupported)
		{
			// The web hosts, and a MAUI host whose platform provider has not been bound. Stated
			// rather than silent: a rider who turned sharing on is owed the reason nothing moved.
			Set(LocationBroadcastStatus.NotSupported, "This device cannot publish its location.");
			return;
		}

		if (_pump is { IsCompleted: false })
		{
			return;
		}

		Set(LocationBroadcastStatus.Starting);

		// Both reads happen HERE, before the pump exists, and that ordering is load-bearing.
		//
		// Each of these states raises Changed when it first resolves, and this object listens to
		// the profile's — a change of cadence restarts the watch. Reading them from inside the pump
		// therefore had the pump's own first act cancel the pump: LoadAsync fired Changed, the
		// handler saw a running pump and restarted it, and the receiver never got as far as being
		// switched on. It failed as a watch that silently never started, which is the hardest
		// possible shape for this bug to have.
		//
		// PrivateAreaState is awaited here for a second reason: it answers "hide" until it has been
		// read (§10.1), so a fix arriving before it resolves would be dropped. Correct, but it
		// would look exactly like a receiver that could not get a lock.
		await _privateAreas.LoadAsync();
		await _profile.LoadAsync();

		// Here for the same reason as the two above: the recorder's switch and interval are read
		// off the device, and reading them from inside the pump would race the first fix.
		await _recording.LoadAsync();

		if (!await DiscloseAsync())
		{
			// The rider read what the app is about to do and said no. Nothing starts, and the
			// status says why — the sharing flag on the server is still theirs to turn off.
			_reasons.Clear();
			Set(LocationBroadcastStatus.PermissionNeeded,
				"Sharing needs your agreement to send your location in the background.");
			return;
		}

		CancellationTokenSource cts = new();
		_running = cts;

		AccuracyProfile profile = _profile.Profile;

		_pump = Task.Run(() => PumpAsync(profile, cts.Token), CancellationToken.None);
	}

	/// <summary>
	/// Google Play's <em>prominent disclosure</em>, shown once per device before the platform's own
	/// permission dialog is ever reached.
	/// <para>
	/// <strong>This is a store requirement with a specific shape, not a nicety.</strong> Play's
	/// background-location policy requires the app's own UI to say that it collects location "to
	/// enable [feature], even when the app is closed or not in use", with an explicit accept and
	/// deny, <em>before</em> the runtime permission request — and it is checked against a video at
	/// review. An app that goes straight to the system dialog is rejected however good its
	/// in-context copy is elsewhere. See Documentation/store-release.md.
	/// </para>
	/// <para>
	/// <strong>Here, rather than on the consent prompt.</strong> Turning sharing on has more than
	/// one route into it — the join-time prompt on the live map and the switch on the ride's info
	/// page — and a disclosure that only one of them shows is a disclosure the reviewer will find
	/// the way around. This is the single choke point every route passes through.
	/// </para>
	/// <para>
	/// Asked once per device and remembered. Repeating it every ride would train riders to dismiss
	/// it, which is the opposite of what a disclosure is for.
	/// </para>
	/// </summary>
	/// <returns>True when the rider accepted, or had already accepted on this device.</returns>
	private async Task<bool> DiscloseAsync()
	{
		if (await _settings.GetAsync(DisclosureStorageKey) == "1")
		{
			return true;
		}

		bool accepted = await _confirm.AskAsync(
			"Share your location while you travel?",
			// The first sentence is Play's required form, near enough verbatim: what is collected,
			// what it enables, and "even when the app is closed or not in use". Revise the rest
			// freely; leave that clause alone.
			"Dumb Luck Routes collects location data to show you to the other people on the group "
			+ "adventures you turn sharing on for, even when the app is closed or not in use — so the "
			+ "group can still see you with your phone in a mount or a pocket and the screen off.\n\n"
			// "Off until you turn it on" was true until joining on a phone started turning it on
			// (JoinRide.ShareByDefaultAsync). A disclosure that describes a default the app no
			// longer has is the one sentence on this screen a reviewer can catch it out on, so it
			// now says what actually happens — and says where the rider sees it and undoes it.
			+ "Sharing is per adventure. It starts on for an adventure you join on this phone, the map "
			+ "says so in red whenever it is off, and you can turn it off at any time. Nothing is ever "
			+ "sent from inside the private area you can set around home.",
			confirmText: "I agree");

		if (accepted)
		{
			await _settings.SetAsync(DisclosureStorageKey, "1");
		}

		return accepted;
	}

	private async Task StopAsync()
	{
		CancellationTokenSource? running = _running;
		Task? pump = _pump;

		_running = null;
		_pump = null;

		if (running is null)
		{
			Set(LocationBroadcastStatus.Off);
			return;
		}

		await running.CancelAsync();

		if (pump is not null)
		{
			// Awaited rather than abandoned: the platform watch holds a foreground service on
			// Android and a background-location assertion on iOS, and both must be released
			// before this returns or the notification outlives the reason for it.
			try { await pump; }
			catch (OperationCanceledException) { /* the way a cancelled pump ends */ }
		}

		running.Dispose();

		// After the pump has been awaited, so nothing is still appending behind this write. The
		// recorder writes through every few points anyway; this is what makes the last few of a
		// ride survive a phone the OS reclaims while it sits in a pocket.
		//
		// No segment break is forced here: a stop and start inside TrackRecording.SegmentGap is
		// one ride with a pause in it, and anything longer breaks on the time gap by itself. A
		// break forced on every stop would split a track each time the rider changed profile.
		await _recording.FlushAsync();

		LastPublishedUtc = null;

		// Cleared with the receiver, not kept as a last-known. A stopped GPS has no opinion about
		// where this phone is, and a dot left on the map from the last fix before the rider turned
		// sharing off is the app claiming otherwise — on a screen they are still looking at.
		LastFix = null;

		Set(LocationBroadcastStatus.Off);
	}

	/// <summary>
	/// Watches the platform and publishes what survives the gates. Runs until cancelled, and every
	/// failure inside it is stated rather than thrown: this task has no caller to catch it, and a
	/// broadcast that dies silently is the worst of the failure modes — the rider believes they are
	/// on the map.
	/// </summary>
	private async Task PumpAsync(AccuracyProfile profile, CancellationToken cancellationToken)
	{
		try
		{
			LocationPermissionState permission = await _provider.EnsurePermissionsAsync(cancellationToken);

			if (permission != LocationPermissionState.Granted)
			{
				Set(
					permission switch
					{
						LocationPermissionState.DeniedPermanently => LocationBroadcastStatus.PermissionBlocked,
						LocationPermissionState.NotSupported => LocationBroadcastStatus.NotSupported,
						_ => LocationBroadcastStatus.PermissionNeeded,
					},
					permission == LocationPermissionState.DeniedPermanently
						? "Location permission is off for this app. Turn it on in the phone's settings."
						: "This app needs permission to use your location.");

				return;
			}

			// The profile is passed in rather than read here: it is fixed for the life of one
			// watch — the platform's cadence is set when the request is made — and a change of it
			// is a restart, not a value this loop re-reads.
			PositionGate gate = new(profile);

			Set(LocationBroadcastStatus.WaitingForFix);

			await foreach (LocationFix fix in _provider.WatchAsync(profile, cancellationToken))
			{
				LastFix = fix;

				// The recorder sees the fix first, and sees all of them (§15.1).
				//
				// Not an oversight of the §10.1 ordering below — a different question. Publishing
				// a fix hands somebody's position to a server and to every other rider on the ride,
				// and that is the thing the private area exists to stop. Recording keeps it in the
				// same store on the same phone that the private area itself lives in, and it does
				// not leave until the rider presses save on the Location screen — where the choice
				// about their private area is offered again, and defaults to cutting it out.
				//
				// Upstream of the §4.2 gate for a plainer reason: that gate is a battery decision
				// about uplink, and a rider on Eco who asked for a 5 m track should get one.
				await _recording.OfferAsync(fix, cancellationToken);

				await HandleAsync(gate, fix, cancellationToken);

				// Every fix moves this rider's own dot (see OwnFix), whether or not it was
				// published and whether or not the status moved with it — so the UI is told about
				// all of them, not only the ones that changed a status. Without this a rider on a
				// steadily broadcasting phone watched a dot that never moved: Set() is a no-op when
				// nothing changed, which is exactly the steady state of a working receiver.
				//
				// Set() may already have raised on a transition. Those are rare enough that one
				// extra render on the change is cheaper than threading a "did it raise" flag back
				// out of every branch below.
				Raise();
			}
		}
		catch (OperationCanceledException)
		{
			// Stopping. Not a failure, and it must not leave an error on screen.
		}
		catch (Exception exception)
		{
			Set(LocationBroadcastStatus.Failed, exception.Message);
		}
	}

	/// <summary>
	/// Puts one fix through the two gates and publishes what survives them. Split out of the pump's
	/// loop so the loop's own <c>Raise</c> runs on every fix, whichever branch this took.
	/// </summary>
	/// <param name="gate">The §4.2 filter for the profile this watch is running at.</param>
	/// <param name="fix">The fix the platform just produced.</param>
	/// <param name="cancellationToken">Stops the publish.</param>
	private async Task HandleAsync(PositionGate gate, LocationFix fix, CancellationToken cancellationToken)
	{
		// §10.1, and first. Not filtered, not queued, not retried — dropped where it was read,
		// because the one copy of a rider's home that cannot leak is the one that never left the
		// phone.
		if (_privateAreas.HidesLocation(fix))
		{
			Set(LocationBroadcastStatus.Suppressed);
			return;
		}

		PositionGateDecision decision = gate.Evaluate(fix);
		LastGateReason = decision.Reason;

		if (!decision.Publish)
		{
			// A refused fix is still proof the receiver is alive, so the status follows the reason
			// rather than staying on whatever it was: a rider stationary at a junction reads "on
			// the map" and not "waiting".
			Set(decision.Reason == PositionGateReason.TooInaccurate
				? LocationBroadcastStatus.WaitingForFix
				: Status == LocationBroadcastStatus.Broadcasting
					? LocationBroadcastStatus.Broadcasting
					: LocationBroadcastStatus.WaitingForFix);
			return;
		}

		await PublishAsync(fix, cancellationToken);
	}

	/// <summary>
	/// Sends one fix, hub first and REST second.
	/// <para>
	/// The hub is the channel §5.7 is designed around — one open connection, no request overhead
	/// per fix. The REST endpoint is the same publish behind a request, and it is what carries a
	/// fix while the hub is reconnecting, which on a motorcycle is a routine event rather than an
	/// exceptional one. Neither is retried: the next fix is a second away and is worth more than
	/// the one that failed.
	/// </para>
	/// </summary>
	private async Task PublishAsync(LocationFix fix, CancellationToken cancellationToken)
	{
		PositionUpdate update = ToUpdate(fix);

		try
		{
			if (!_hub.IsConnected)
			{
				await _hub.ConnectAsync(cancellationToken);
			}

			await _hub.PublishPositionAsync(update, cancellationToken);
			Published();
			return;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			// Falls through to the REST path. Deliberately not stated yet — one failed hub send
			// with a successful fallback is not something to put in front of a rider.
		}

		try
		{
			await _api.PublishPositionAsync(update, cancellationToken);
			Published();
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			// Both paths refused. The receiver is fine and the ride is fine; what is broken is the
			// link, and saying so is what stops a rider believing they are on the map.
			Set(LocationBroadcastStatus.Failed, exception.Message);
		}
	}

	private void Published()
	{
		LastPublishedUtc = _clock.GetUtcNow();
		Detail = null;
		Set(LocationBroadcastStatus.Broadcasting);
	}

	/// <summary>
	/// A platform fix as the wire carries it (§5.3, §5.7): degrees scaled to integers, and the
	/// three optional measurements narrowed to <c>short</c>.
	/// <para>
	/// Clamped rather than cast. An unclamped cast wraps — a 40 000 m accuracy estimate from a
	/// cell-tower fix would arrive as a negative number, and negative accuracy is a value nothing
	/// downstream has a meaning for.
	/// </para>
	/// </summary>
	/// <param name="fix">The platform's fix.</param>
	public static PositionUpdate ToUpdate(LocationFix fix) => new(
		PositionScale.FromDegrees(fix.Latitude),
		PositionScale.FromDegrees(fix.Longitude),
		fix.RecordedUtc,
		ToShort(fix.SpeedMps),
		ToShort(fix.HeadingDeg),
		ToShort(fix.AccuracyM));

	private static short? ToShort(double? value) =>
		value is { } number && double.IsFinite(number)
			// AwayFromZero rather than .NET's banker's rounding: these are measurements a rider may
			// see quoted back at them, and 12.5 m/s arriving as 12 while 13.5 arrives as 14 is the
			// kind of inconsistency that costs an afternoon to explain.
			? (short)Math.Clamp(Math.Round(number, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue)
			: null;

	private void OnProfileChanged()
	{
		if (_pump is not { IsCompleted: false })
		{
			return;
		}

		// Restarted rather than adjusted: the cadence and the accuracy the platform was asked for
		// were fixed when the watch was created.
		//
		// Off this thread, and fire-and-forget. The restart awaits the running pump's teardown, so
		// running it inline on whichever thread raised the event risks waiting on the very task
		// that is raising it — and the screen that moved the control must not block on a GPS
		// teardown either.
		_ = Task.Run(RestartAsync);
	}

	private async Task RestartAsync()
	{
		await StopAsync();

		if (_reasons.Count > 0)
		{
			await EnsureRunningAsync();
		}
	}

	private void Set(LocationBroadcastStatus status, string? detail = null)
	{
		if (Status == status && Detail == detail)
		{
			return;
		}

		// Every GPS transition in the app passes through here — starting, stopping, a permission
		// refused, a receiver the platform took away. Logged at the choke point rather than at the
		// callers so a path added later cannot forget, and with the reason count because "stopped"
		// means something different when it is the last ride letting go than when it is a failure.
		DiagnosticLog.Write(
			$"GPS: {Status} -> {status}{(detail is null ? "" : $" ({detail})")}, " +
			$"{_reasons.Count} ride(s) sharing.");

		Status = status;
		Detail = detail;
		Raise();
	}

	private void Raise() => Changed?.Invoke();

	/// <summary>
	/// Stops the receiver and waits for the platform to release it. The watch is a foreground
	/// service on one platform and a background-location assertion on the other, and both have to
	/// be given back deliberately — this is the path that guarantees the notification is down
	/// before the scope is gone.
	/// </summary>
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_profile.Changed -= OnProfileChanged;
		_reasons.Clear();

		await StopAsync();

		Changed = null;
	}

	/// <summary>
	/// The synchronous escape hatch, for containers that only dispose one way — bUnit's test scope
	/// is one, and it refuses to construct a service that is <see cref="IAsyncDisposable"/> alone.
	/// <para>
	/// It cancels the watch and does not wait for it. That is weaker than
	/// <see cref="DisposeAsync"/> and deliberately so: blocking a container's synchronous teardown
	/// on a GPS release is how a phone deadlocks on shutdown. The receiver still stops — each
	/// platform provider releases in the <c>finally</c> of its own watch loop, which cancellation
	/// is what triggers. Prefer <see cref="DisposeAsync"/>; every host in this app calls it.
	/// </para>
	/// </summary>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_profile.Changed -= OnProfileChanged;
		_reasons.Clear();

		_running?.Cancel();
		_running = null;
		_pump = null;

		Status = LocationBroadcastStatus.Off;
		Changed = null;
	}
}

/// <summary>Where the device's broadcast has got to (§4.3, §5.7).</summary>
public enum LocationBroadcastStatus
{
	/// <summary>No ride is asking, so the receiver is off. The battery-correct resting state.</summary>
	Off = 0,

	/// <summary>Asked for, and the permission and first-fix work is under way.</summary>
	Starting = 1,

	/// <summary>Running, with nothing publishable yet — a cold receiver, or a sky full of buildings.</summary>
	WaitingForFix = 2,

	/// <summary>Fixes are reaching the server.</summary>
	Broadcasting = 3,

	/// <summary>Inside the rider's own private area, so nothing is being sent (§10.1). Not a fault.</summary>
	Suppressed = 4,

	/// <summary>The rider has not granted location permission yet, and can still be asked.</summary>
	PermissionNeeded = 5,

	/// <summary>Permission is denied in a way only the phone's settings can undo.</summary>
	PermissionBlocked = 6,

	/// <summary>This host has no GPS the app can use — both browsers (§18.6).</summary>
	NotSupported = 7,

	/// <summary>The receiver is fine; the fixes are not getting through.</summary>
	Failed = 8,
}
