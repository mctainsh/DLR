using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BlazorDLR.Shared.Services;
using CoreLocation;
using Foundation;

namespace BlazorDLR.Platforms.Apple.Location;

/// <summary>
/// The iOS half of §4.3: <c>CLLocationManager</c>, configured so fixes keep arriving with the app
/// backgrounded and the screen off.
/// <para>
/// <strong>Three settings do the background work, and all three are load-bearing.</strong>
/// <list type="bullet">
///   <item><see cref="CLLocationManager.AllowsBackgroundLocationUpdates"/> — without it iOS stops
///     delivering the moment the app leaves the foreground. Setting it requires the
///     <c>location</c> background mode in Info.plist; without that entry it throws.</item>
///   <item><c>PausesLocationUpdatesAutomatically = false</c> — the default is true, and iOS will
///     helpfully decide a stationary rider no longer needs updates and stop. It does not restart
///     reliably, so a coffee stop can end a ride's tracking for good.</item>
///   <item><c>ShowsBackgroundLocationIndicator = true</c> — the blue pill in the status bar. It is
///     not required for the API to work, and it is turned on deliberately: this app publishes a
///     rider's position to other people, and the platform's own "you are being located" signal is
///     the honest thing to leave switched on.</item>
/// </list>
/// </para>
/// <para>
/// <strong><c>ActivityType.OtherNavigation</c></strong> rather than <c>AutomotiveNavigation</c>:
/// the automotive profile lets iOS snap fixes to the road network and suppress updates it thinks
/// are off-road, which on a fire trail or in a car park is exactly the wrong assumption for a
/// motorcycle app. <c>OtherNavigation</c> keeps vehicle-speed handling without the road-snapping.
/// </para>
/// <para>
/// <strong>Permission is two rungs, like Android's.</strong> <em>When in use</em> first, then
/// <em>always</em>. iOS will only show the "always" prompt after the first has been granted, and
/// it shows it once ever — a second request on a rider who chose "when in use" does nothing at all,
/// which is why <see cref="EnsurePermissionsAsync"/> treats a when-in-use grant as success rather
/// than blocking on an answer that will never come.
/// </para>
/// </summary>
public sealed class AppleLocationProvider : ILocationProvider
{
	/// <summary>
	/// How many fixes may queue for a consumer that is behind, dropping the oldest — the newest fix
	/// is the one worth keeping. Matches the Android service's buffer.
	/// <para>
	/// This was ten, chosen when the consumer awaited a network round trip per fix. The publish has
	/// since moved off that loop into <c>PositionOutbox</c>, so the consumer only records and gates
	/// and drains this immediately. What the buffer is for now is a batch delivered at once after
	/// the phone has been asleep, every fix of which the rider's own track wants (§15.1).
	/// </para>
	/// </summary>
	private const int FixBufferSize = 64;

	/// <summary>
	/// Core Location's <c>kCLDistanceFilterNone</c>: report every fix, however small the movement.
	/// Spelled out because the constant is not surfaced by the binding, and a bare −1 in a distance
	/// field reads like a bug rather than like the documented sentinel it is.
	/// </summary>
	private const double NoDistanceFilter = -1;

	private readonly object _gate = new();
	private CLLocationManager? _manager;

	/// <inheritdoc />
	public bool IsSupported => true;

	/// <inheritdoc />
	public bool IsRecording { get; private set; }

