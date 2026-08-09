using System.Globalization;
using DLR.Core.Tracks;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// A circle around somewhere the rider does not want to be observed — home, most obviously —
/// inside which this device neither records nor publishes a fix (§10.1, §18.6).
/// <para>
/// <strong>This device's answer, and nowhere else's.</strong> It is stored through
/// <see cref="IDeviceSettings"/>, never sent to the server, never carried in an export, and
/// never named to another rider. That is not squeamishness about one more column: an area
/// that says "do not look here" is a precise statement of where somebody lives, so the only
/// copy that cannot leak is the one that never leaves the phone. The consequence is stated
/// plainly in the UI — the setting does not follow you to another device, because it cannot.
/// </para>
/// <para>
/// <strong>Suppression, not obfuscation.</strong> Inside the circle no fix is recorded and
/// none is broadcast, so co-riders see the rider as present in the ride with no position on
/// the map. Publishing a jittered or snapped-to-edge point instead would be worse than
/// useless: several such points bound the true centre, which is the one number this protects.
/// </para>
/// <para>
/// Compare <see cref="RouteStyle"/>, the other device-local preference: same store, same
/// hand-rolled encoding, same "a value we cannot parse means the default" posture. The
/// difference is what a mistake costs, which is why <see cref="Decode"/> answers
/// <c>null</c> — "no area" — rather than guessing at a half-readable one.
/// </para>
/// </summary>
/// <param name="Latitude">Centre latitude in decimal degrees.</param>
/// <param name="Longitude">Centre longitude in decimal degrees.</param>
/// <param name="RadiusM">Radius in metres. Clamped to [<see cref="MinRadiusM"/>, <see cref="MaxRadiusM"/>].</param>
public sealed record PrivateArea(double Latitude, double Longitude, double RadiusM)
{
	/// <summary>
	/// What a newly-placed area gets. A kilometre is a few streets in every direction — wide
	/// enough that the centre is not the obvious middle of a small hole in a track, and narrow
	/// enough that the ride still picks the rider up before the first junction.
	/// </summary>
	public const double DefaultRadiusM = 1000;

	/// <summary>
	/// Smallest radius offered. Below this a consumer GPS's own error is a large fraction of the
	/// circle, so a fix reported just outside it still comes from inside — the setting would look
	/// like it was working while doing nothing.
	/// </summary>
	public const double MinRadiusM = 100;

	/// <summary>
	/// Largest radius offered. Beyond this it stops being a private area around a place and
	/// becomes "do not share on this ride", which is what the per-ride sharing switch is for (§5.6).
	/// </summary>
	public const double MaxRadiusM = 10_000;

	/// <summary>
	/// Whether a position falls inside the area, and must therefore be neither stored nor sent.
	/// <para>
	/// Great-circle distance through <see cref="Distance.BetweenM"/> — the same measure the rest
	/// of the app uses — rather than a degrees-based box. A box would be a different shape at
	/// every latitude, and "1 km" has to mean 1 km on the ground for the copy beside the control
	/// to be true.
	/// </para>
	/// </summary>
	/// <param name="latitudeDeg">The fix's latitude in decimal degrees.</param>
	/// <param name="longitudeDeg">The fix's longitude in decimal degrees.</param>
	public bool Contains(double latitudeDeg, double longitudeDeg)
	{
		if (!double.IsFinite(latitudeDeg) || !double.IsFinite(longitudeDeg))
		{
			// A fix we cannot place cannot be shown to be outside the area. Treating it as inside
			// is the only answer that cannot leak; a fix nobody can locate is worth nothing anyway.
			return true;
		}

		return Distance.BetweenM(
			new TrackPoint(Latitude, Longitude),
			new TrackPoint(latitudeDeg, longitudeDeg)) <= RadiusM;
	}

	/// <summary>
	/// Brings values from a control — or read back from a device that once stored something else —
	/// into range: the radius is clamped, and a centre that is not a real coordinate is refused.
	/// </summary>
	/// <returns>The usable area, or <c>null</c> when the centre is not a point on the earth.</returns>
	public PrivateArea? Normalised()
	{
		if (!double.IsFinite(Latitude) || !double.IsFinite(Longitude)
			|| Latitude is < -90 or > 90 || Longitude is < -180 or > 180)
		{
			return null;
		}

		return this with
		{
			RadiusM = Math.Clamp(double.IsFinite(RadiusM) ? RadiusM : DefaultRadiusM, MinRadiusM, MaxRadiusM),
		};
	}

	/// <summary>
	/// The area as one string for <see cref="IDeviceSettings"/>, leading <c>1</c> being the format
	/// version — the same arrangement as <see cref="RouteStyle.Encode"/> and for the same reasons.
	/// <para>
	/// Six decimal places on the centre, which is about 0.1 m: the coordinate is a house, and a
	/// coarser field would move the circle after a save-and-reload.
	/// </para>
	/// </summary>
	public string Encode()
	{
		PrivateArea safe = Normalised()
			?? throw new InvalidOperationException("A private area with no valid centre cannot be stored.");

		return string.Join('|', [
			"1",
			safe.Latitude.ToString("0.######", CultureInfo.InvariantCulture),
			safe.Longitude.ToString("0.######", CultureInfo.InvariantCulture),
			safe.RadiusM.ToString("0.#", CultureInfo.InvariantCulture),
		]);
	}

	/// <summary>
	/// Reads back what <see cref="Encode"/> wrote.
	/// <para>
	/// Unlike <see cref="RouteStyle.Decode"/>, which repairs a value field by field, anything not
	/// wholly readable here answers <c>null</c> — "this device has no private area". A partially
	/// recovered circle would be a circle somewhere the rider never put one, which is worse than
	/// visibly having lost the setting: the first is silent and the second sends them back to
	/// this screen.
	/// </para>
	/// </summary>
	/// <param name="encoded">A string from <see cref="Encode"/>, or <c>null</c> on a device that has never stored one.</param>
	public static PrivateArea? Decode(string? encoded)
	{
		if (string.IsNullOrWhiteSpace(encoded))
		{
			return null;
		}

		string[] parts = encoded.Split('|');

		if (parts.Length < 4 || parts[0] != "1")
		{
			return null;
		}

		if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude)
			|| !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude)
			|| !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double radius))
		{
			return null;
		}

		return new PrivateArea(latitude, longitude, radius).Normalised();
	}
}
