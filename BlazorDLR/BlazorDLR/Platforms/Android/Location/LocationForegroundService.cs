using System.Threading.Channels;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.OS;
using Android.Runtime;
using AndroidX.Core.App;
using BlazorDLR.Shared.Services;
using AndroidLocation = Android.Locations.Location;
using AndroidUri = Android.Net.Uri;

namespace BlazorDLR.Platforms.Android.Location;

/// <summary>
/// The Android half of §4.3 — a started foreground service that owns the receiver, so fixes keep
/// arriving when the app is backgrounded and when the screen is off.
/// <para>
/// <strong>Why a service and not a timer in the app.</strong> Since Android 8 a backgrounded
/// process gets location updates a few times an hour, whatever it asked for, and since Android 12
/// it cannot start one from the background at all. A ride is exactly the case those limits exist to
/// stop, so the only supported way to do it is the one the platform offers deliberately: a
/// foreground service, typed <c>location</c>, with a notification the rider cannot dismiss. That
/// notification is not an inconvenience to be minimised — it is the contract. It is what makes
/// "this app is using your location right now" impossible to miss, and Play reviews the app
/// against exactly that claim.
/// </para>
/// <para>
/// <strong>START_STICKY.</strong> A ride outlives memory pressure. If the OS kills this service it
/// is restarted with a null intent, and <see cref="OnStartCommand"/> treats that as "start
/// watching again on the last profile" rather than as a fresh command.
/// </para>
/// <para>
/// <strong>The fixes leave through a channel, not an event.</strong> The consumer is an
/// <c>IAsyncEnumerable</c> on the shared side (<see cref="ILocationProvider.WatchAsync"/>), and a
/// bounded channel that drops the oldest is the honest backpressure policy for positions: if the
/// consumer is behind, the fix worth keeping is the newest one.
/// </para>
/// </summary>
[Service(
	Enabled = true,
	Exported = false,
	// Declared here as well as in the manifest: this attribute is what the generated <service>
	// element carries, and a foreground service started with a type the manifest does not grant
	// throws at startForeground() on Android 14+.
	ForegroundServiceType = ForegroundService.TypeLocation)]
public sealed class LocationForegroundService : Service
{
	/// <summary>Starts (or re-tunes) the receiver. Carries the profile as an int extra.</summary>
	public const string ActionStart = "au.com.securehub.dlr.location.START";

	/// <summary>Stops the receiver and takes the notification down with it.</summary>
	public const string ActionStop = "au.com.securehub.dlr.location.STOP";

	/// <summary>The extra carrying <see cref="AccuracyProfile"/> as an int.</summary>
	public const string ExtraProfile = "profile";

	/// <summary>
	/// The notification channel. Low importance: it must be present and permanent, and it must
	/// never make a sound — a rider gets one of these for the whole ride, not one per fix.
	/// </summary>
	private const string ChannelId = "dlr.location";

	private const int NotificationId = 4301;

	/// <summary>
	/// How many fixes may queue for a slow consumer. Small on purpose — see the channel note in
	/// the type's remarks. Ten is a few seconds of Precise.
	/// </summary>
	private const int FixBufferSize = 10;

	private static readonly Channel<LocationFix> Fixes = Channel.CreateBounded<LocationFix>(
		new BoundedChannelOptions(FixBufferSize)
		{
			FullMode = BoundedChannelFullMode.DropOldest,
			SingleReader = true,
			SingleWriter = false,
		});

	private ILocationEngine? _engine;
	private AccuracyProfile _profile = AccuracyProfile.Balanced;

	/// <summary>
	/// The stream the shared provider reads. Static because the OS owns the service instance's
	/// lifetime — it can be killed and restarted under a consumer that is still enumerating, and a
	/// stream tied to one instance would end silently when that happened.
	/// </summary>
	public static ChannelReader<LocationFix> Reader => Fixes.Reader;

	/// <summary>Whether a receiver is currently running. What <see cref="ILocationProvider.IsRecording"/> answers.</summary>
	public static bool IsRunning { get; private set; }