	/// <inheritdoc />
	public Task<LocationPermissionState> EnsurePermissionsAsync(CancellationToken cancellationToken = default)
	{
		TaskCompletionSource<LocationPermissionState> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

		// CLLocationManager must be created and used on the main thread; this is called from the
		// broadcaster's background pump.
		MainThread.BeginInvokeOnMainThread(() =>
		{
			try
			{
				CLLocationManager manager = EnsureManager();

				void Settle(CLAuthorizationStatus status)
				{
					answer.TrySetResult(status switch
					{
						CLAuthorizationStatus.AuthorizedAlways => LocationPermissionState.Granted,

						// A rider who granted "when in use" gets a working app that stops
						// publishing when the phone is pocketed. Degraded, not broken — and it is
						// their answer, so it is not re-asked.
						CLAuthorizationStatus.AuthorizedWhenInUse => LocationPermissionState.Granted,

						// Restricted is parental controls or an MDM policy: nothing the rider can
						// change from inside this app, which is what DeniedPermanently means.
						CLAuthorizationStatus.Denied or CLAuthorizationStatus.Restricted =>
							LocationPermissionState.DeniedPermanently,

						_ => LocationPermissionState.Denied,
					});
				}

				CLAuthorizationStatus current = manager.AuthorizationStatus;

				if (current != CLAuthorizationStatus.NotDetermined)
				{
					// Already answered once. iOS will not show either prompt again, so the stored
					// answer is the answer — the way back is the Settings app.
					if (current == CLAuthorizationStatus.AuthorizedWhenInUse)
					{
						// Asking for "always" from "when in use" is the one upgrade iOS does allow,
						// and it shows the prompt at most once. Requested without waiting on it:
						// the app is usable on when-in-use, and blocking a ride behind a prompt
						// that may never appear would be worse than the degraded mode.
						manager.RequestAlwaysAuthorization();
					}

					Settle(current);
					return;
				}

				// First run. The delegate's callback is what carries the rider's answer — the
				// request methods return immediately and the status is still NotDetermined when
				// they do.
				manager.AuthorizationChanged += OnChanged;
				manager.RequestWhenInUseAuthorization();

				void OnChanged(object? sender, CLAuthorizationChangedEventArgs args)
				{
					if (args.Status == CLAuthorizationStatus.NotDetermined)
					{
						// The dialog is still on screen.
						return;
					}

					manager.AuthorizationChanged -= OnChanged;

					if (args.Status == CLAuthorizationStatus.AuthorizedWhenInUse)
					{
						manager.RequestAlwaysAuthorization();
					}

					Settle(args.Status);
				}
			}
			catch (Exception exception)
			{
				answer.TrySetException(exception);
			}
		});

		return answer.Task.WaitAsync(cancellationToken);
	}

