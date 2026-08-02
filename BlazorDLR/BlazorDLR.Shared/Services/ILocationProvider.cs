namespace BlazorDLR.Shared.Services;

/// <summary>
/// The GPS seam (§4.3, §18.2).
/// <para>
/// <strong>Mobile:</strong> a foreground service on Android and <c>CLLocationManager</c> on
/// iOS, with the accuracy profiles from §4.2. Both platforms implement this from
/// <c>BlazorDLR/Platforms/</c>.
/// <strong>Web:</strong> a browser has no continuous GPS the app can trust, so the web
/// implementation is deliberately non-functional — <see cref="IsSupported"/> is <c>false</c>
/// and every method throws <see cref="NotSupportedException"/>. Recording and publishing are
/// mobile features (§18.6); a component that reaches for this on the web has picked a side.
/// </para>
/// </summary>
public interface ILocationProvider
{
	/// <summary>Whether the current host can produce fixes at all. False in the browser.</summary>
	bool IsSupported { get; }

	/// <summary>Whether this device is currently recording.</summary>
	bool IsRecording { get; }

	/// <summary>Ask for the permissions this platform needs before <see cref="WatchAsync"/> can be called.</summary>
	Task<LocationPermissionState> EnsurePermissionsAsync(CancellationToken cancellationToken = default);

	/// <summary>Fixes as they arrive. Bounded by the profile the caller passes in.</summary>
	IAsyncEnumerable<LocationFix> WatchAsync(AccuracyProfile profile, CancellationToken cancellationToken = default);
}

/// <summary>The three accuracy profiles from §4.2.</summary>
public enum AccuracyProfile
{
	/// <summary>10 s / 25 m — touring.</summary>
	Eco = 0,

	/// <summary>5 s / 10 m — the default.</summary>
	Balanced = 1,

	/// <summary>1 s / 5 m — twisty roads, track days.</summary>
	Precise = 2,
}

/// <summary>What the platform said when asked for GPS permission.</summary>
public enum LocationPermissionState
{
	/// <summary>The caller may start watching.</summary>
	Granted = 0,

	/// <summary>Denied for now — the caller may prompt again.</summary>
	Denied = 1,

	/// <summary>Denied permanently — the caller must send the rider to system settings.</summary>
	DeniedPermanently = 2,

	/// <summary>This host does not do GPS at all — the browser (§18.6).</summary>
	NotSupported = 3,
}

/// <summary>One fix from the platform's location service.</summary>
/// <param name="Latitude">Decimal degrees.</param>
/// <param name="Longitude">Decimal degrees.</param>
/// <param name="AccuracyM">Horizontal accuracy in metres, or null if the platform did not report it.</param>
/// <param name="SpeedMps">Metres per second, or null.</param>
/// <param name="HeadingDeg">Degrees from true north, or null.</param>
/// <param name="RecordedUtc">When the platform stamped the fix.</param>
public sealed record LocationFix(
	double Latitude,
	double Longitude,
	double? AccuracyM,
	double? SpeedMps,
	double? HeadingDeg,
	DateTimeOffset RecordedUtc);