	/// <summary>Not a bound service — the app talks to it with intents.</summary>
	/// <param name="intent">Ignored.</param>
	public override IBinder? OnBind(Intent? intent) => null;

	/// <inheritdoc />
	public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
	{
		if (intent?.Action == ActionStop)
		{
			StopWatching();
			StopForeground(StopForegroundFlags.Remove);
			StopSelf();
			return StartCommandResult.NotSticky;
		}

		// A null intent is the OS restarting us after a kill (START_STICKY). The profile from the
		// last start is what to resume on, which is why it is a field rather than a local.
		if (intent?.HasExtra(ExtraProfile) == true)
		{
			_profile = (AccuracyProfile)intent.GetIntExtra(ExtraProfile, (int)AccuracyProfile.Balanced);
		}

		// Before anything else that can throw: Android 14 gives a service five seconds to call
		// this, and an ANR here is a crash the rider reads as the app dying when they started
		// their ride.
		StartForeground(NotificationId, BuildNotification());

		StartWatching();

		return StartCommandResult.Sticky;
	}

	/// <inheritdoc />
	public override void OnDestroy()
	{
		StopWatching();
		base.OnDestroy();
	}

	/// <summary>
	/// Asks the OS to start the service, or to re-tune a running one to a new profile.
	/// <para>
	/// <c>StartForegroundService</c> on Oreo and later: a plain <c>StartService</c> from a
	/// backgrounded app is refused outright, and the app is backgrounded the moment the rider puts
	/// the phone in the mount.
	/// </para>
	/// </summary>
	/// <param name="context">Any context; the application context is used.</param>
	/// <param name="profile">The accuracy profile to run at (§4.2).</param>
	public static void Start(Context context, AccuracyProfile profile)
	{
		Context app = context.ApplicationContext ?? context;

		Intent intent = new(app, typeof(LocationForegroundService));
		intent.SetAction(ActionStart);
		intent.PutExtra(ExtraProfile, (int)profile);

		if (OperatingSystem.IsAndroidVersionAtLeast(26))
		{
			app.StartForegroundService(intent);
		}
		else
		{
			app.StartService(intent);
		}
	}

	/// <summary>Stops the receiver and removes the notification.</summary>
	/// <param name="context">Any context; the application context is used.</param>
	public static void Stop(Context context)
	{
		Context app = context.ApplicationContext ?? context;

		Intent intent = new(app, typeof(LocationForegroundService));
		intent.SetAction(ActionStop);

		// StartService, not StartForegroundService: this one is telling a running service to stop,
		// and asking the OS to promote a service that is about to die trips the "did not call
		// startForeground in time" watchdog.
		app.StartService(intent);
	}

	private void StartWatching()
	{
		if (_engine is not null)
		{
			// A re-tune rather than a start: the rider changed profile mid-ride.
			_engine.Stop();
			_engine = null;
		}

		_engine = LocationEngineFactory.Create(this, Publish);
		_engine.Start(_profile);
		IsRunning = true;
	}

	private void StopWatching()
	{
		_engine?.Stop();
		_engine = null;
		IsRunning = false;
	}

	private static void Publish(LocationFix fix) => Fixes.Writer.TryWrite(fix);