	/// <summary>
	/// Starts the receiver and yields fixes until the caller stops enumerating.
	/// <para>
	/// The manager is started here and stopped in the <c>finally</c>, so the background-location
	/// assertion — and the blue pill that goes with it — lasts exactly as long as the enumeration.
	/// </para>
	/// </summary>
	/// <param name="rate">The rider's update rate (§4.2).</param>
	/// <param name="cancellationToken">Stops the receiver.</param>
	public async IAsyncEnumerable<LocationFix> WatchAsync(
		LocationUpdateRate rate,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		Channel<LocationFix> fixes = Channel.CreateBounded<LocationFix>(
			new BoundedChannelOptions(FixBufferSize)
			{
				FullMode = BoundedChannelFullMode.DropOldest,
				SingleReader = true,
				SingleWriter = true,
			});

		void OnLocations(object? sender, CLLocationsUpdatedEventArgs args)
		{
			foreach (CLLocation location in args.Locations)
			{
				fixes.Writer.TryWrite(ToFix(location));
			}
		}

		void OnFailed(object? sender, NSErrorEventArgs args)
		{
			// kCLErrorDenied (1) is the only failure worth ending the stream for: everything else
			// — a temporarily unavailable network fix, a heading failure — is transient, and iOS
			// carries on delivering after it. Ending the stream on those would stop a ride
			// because one fix failed.
			if (args.Error?.Code == 1)
			{
				fixes.Writer.TryComplete(new InvalidOperationException(
					"Location services are not permitted for this app."));
			}
		}

		CLLocationManager manager = await MainThread.InvokeOnMainThreadAsync(() =>
		{
			CLLocationManager created = EnsureManager();

			created.DesiredAccuracy = DesiredAccuracy(rate);
			created.DistanceFilter = DistanceFilter(rate);
			created.ActivityType = CLActivityType.OtherNavigation;
			created.PausesLocationUpdatesAutomatically = false;

			// Only legal once "always" or "when in use" has been granted AND the location
			// background mode is declared; iOS throws otherwise. Guarded rather than assumed so a
			// misconfigured build fails with a clear message instead of a crash on the first ride.
			if (created.AuthorizationStatus is CLAuthorizationStatus.AuthorizedAlways
				or CLAuthorizationStatus.AuthorizedWhenInUse)
			{
				created.AllowsBackgroundLocationUpdates = true;

				if (OperatingSystem.IsIOSVersionAtLeast(11))
				{
					created.ShowsBackgroundLocationIndicator = true;
				}
			}

			created.LocationsUpdated += OnLocations;
			created.Failed += OnFailed;
			created.StartUpdatingLocation();

			return created;
		});

		IsRecording = true;

		try
		{
			await foreach (LocationFix fix in fixes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
			{
				yield return fix;
			}
		}
		finally
		{
			IsRecording = false;

			// Fire-and-forget onto the main thread: this runs from the enumerator's disposal,
			// which may itself be on the main thread — awaiting there would deadlock.
			MainThread.BeginInvokeOnMainThread(() =>
			{
				manager.LocationsUpdated -= OnLocations;
				manager.Failed -= OnFailed;
				manager.StopUpdatingLocation();

				// Released explicitly. Left true, iOS keeps the app's background-location
				// assertion — and the blue pill — alive after the ride has ended.
				manager.AllowsBackgroundLocationUpdates = false;
			});
		}
	}

	private CLLocationManager EnsureManager()
	{
		lock (_gate)
		{
			// One manager for the app's lifetime. A new one per watch loses the authorization
			// callback registration mid-prompt, and iOS treats a manager that is garbage-collected
			// while a request is outstanding as a request nobody is listening to.
			return _manager ??= new CLLocationManager();
		}
	}

	/// <summary>
	/// What the rider's rate asks Core Location for (§4.2). Only the update distance is a
	/// statement about precision; the two times are about how often, which is the filter's job.
	/// <para>
	/// <c>Best</c> rather than <c>BestForNavigation</c> even at the finest step: the navigation
	/// tier turns on extra sensor fusion intended for turn-by-turn in a car with the screen on and
	/// external power, and its battery cost on a phone in a mount for six hours is not what a rider
	/// is asking for when they choose a tighter step.
	/// </para>
	/// </summary>
	/// <param name="rate">The rider's rate.</param>
	private static double DesiredAccuracy(LocationUpdateRate rate) =>
		rate.DistanceM <= 5 ? CLLocation.AccuracyBest : CLLocation.AccuracyNearestTenMeters;

	/// <summary>
	/// The smallest movement iOS reports, in metres: a fifth of the rider's update distance,
	/// capped at ten, and off altogether when that fifth is under a metre.
	/// <para>
	/// Deliberately below the publish distance the gate enforces — the receiver has to see the
	/// rider stop, and a filter set at the publish distance would hide exactly that.
	/// </para>
	/// </summary>
	/// <param name="rate">The rider's rate.</param>
	private static double DistanceFilter(LocationUpdateRate rate) =>
		rate.DistanceM / 5 < 1 ? NoDistanceFilter : Math.Min(rate.DistanceM / 5, 10);

	/// <summary>
	/// Maps a Core Location fix, keeping "iOS did not say" distinct from zero (§8).
	/// <para>
	/// Core Location reports the three optional measurements as negative when unavailable — a
	/// convention that silently becomes a real value if it is not checked. A rider with no course
	/// would arrive at the ride heading −1°.
	/// </para>
	/// </summary>
	private static LocationFix ToFix(CLLocation location) => new(
		location.Coordinate.Latitude,
		location.Coordinate.Longitude,
		location.HorizontalAccuracy >= 0 ? location.HorizontalAccuracy : null,
		location.Speed >= 0 ? location.Speed : null,
		location.Course >= 0 ? location.Course : null,
		// The receiver's own stamp, not this process's clock — which is the time the ride wants,
		// and is why there is no ambient clock read here (§10.4).
		ToUtc(location.Timestamp));

	private static DateTimeOffset ToUtc(NSDate timestamp) =>
		new(DateTime.SpecifyKind((DateTime)timestamp, DateTimeKind.Utc));
}
