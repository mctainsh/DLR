namespace DLR.Core.Tracks;

/// <summary>
/// One fix on a track (§15.1).
/// <para>
/// Elevation and time are nullable and commonly absent - an imported route has neither, and
/// plenty of exporters emit one without the other. Nothing downstream may substitute a zero:
/// zero implies a measurement, null says there was none (§8).
/// </para>
/// </summary>
/// <param name="Latitude">Degrees, −90 to 90.</param>
/// <param name="Longitude">Degrees, −180 to 180.</param>
/// <param name="ElevationM">Metres above sea level, if the file said so. Never interpolated.</param>
/// <param name="TimeUtc">When the fix was taken, if the file said so.</param>
public readonly record struct TrackPoint(
	double Latitude,
	double Longitude,
	double? ElevationM = null,
	DateTimeOffset? TimeUtc = null)
{
	/// <summary>Whether the coordinates are inside the ranges a coordinate can have.</summary>
	/// <remarks>
	/// <c>NaN</c> fails every comparison, so the range checks reject it without a separate
	/// clause - which is worth saying out loud, because it looks like an omission.
	/// </remarks>
	public bool HasUsableCoordinates =>
		Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;
}