	/// <summary>
	/// The notification the rider cannot dismiss while this runs.
	/// <para>
	/// It says what is happening and why, in those words, because it is the only part of the
	/// background-location story most riders will ever read — and because Play's policy for
	/// background location is judged on whether the app is honest about it at the moment it
	/// happens, not only in the listing.
	/// </para>
	/// </summary>
	private Notification BuildNotification()
	{
		EnsureChannel();

		// Tapping it opens the app rather than doing nothing — a notification that is not a way
		// back into the thing it describes is a dead end on a lock screen.
		Intent? launch = PackageManager?.GetLaunchIntentForPackage(PackageName!);

		PendingIntent? content = launch is null
			? null
			: PendingIntent.GetActivity(
				this,
				0,
				launch,
				OperatingSystem.IsAndroidVersionAtLeast(23)
					? PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent
					: PendingIntentFlags.UpdateCurrent);

		// Called as statements rather than chained. AndroidX's builder returns itself from every
		// setter, but the binding types that return value as nullable, so a fluent chain is a
		// sequence of possible-null dereferences the compiler is right to complain about. The
		// setters mutate in place, so this is the same builder either way.
		NotificationCompat.Builder builder = new(this, ChannelId);

		builder.SetContentTitle("Sharing your location");
		builder.SetContentText("Your position is going to the group adventures you are sharing with.");

		// Ours, not a stock android.R drawable — see the comment in the resource itself. A
		// notification with a small icon the platform cannot resolve is never posted, and a
		// foreground service whose notification never posts is a service that cannot start.
		builder.SetSmallIcon(global::BlazorDLR.Resource.Drawable.dlr_location_notification);
		builder.SetOngoing(true);
		builder.SetCategory(NotificationCompat.CategoryService);
		builder.SetPriority(NotificationCompat.PriorityLow);

		// Public: the whole point is that it is visible on a locked screen, which is where the
		// phone spends a ride.
		builder.SetVisibility(NotificationCompat.VisibilityPublic);
		builder.SetShowWhen(false);

		if (content is not null)
		{
			builder.SetContentIntent(content);
		}

		return builder.Build()
			?? throw new InvalidOperationException(
				"The location notification could not be built, so the foreground service cannot start.");
	}

	private void EnsureChannel()
	{
		if (!OperatingSystem.IsAndroidVersionAtLeast(26))
		{
			return;
		}

		NotificationManager? manager = (NotificationManager?)GetSystemService(NotificationService);

		if (manager is null || manager.GetNotificationChannel(ChannelId) is not null)
		{
			return;
		}

		NotificationChannel channel = new(
			ChannelId,
			"Location sharing",
			NotificationImportance.Low)
		{
			Description = "Shown while this phone is sharing its position with a group adventure.",
		};

		channel.SetShowBadge(false);
		channel.SetSound(null, null);
		channel.EnableVibration(false);

		manager.CreateNotificationChannel(channel);
	}
}

/// <summary>What the service drives, whichever location API is available on the device.</summary>
internal interface ILocationEngine
{
	/// <summary>Begins delivering fixes at the profile's cadence.</summary>
	/// <param name="profile">The accuracy profile (§4.2).</param>
	void Start(AccuracyProfile profile);

	/// <summary>Stops delivering, releasing whatever the platform was holding.</summary>
	void Stop();
}

/// <summary>
/// Chooses the location API this device actually has.
/// <para>
/// §4.3 specifies the <strong>fused location provider</strong>, and that is the right default: it
/// blends GNSS, Wi-Fi, cell and the motion sensors, which is what keeps a fix alive in a street of
/// tall buildings and what makes the "balanced" profile cost what it claims to.
/// </para>
/// <para>
/// It is not, however, universally present. Play Services is absent on Huawei's recent phones, on
/// de-Googled Android, and in some emulators — and on those devices the fused client throws or
/// silently never calls back, which is indistinguishable from a phone that cannot see the sky. So
/// the platform's own <see cref="LocationManager"/> is kept as a fallback: it is worse at all the
/// things fused is good at, and it is very much better than an app that looks broken.
/// </para>
/// </summary>
internal static class LocationEngineFactory
{
	/// <summary>Builds the best engine this device supports.</summary>
	/// <param name="context">The service, used as the Android context.</param>
	/// <param name="publish">Where fixes go.</param>
	public static ILocationEngine Create(Context context, Action<LocationFix> publish) =>
		FusedLocationEngine.IsAvailable(context)
			? new FusedLocationEngine(context, publish)
			: new PlatformLocationEngine(context, publish);
}

/// <summary>
/// The last-resort engine: <c>LocationManager</c> with the GPS provider, present on every Android
/// device since the beginning. Used when Play Services is not installed — see
/// <see cref="LocationEngineFactory"/>.
/// </summary>
internal sealed class PlatformLocationEngine : Java.Lang.Object, ILocationEngine, ILocationListener
{
	private readonly Context _context;
	private readonly Action<LocationFix> _publish;
	private LocationManager? _manager;

