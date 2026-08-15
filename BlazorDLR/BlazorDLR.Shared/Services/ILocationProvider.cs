namespace BlazorDLR.Shared.Services;

/// <summary>
/// The GPS seam (§4.3, §18.2). <strong>Mobile only.</strong>
/// <para>
/// A foreground service on Android and <c>CLLocationManager</c> on iOS, with the accuracy
/// profiles from §4.2. Both are implemented in <c>BlazorDLR/Platforms/</c>;
/// <see cref="Platform.NoopLocationProvider"/> covers the Windows and macOS MAUI heads.
/// </para>
/// <para>
/// <strong>The web hosts do not register this at all.</strong> A browser cannot deliver the
/// background, high-cadence fixes a live ride needs, and binding a "not supported" stub there
/// only made every screen above it explain why it was doing nothing. Recording and publishing
/// are mobile features (§18.6); receiving is not a GPS concern, so the web still draws every
/// other rider from the hub as usual. Shared code that wants this — or anything built on it —
/// resolves it with <c>GetService</c> and treats <c>null</c> as "this host has no receiver".
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
	/// <summary>60 s / 50 m — touring.</summary>
	Eco = 0,

	/// <summary>30 s / 10 m — the default.</summary>
	Balanced = 1,

	/// <summary>10 s / 5 m — twisty roads, track days.</summary>
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
