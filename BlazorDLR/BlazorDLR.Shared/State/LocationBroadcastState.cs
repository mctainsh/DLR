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
/// <see cref="PrivateAreaState.HidesLocation(LocationFix)"/> <em>before</em> anything on the
/// publishing path looks at it (§10.1) — before the accuracy filter, before the cadence filter,
/// and long before the network. A fix from inside the rider's private area is not filtered, not
/// queued and not retried: it is dropped where it was read. And because that state answers "hide"
/// until it has an answer — from the account, or from this device's cache of it — a race at
/// startup fails closed.
/// <para>
/// <strong>Publishing is the only thing that gate governs.</strong> It does not govern
/// <see cref="OwnFix"/>, which is what this rider's own screen draws, and it never governed the
/// recorder (§15.1). Suppressing a rider's position on their own phone hides their house from the
/// one person who is standing in it, and costs them the map at the moment they are most likely to
/// be reading it — see the remarks on <see cref="OwnFix"/>.
/// </para>
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

	/// <summary>
	/// How often "this rider is private" is restated while it stays true (§10.1).
	/// <para>
	/// Long, because it is insurance rather than a heartbeat: the one thing it covers is the server
	/// having forgotten — a restart, or a hub that reconnected across the moment the rider reached
	/// their street. A minute of being a stale pin is the worst case it leaves, against one small
	/// message a minute while somebody is sitting at home.
	/// </para>
	/// </summary>
	public static readonly TimeSpan PrivacyRepeat = TimeSpan.FromMinutes(1);

	/// <summary>
	/// How long one send — hub or REST — may take before it is abandoned (§4.2, §5.7).
	/// <para>
	/// Neither transport bounds itself anywhere near usefully. A hub invoke waits for the server's
	/// completion message and a cell radio that has gone quiet without closing the socket will not
	/// produce one; the shared <c>HttpClient</c> carries .NET's 100-second default. Riders reported
	/// the result as a pin that stopped for minutes, jumped twenty kilometres and then behaved
	/// normally, and that is exactly the shape of a stalled send.
	/// </para>
	/// <para>
	/// Eight seconds, which is comfortably longer than a slow round trip on a bad link and
	/// comfortably shorter than the interval at which giving up costs anything: the next fix is
	/// seconds away and is worth strictly more than the one being waited on.
	/// </para>
	/// </summary>
	public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(8);

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

	/// <summary>
	/// Guards <see cref="Status"/>, <see cref="Detail"/> and <see cref="LastPublishedUtc"/>, which
	/// the fix pump and the sender both write since the publish moved off the pump.
	/// </summary>
	private readonly object _statusGate = new();

	/// <summary>
	/// True while the rider's own fixes say they are inside their private area, so a send that was
	/// already in flight cannot report them back onto the map (§10.1). See <see cref="Set"/>.
	/// </summary>
	private bool _suppressed;

	/// <summary>
	/// Set once the status must not move again — a failure the app cannot recover from without a
	/// restart, which a later routine update would otherwise paper over. Cleared on the next start.
	/// </summary>
	private bool _statusLatched;

	private CancellationTokenSource? _running;
	private Task? _pump;
	private bool _disposed;

	/// <summary>
	/// What the server was last told about this rider's private area, or <c>null</c> when it has not
	/// been told anything this run (§10.1).
	/// <para>
	/// Three states again, and for the same reason <see cref="PrivateArea.TryDecodeCached"/> needs
	/// three: "told them private", "told them not private" and "have not said" are different, and
	/// only the third one may be re-sent unconditionally. Without it every fix inside the circle
	/// would put a message on the wire — one a second, for the length of a driveway — and every fix
	/// outside it would put another.
	/// </para>
	/// </summary>
	private bool? _announcedPrivate;

	/// <summary>When that was said, for the repeat below.</summary>
	private DateTimeOffset _announcedUtc;

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
	/// Where this device believes it is — the last fix the platform produced, published or not,
	/// including one refused by the §4.2 gate or suppressed by the private area — or <c>null</c>
	/// when the receiver is off or has not produced one yet.
	/// <para>
	/// <strong>This is the device's own read, and it deliberately does not wait for the server.</strong>
	/// The rider pins on the live map come back from the ride (§5.3), which means they only exist
	/// once a ride is <c>Live</c>, once a fix has cleared the gate, and once the next 5 s fan-out
	/// tick has run. None of that is a reason for somebody to be unable to see where they are
	/// standing, and a phone that has a fix in hand and draws nothing reads as a broken app. It is
	/// also what "the GPS is alive" is drawn from: a screen that only ever saw published fixes
	/// could not tell a warming-up receiver from a dead one.
	/// </para>
	/// <para>
	/// <strong>A suppressed fix is drawn here too, and that is a correction.</strong> This property
	/// used to answer <c>null</c> inside the rider's private area, on the argument that the map
	/// should agree with the setting. The argument does not hold: the setting is about what leaves
	/// the phone, and on the rider's own screen there is nobody to hide their house from — they are
	/// standing in it. What it cost was the whole map going dead at the moment a rider is most
	/// likely to be reading it: no mark of their own, nothing for the camera to follow and no
	/// heading to turn the map by, from the driveway to the edge of the circle. §10.1 is a rule
	/// about publishing and it is enforced where publishing is decided, in <c>Handle</c>.
	/// </para>
	/// <para>
	/// There is deliberately no second "but not this one" property beside it. The pair that used to
	/// exist differed only inside the private area, which is exactly the case this got wrong, and
	/// leaving both would invite somebody to reinstate the distinction as a fix.
	/// </para>
	/// </summary>
	public LocationFix? OwnFix { get; private set; }

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
		// PrivateAreaState is awaited here for a second reason: it answers "hide" until it has an
		// answer (§10.1), so a fix arriving before it resolves would be dropped. Correct, but it
		// would look exactly like a receiver that could not get a lock. Since the area moved onto
		// the account this read can also involve the network, which makes the ordering matter
		// more rather than less — it is a request, not a preference lookup, and the pump must not
		// be the thing waiting on it.
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

		// Forgotten rather than reversed. The receiver stopping is not the rider leaving their private
		// area — they are usually stopping *because* they got home — so announcing "no longer private"
		// here would put them back on a map from their own driveway. What this does mean is that the
		// next run has said nothing yet, so the first fix inside the circle states it again.
		_announcedPrivate = null;

		// Both belong to the run that is ending. A latch left standing would make every status this
		// object ever showed again a lie, and a suppression left standing would hold the next run's
		// first fix off the map until the rider happened to leave their circle.
		lock (_statusGate)
		{
			_statusLatched = false;
			_suppressed = false;
		}

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
		OwnFix = null;

		Set(LocationBroadcastStatus.Off);
	}

	/// <summary>
	/// Watches the platform and hands what survives the gates to the sender. Runs until cancelled,
	/// and every failure inside it is stated rather than thrown: this task has no caller to catch
	/// it, and a broadcast that dies silently is the worst of the failure modes — the rider believes
	/// they are on the map.
	/// </summary>
	/// <remarks>
	/// <strong>Two tasks, not one.</strong> This one only ever touches the platform and the device;
	/// <see cref="SendLoopAsync"/> is the only thing here that talks to a server, and they meet at a
	/// <see cref="PositionOutbox"/>. That split is what stops a slow network from stopping the GPS.
	/// </remarks>
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

			// The mailbox and the one task that empties it. Nothing below this line waits on a
			// socket: the loop reads a fix, records it, gates it, posts it and goes back for the
			// next one, and the sender does the waiting on its own thread.
			//
			// This used to be one loop that did both, and the cost was not theoretical. A send
			// that hung — a black-holed cell socket at speed is the ordinary case, not the
			// exceptional one — stopped the rider's own mark, the recorder and every fix behind
			// it, for as long as the slowest timeout on the path. See PositionOutbox.
			using PositionOutbox outbox = new();

			Task sender = Task.Run(
				() => SendLoopAsync(gate, outbox, cancellationToken),
				CancellationToken.None);

			try
			{
				// Inside the try, because Set raises Changed and a subscriber may throw. Outside it,
				// that exception skipped the finally below: the outbox was never completed, the
				// sender was orphaned, and `using` then disposed the semaphore underneath it.
				Set(LocationBroadcastStatus.WaitingForFix);

				await PumpFixesAsync(gate, outbox, profile, cancellationToken);
			}
			finally
			{
				// The receiver has stopped, so nothing more will be posted. Closing the outbox is
				// what ends the sender's loop, and awaiting it is what extends StopAsync's promise
				// — everything released before it returns — to cover the socket as well as the
				// platform watch.
				outbox.Complete();

				try { await sender; }
				catch (OperationCanceledException) { /* the way a cancelled sender ends */ }
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
	/// The receiver's loop: every fix the platform produces, recorded, gated and posted.
	/// <para>
	/// Split from <see cref="PumpAsync"/> so the outbox and its sender can be torn down in one
	/// <c>finally</c> around the whole of it, whichever way the enumeration ends.
	/// </para>
	/// </summary>
	/// <param name="gate">The §4.2 filter for the profile this watch is running at.</param>
	/// <param name="outbox">Where an accepted fix is handed over.</param>
	/// <param name="profile">The profile the platform was asked for.</param>
	/// <param name="cancellationToken">Stops the receiver.</param>
	private async Task PumpFixesAsync(
		PositionGate gate,
		PositionOutbox outbox,
		AccuracyProfile profile,
		CancellationToken cancellationToken)
	{
		await foreach (LocationFix fix in _provider.WatchAsync(profile, cancellationToken))
		{
			OwnFix = fix;

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

			Handle(gate, outbox, fix);

			// Every fix moves this rider's own dot (see OwnFix), whether or not it was
			// published, whether or not the private area suppressed it, and whether or not the
			// status moved with it — so the UI is told about all of them, not only the ones
			// that changed a status. Without this a rider on a
			// steadily broadcasting phone watched a dot that never moved: Set() is a no-op when
			// nothing changed, which is exactly the steady state of a working receiver.
			//
			// Set() may already have raised on a transition. Those are rare enough that one
			// extra render on the change is cheaper than threading a "did it raise" flag back
			// out of every branch below.
			Raise();
		}
	}

	/// <summary>
	/// Puts one fix through the two gates and hands what survives them to the outbox. Split out of
	/// the pump's loop so the loop's own <c>Raise</c> runs on every fix, whichever branch this took.
	/// <para>
	/// Synchronous, and that is the point of it. Everything here is a decision — is this fix inside
	/// the circle, is it worth the uplink — and none of it touches the network. What used to be
	/// awaited from this method is now a <see cref="PositionOutbox.Post"/> that returns at once.
	/// </para>
	/// </summary>
	/// <param name="gate">The §4.2 filter for the profile this watch is running at.</param>
	/// <param name="outbox">Where a fix worth sending is handed over.</param>
	/// <param name="fix">The fix the platform just produced.</param>
	private void Handle(PositionGate gate, PositionOutbox outbox, LocationFix fix)
	{
		// §10.1, and first on this path. Not filtered, not queued, not retried — the fix is
		// dropped here and never reaches the hub.
		//
		// "This path" is load-bearing: the caller has already recorded the fix and already assigned
		// OwnFix, so the rider's own mark, the follow camera and the heading-up rotation all keep
		// working inside the circle. Returning early from *here* is what makes the area a rule
		// about what other people can see rather than a blindfold on the person wearing it.
		if (_privateAreas.HidesLocation(fix))
		{
			_suppressed = true;
			Set(LocationBroadcastStatus.Suppressed);

			// IsInsideArea, not the gate that was just consulted. The gate suppresses while this
			// device has no answer about the circle yet, and announcing on that would tell a rider's
			// friends they are at home every time the app starts up out of signal. Only a circle we
			// actually hold, that actually contains this fix, is a fact worth stating.
			if (_privateAreas.IsInsideArea(fix))
			{
				AnnouncePrivacy(outbox, isPrivate: true);
			}

			return;
		}

		// Out of the circle: the fix that proves it is what lifts the suppression latch, so a send
		// still in flight from before the crossing can report success again.
		_suppressed = false;

		// Out of the circle, said explicitly rather than left to the publish below to imply. The
		// publish is not guaranteed to happen — the §4.2 gate refuses fixes that are too inaccurate
		// or too close together, and a rider rolling out of their street at walking pace can be
		// refused for a while — so relying on it would leave somebody labelled "private" on a map they
		// are visibly moving across. The server clears the flag on a published fix as well, which is
		// the belt to this pair of braces.
		AnnouncePrivacy(outbox, isPrivate: false);

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

		// Handed over, not sent. Whatever is already waiting for the sender is replaced: a position
		// this one supersedes is worth nothing to a ride, and sending it first is what used to make
		// a stall take minutes to unwind rather than seconds. See PositionOutbox.
		outbox.Post(fix);
	}

	/// <summary>
	/// Decides whether the ride needs telling that the rider crossed the edge of their own circle,
	/// and posts the crossing when it does (§10.1).
	/// <para>
	/// <strong>Stated rather than implied, and repeated while it stays true.</strong> A fix inside
	/// the circle is dropped, so "no fixes are arriving" is what the server would otherwise have to
	/// infer — and it cannot tell that from a tunnel, a flat battery, or an app that was killed. One
	/// bit, sent deliberately, is the difference between a member list that says "at home, hidden"
	/// and one that says nothing while a pin sits outside somebody's house.
	/// </para>
	/// <para>
	/// The <em>private</em> direction is repeated every <see cref="PrivacyRepeat"/> because it is sent
	/// once, at the kerb, and if it goes missing the rider is a pin parked outside their house for
	/// the rest of the ride. A server restart or a hub reconnect in that moment is exactly the case
	/// the repeat covers, and the cost of it is one small message a minute while somebody is at home.
	/// The <em>public</em> direction is not repeated: it heals itself, because the next published fix
	/// clears the flag server-side.
	/// </para>
	/// <para>
	/// The decision is recorded before the crossing is posted, not after. A send that fails must not
	/// leave this loop trying again on the next fix a second later — the repeat window is the retry,
	/// and it is deliberately slower than the fixes are.
	/// </para>
	/// </summary>
	/// <param name="outbox">Where the crossing is handed over.</param>
	/// <param name="isPrivate">Which way the rider crossed the edge.</param>
	private void AnnouncePrivacy(PositionOutbox outbox, bool isPrivate)
	{
		DateTimeOffset now = _clock.GetUtcNow();

		if (_announcedPrivate == isPrivate && (!isPrivate || now - _announcedUtc < PrivacyRepeat))
		{
			return;
		}

		_announcedPrivate = isPrivate;
		_announcedUtc = now;

		outbox.PostPrivacy(isPrivate);
	}

	/// <summary>
	/// The one task that talks to the network, draining <paramref name="outbox"/> until the receiver
	/// stops (§4.2, §5.7).
	/// <para>
	/// Everything slow lives here, alone, so that when it is slow nothing else is. It ends when the
	/// outbox is completed and drained — <see cref="PositionOutbox.TakeAsync"/> answers an empty
	/// batch — or when the watch is cancelled.
	/// </para>
	/// <para>
	/// The crossing goes before the position when both are waiting. Coming out of the circle that is
	/// the natural pair, and going in there is no position to send: <see cref="PositionOutbox"/>
	/// drops it.
	/// </para>
	/// </summary>
	/// <param name="gate">The §4.2 filter, told which fix actually landed.</param>
	/// <param name="outbox">The mailbox the pump posts into.</param>
	/// <param name="cancellationToken">Stops the sender.</param>
	private async Task SendLoopAsync(
		PositionGate gate,
		PositionOutbox outbox,
		CancellationToken cancellationToken)
	{
		try
		{
			while (await outbox.TakeAsync(cancellationToken) is { IsEmpty: false } batch)
			{
				try
				{
					if (batch.Privacy is { } isPrivate)
					{
						await SendPrivacyAsync(isPrivate, cancellationToken);
					}

					if (batch.Fix is { } fix)
					{
						await SendPositionAsync(gate, outbox, fix, cancellationToken);
					}
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception exception)
				{
					// One turn came apart in a way the send paths did not expect. It must not end
					// the loop: the pump goes on posting into an outbox nobody drains, and because
					// the rider's own mark keeps moving there is no symptom on this device at all —
					// the rider is simply off the ride's map until they restart the app. Report the
					// turn and go back for the next batch, which is paced by the pump anyway.
					Set(LocationBroadcastStatus.Failed, exception.Message);
				}
			}
		}
		catch (OperationCanceledException)
		{
			// Stopping. Not a failure, and it must not leave an error on screen.
		}
		catch (Exception exception)
		{
			// The outbox itself came apart, so there is no loop left to continue. Latched, because
			// the pump is still running and its refused-fix branch would otherwise overwrite this
			// with "waiting for a GPS fix" — which is the one explanation guaranteed to be wrong.
			Set(LocationBroadcastStatus.Failed, exception.Message, terminal: true);
		}
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
	/// <param name="gate">Told that this fix landed, so the cadence measures from what arrived.</param>
	/// <param name="fix">The fix to send.</param>
	/// <param name="cancellationToken">Stops the sender.</param>
	private async Task SendPositionAsync(
		PositionGate gate,
		PositionOutbox outbox,
		LocationFix fix,
		CancellationToken cancellationToken)
	{
		PositionUpdate update = ToUpdate(fix);

		SendResult result = await TrySendAsync(
			token => _hub.PublishPositionAsync(update, token),
			token => _api.PublishPositionAsync(update, token),
			// Between the two transports, the newest fix wins — the same rule the outbox itself
			// runs on. Without this, one turn could spend a hub deadline and then a REST deadline
			// publishing a position already superseded by one sitting in the slot.
			() => outbox.HasFixWaiting,
			cancellationToken);

		if (result.Outcome is SendOutcome.Superseded)
		{
			// Abandoned in favour of the fix already waiting, which the next turn sends. Not
			// confirmed — nothing reached the ride — so the cadence still measures from the last
			// fix that did, and not reported either: this is the outbox working, not a failure.
			return;
		}

		if (result.Failure is { } failure)
		{
			// Both paths refused. The receiver is fine and the ride is fine; what is broken is the
			// link, and saying so is what stops a rider believing they are on the map.
			Set(LocationBroadcastStatus.Failed, failure);
			return;
		}

		// Only now, and this is the point of Confirm: the profile's interval measures from the last
		// fix that *reached* the ride, so a run of failed sends is retried at the receiver's cadence
		// rather than waiting out a whole interval for each one.
		gate.Confirm(fix);

		Published();
	}

	/// <summary>Sends one private-area crossing, hub first and REST second (§10.1).</summary>
	/// <param name="isPrivate">Which way the rider crossed the edge.</param>
	/// <param name="cancellationToken">Stops the sender.</param>
	private async Task SendPrivacyAsync(bool isPrivate, CancellationToken cancellationToken)
	{
		PositionPrivacyUpdate update = new(isPrivate);

		// The failure is swallowed rather than shown. The rider's own screen is unaffected either
		// way, the position that would have gone with this was suppressed on the device regardless,
		// and the repeat in AnnouncePrivacy will say it again shortly. Turning this into a red
		// status would report a privacy failure for something that leaked nothing.
		await TrySendAsync(
			token => _hub.PublishPrivacyAsync(update, token),
			token => _api.SetPositionPrivacyAsync(update, token),
			// Nothing supersedes a crossing: it is sent once, at the edge of the circle, and
			// abandoning it would leave a rider hidden — or exposed — for the rest of the ride.
			superseded: null,
			cancellationToken);
	}

	/// <summary>
	/// Runs a send down the hub and then, if that refuses, down REST — each under its own
	/// <see cref="SendTimeout"/>.
	/// </summary>
	/// <param name="viaHub">The send over the realtime connection.</param>
	/// <param name="viaRest">The same send as a request.</param>
	/// <param name="superseded">
	/// Asked before the fallback: when it answers true, the payload is stale and the REST leg is
	/// abandoned rather than spent on it. <c>null</c> for a send nothing can supersede.
	/// </param>
	/// <param name="cancellationToken">Stops the sender. Cancellation from here is not a failure.</param>
	/// <returns>Whether one of the two got through, was abandoned, or why the second one did not.</returns>
	private async Task<SendResult> TrySendAsync(
		Func<CancellationToken, Task> viaHub,
		Func<CancellationToken, Task> viaRest,
		Func<bool>? superseded,
		CancellationToken cancellationToken)
	{
		string? hubFailure = await TryLegAsync(
			async token =>
			{
				if (!_hub.IsConnected)
				{
					await _hub.ConnectAsync(token);
				}

				await viaHub(token);
			},
			cancellationToken);

		if (hubFailure is null)
		{
			return new SendResult(SendOutcome.Sent, null);
		}

		if (superseded?.Invoke() == true)
		{
			return new SendResult(SendOutcome.Superseded, null);
		}

		// Falls through to the REST path. The hub's failure is deliberately not carried out of
		// here — one failed hub send with a successful fallback is not something to put in front
		// of a rider.
		string? restFailure = await TryLegAsync(viaRest, cancellationToken);

		return restFailure is null
			? new SendResult(SendOutcome.Sent, null)
			: new SendResult(SendOutcome.Failed, restFailure);
	}

	/// <summary>
	/// Runs one transport under <see cref="SendTimeout"/> and reports whether it got through.
	/// <para>
	/// The two legs share this so the rule that matters — the watch stopping is not a send failing,
	/// and must propagate rather than be reported — has one copy rather than two that have to stay
	/// identical.
	/// </para>
	/// </summary>
	/// <param name="send">The transport.</param>
	/// <param name="cancellationToken">Stops the sender.</param>
	/// <returns><c>null</c> when it got through, or why it did not.</returns>
	private async Task<string?> TryLegAsync(
		Func<CancellationToken, Task> send,
		CancellationToken cancellationToken)
	{
		try
		{
			await WithTimeoutAsync(send, cancellationToken);
			return null;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			return exception.Message;
		}
	}

	/// <summary>
	/// Runs one send with a deadline on it.
	/// <para>
	/// Neither transport bounds itself usefully: a hub invoke waits for the server's completion
	/// message, and the shared <c>HttpClient</c> carries .NET's 100-second default. On a link that
	/// has gone quiet without closing — a cell radio at speed, which is most of a ride — that is
	/// minutes of a rider being off the map, and before the outbox it was minutes of the fix pump
	/// being stopped dead with it. A position nobody can deliver inside <see cref="SendTimeout"/>
	/// has already been replaced by a better one.
	/// </para>
	/// <para>
	/// The deadline comes from <see cref="TimeProvider"/> rather than <c>CancelAfter</c>'s ambient
	/// timer, so a test can drive a send that never answers without waiting out real seconds.
	/// </para>
	/// </summary>
	/// <param name="send">The send, which must honour the token it is handed.</param>
	/// <param name="cancellationToken">Stops the sender.</param>
	private async Task WithTimeoutAsync(
		Func<CancellationToken, Task> send,
		CancellationToken cancellationToken)
	{
		using CancellationTokenSource deadline = new(SendTimeout, _clock);
		using CancellationTokenSource linked =
			CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

		try
		{
			await send(linked.Token);
		}
		catch (OperationCanceledException)
			when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
		{
			// Translated, so the caller's "was this us stopping?" test stays a test about the
			// watch's own token, and so Detail says something a rider can act on.
			throw new TimeoutException(
				$"no answer from the server within {SendTimeout.TotalSeconds:0} seconds");
		}
	}

	private void Published()
	{
		lock (_statusGate)
		{
			LastPublishedUtc = _clock.GetUtcNow();
		}

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

	/// <summary>
	/// Moves the status, from either thread.
	/// <para>
	/// Locked because the pump and the sender are genuinely two threads since the publish moved off
	/// the pump, and the read-compare-write below is not safe between them. The lock covers the
	/// decision only — <see cref="Raise"/> runs outside it, because subscriber code re-entering
	/// this would deadlock and a UI handler is exactly the kind of code that would.
	/// </para>
	/// </summary>
	/// <param name="status">Where to move to.</param>
	/// <param name="detail">What to say alongside it, if anything.</param>
	/// <param name="terminal">
	/// Latches the status so nothing later can move it: for a failure the app cannot recover from
	/// without a restart, where a subsequent routine update would overwrite the only true
	/// explanation with a misleading one.
	/// </param>
	private void Set(LocationBroadcastStatus status, string? detail = null, bool terminal = false)
	{
		lock (_statusGate)
		{
			if (_statusLatched || (Status == status && Detail == detail))
			{
				return;
			}

			// Suppression is a fact about where the rider is, not a status one send can move. A
			// position taken before the rider crossed into their circle can land after they have —
			// and left to win, it would say "Sharing your location." while §10.1 suppresses, then
			// stand for the rest of the ride, because a rider indoors at home stops producing the
			// fixes that would correct it. Only leaving the circle clears this.
			if (_suppressed && status is LocationBroadcastStatus.Broadcasting)
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
			_statusLatched = terminal;
		}

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

/// <summary>How one hub-then-REST send ended.</summary>
internal enum SendOutcome
{
	/// <summary>One of the two transports carried it.</summary>
	Sent = 0,

	/// <summary>Abandoned between the two: a newer fix was already waiting.</summary>
	Superseded = 1,

	/// <summary>Both transports refused.</summary>
	Failed = 2,
}

/// <summary>One send's result.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Failure">Why the last transport refused, when it did.</param>
internal readonly record struct SendResult(SendOutcome Outcome, string? Failure);

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