	public PlatformLocationEngine(Context context, Action<LocationFix> publish)
	{
		_context = context;
		_publish = publish;
	}

	public void Start(AccuracyProfile profile)
	{
		_manager = (LocationManager?)_context.GetSystemService(Context.LocationService);

		if (_manager is null)
		{
			return;
		}

		// The provider list is asked rather than assumed: an emulator with no GPS provider throws
		// IllegalArgumentException from RequestLocationUpdates, and a ride that crashes the moment
		// it starts is worse than one with no fixes.
		string? provider =
			_manager.IsProviderEnabled(LocationManager.GpsProvider) ? LocationManager.GpsProvider
			: _manager.IsProviderEnabled(LocationManager.NetworkProvider) ? LocationManager.NetworkProvider
			: null;

		if (provider is null)
		{
			return;
		}

		_manager.RequestLocationUpdates(
			provider,
			(long)AndroidLocationRequestSpec.Interval(profile).TotalMilliseconds,
			(float)AndroidLocationRequestSpec.MinDistanceM(profile),
			this);
	}

	public void Stop()
	{
		_manager?.RemoveUpdates(this);
		_manager = null;
	}

	/// <inheritdoc />
	public void OnLocationChanged(AndroidLocation location) => _publish(location.ToFix());

	/// <inheritdoc />
	public void OnProviderDisabled(string provider) { }

	/// <inheritdoc />
	public void OnProviderEnabled(string provider) { }

	/// <inheritdoc />
	public void OnStatusChanged(string? provider, [GeneratedEnum] Availability status, Bundle? extras) { }
}

/// <summary>
/// The cadence and precision each profile asks the platform for (§4.2).
/// <para>
/// Deliberately <em>not</em> the same numbers <c>PositionGate</c> enforces. This is what the
/// receiver is asked to produce; the gate is what survives the trip to the server. Asking the
/// platform for exactly the publish cadence would leave nothing to filter and no way to notice a
/// rider stopping between two intervals — so the receiver runs a little faster than the wire, and
/// the gate spends the difference.
/// </para>
/// </summary>
internal static class AndroidLocationRequestSpec
{
	/// <summary>How often the receiver is asked for a fix.</summary>
	/// <param name="profile">The rider's profile.</param>
	public static TimeSpan Interval(AccuracyProfile profile) => profile switch
	{
		AccuracyProfile.Eco => TimeSpan.FromSeconds(5),
		AccuracyProfile.Precise => TimeSpan.FromSeconds(1),
		_ => TimeSpan.FromSeconds(2),
	};

	/// <summary>The smallest movement the receiver reports, in metres.</summary>
	/// <param name="profile">The rider's profile.</param>
	public static double MinDistanceM(AccuracyProfile profile) => profile switch
	{
		AccuracyProfile.Eco => 10,
		AccuracyProfile.Precise => 0,
		_ => 5,
	};
}

/// <summary>Turns an Android fix into the shared one.</summary>
internal static class AndroidLocationExtensions
{
	/// <summary>
	/// Maps the platform's fix, keeping "the platform did not say" distinct from zero (§8).
	/// <para>
	/// Android reports speed, bearing and accuracy as plain floats that read 0 when unset, and asks
	/// separately whether each was set at all. Trusting the float would tell a ride that a rider on
	/// the motorway is stationary and pointing due north.
	/// </para>
	/// </summary>
	/// <param name="location">The platform's fix.</param>
	public static LocationFix ToFix(this AndroidLocation location) => new(
		location.Latitude,
		location.Longitude,
		location.HasAccuracy ? location.Accuracy : null,
		location.HasSpeed ? location.Speed : null,
		location.HasBearing ? location.Bearing : null,
		// Location.Time is Unix epoch milliseconds from the receiver, not from this process's
		// clock — which is the timestamp the ride wants, and is why reading the ambient clock here
		// would be both wrong and against §10.4.
		DateTimeOffset.FromUnixTimeMilliseconds(location.Time));
}
