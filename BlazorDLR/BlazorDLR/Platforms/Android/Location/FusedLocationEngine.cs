using Android.Content;
using Android.Gms.Common;
using Android.Gms.Location;
using Android.OS;
using BlazorDLR.Shared.Services;
using AndroidLocation = Android.Locations.Location;

namespace BlazorDLR.Platforms.Android.Location;

/// <summary>
/// §4.3's specified receiver: Google's fused location provider, which blends GNSS with Wi-Fi, cell
/// and the motion sensors.
/// <para>
/// The reason it is the default rather than <c>LocationManager</c> is not accuracy in the open —
/// raw GNSS is fine there. It is everywhere else: a street of tall buildings, a tunnel exit, the
/// first thirty seconds after the phone comes out of a pocket. Fused keeps producing a usable fix
/// through all three, and it does the duty-cycling that makes the Eco profile actually cost less
/// battery rather than merely reporting less often.
/// </para>
/// <para>
/// <strong>Availability is checked, not assumed</strong> — see
/// <see cref="LocationEngineFactory"/>. On a device with no Play Services this type is never
/// constructed.
/// </para>
/// </summary>
internal sealed class FusedLocationEngine : ILocationEngine
{
	private readonly Context _context;
	private readonly Action<LocationFix> _publish;
	private IFusedLocationProviderClient? _client;
	private FixCallback? _callback;

	/// <summary>Creates the engine over an Android context.</summary>
	/// <param name="context">The service the receiver runs inside.</param>
	/// <param name="publish">Where fixes go.</param>
	public FusedLocationEngine(Context context, Action<LocationFix> publish)
	{
		_context = context;
		_publish = publish;
	}

	/// <summary>
	/// Whether this device has a working Play Services to talk to.
	/// <para>
	/// <c>IsGooglePlayServicesAvailable</c> answers several kinds of "no" — missing, disabled, too
	/// old — and only <c>Success</c> is a yes. The others are treated identically here: any of them
	/// means the fused client will not deliver, and the caller has a fallback that will.
	/// </para>
	/// </summary>
	/// <param name="context">Any Android context.</param>
	public static bool IsAvailable(Context context)
	{
		try
		{
			return GoogleApiAvailability.Instance.IsGooglePlayServicesAvailable(context)
				== ConnectionResult.Success;
		}
		catch
		{
			// The availability check itself throws on some minimal ROMs. Treating that as "not
			// available" is the answer that keeps the app working.
			return false;
		}
	}

	/// <inheritdoc />
	public void Start(AccuracyProfile profile)
	{
		_client = LocationServices.GetFusedLocationProviderClient(_context);
		_callback = new FixCallback(_publish);

		long intervalMs = (long)AndroidLocationRequestSpec.Interval(profile).TotalMilliseconds;

		LocationRequest request = new LocationRequest.Builder(intervalMs)
			.SetPriority(Priority(profile))
			// The floor on how often a fix may arrive. Half the interval: fused will hand over a
			// fix produced for another app at no extra battery cost, and on a motorcycle a fix
			// that arrives early is worth having.
			.SetMinUpdateIntervalMillis(Math.Max(500, intervalMs / 2))
			.SetMinUpdateDistanceMeters((float)AndroidLocationRequestSpec.MinDistanceM(profile))
			// False on purpose: true makes the first callback wait for a fix that meets the
			// requested accuracy, which at a cold start under cloud can be tens of seconds of the
			// rider looking at a map with no pin on it. A rough first fix, filtered by
			// PositionGate, is the better trade.
			.SetWaitForAccurateLocation(false)
			.Build();

		// Looper.MainLooper rather than the calling thread: the service's OnStartCommand thread
		// has no looper of its own, and RequestLocationUpdates needs one to post callbacks to.
		_client.RequestLocationUpdates(request, _callback, Looper.MainLooper);
	}

	/// <inheritdoc />
	public void Stop()
	{
		if (_client is not null && _callback is not null)
		{
			_client.RemoveLocationUpdates(_callback);
		}

		_callback?.Dispose();
		_callback = null;
		_client = null;
	}

	/// <summary>
	/// The battery/accuracy trade each profile asks Play Services for.
	/// <para>
	/// Eco is <em>balanced power</em> rather than <em>low power</em>: low power is the block-level
	/// tier, roughly a kilometre, which cannot place a rider on a road at all. Eco is meant to be
	/// a touring cadence, not a different kind of answer.
	/// </para>
	/// </summary>
	private static int Priority(AccuracyProfile profile) => profile switch
	{
		AccuracyProfile.Eco => global::Android.Gms.Location.Priority.PriorityBalancedPowerAccuracy,
		_ => global::Android.Gms.Location.Priority.PriorityHighAccuracy,
	};

	/// <summary>
	/// Play Services hands fixes back through this. A batch may carry more than one — the API
	/// coalesces while the device is dozing — and every one of them is published: the gate
	/// upstream is what decides which are worth sending, and it needs to see them in order.
	/// </summary>
	private sealed class FixCallback : LocationCallback
	{
		private readonly Action<LocationFix> _publish;

		public FixCallback(Action<LocationFix> publish) => _publish = publish;

		public override void OnLocationResult(LocationResult result)
		{
			foreach (AndroidLocation location in result.Locations)
			{
				_publish(location.ToFix());
			}
		}
	}
}
