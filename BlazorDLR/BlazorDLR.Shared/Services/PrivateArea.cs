using System.Globalization;
using DLR.Core.Contracts.Identity;
using DLR.Core.Tracks;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// A circle around somewhere the rider does not want to be observed — home, most obviously —
/// inside which nothing this device knows about a rider's position leaves it (§10.1, §18.6).
/// <para>
/// <strong>Two places consult it, at two different moments.</strong> The broadcaster asks about
/// every fix before publishing it, and drops the ones inside where they were read. The recorder
/// does <em>not</em> ask — it keeps them, in the device-local store this area is cached in — and
/// the Location screen asks again when the rider saves that track, defaulting to cutting them
/// out. The rule the pair enforces is the one that matters: a coordinate from inside the circle
/// never reaches another rider without the person holding the phone choosing it point-blank.
/// </para>
/// <para>
/// <strong>The account holds it; this device caches it.</strong> It used to be the other way
/// round — device-only, never sent — and that lost people their private area. An app update, a
/// reinstall, a cleared browser store or a new phone each wiped it in silence, and a rider who
/// believes they have a circle around their house and does not is broadcasting from their
/// doorstep. The area is therefore stored on the rider's profile (<c>/api/v1/me/private-area</c>)
/// and mirrored into <see cref="IDeviceSettings"/> so the gate still answers with no network.
/// See <see cref="PrivateAreaSettings"/> for what that costs and what it does not: the server
/// can read the circle, but no other rider ever can.
/// </para>
/// <para>
/// <strong>Suppression, not obfuscation.</strong> Inside the circle nothing is broadcast, so
/// co-riders see the rider as present in the ride with no position on the map. Publishing a
/// jittered or snapped-to-edge point instead would be worse than useless: several such points
/// bound the true centre, which is the one number this protects. The same reasoning is why the
/// saved-track filter removes points and leaves a segment break rather than drawing across the
/// gap — a straight line between the two ends of the hole passes through the middle of it.
/// </para>
/// <para>
/// Compare <see cref="RouteStyle"/>, which is still genuinely device-local: same store, same
/// hand-rolled encoding, same "a value we cannot parse means the default" posture. The
/// differences are that this one is a cache of an account setting rather than the setting
/// itself, and that a mistake costs more — which is why <see cref="Decode"/> answers
/// <c>null</c> rather than guessing at a half-readable circle, and why
/// <see cref="TryDecodeCached"/> exists to separate "no area" from "this device has not been
/// told yet".
/// </para>
/// </summary>
/// <param name="Latitude">Centre latitude in decimal degrees.</param>
/// <param name="Longitude">Centre longitude in decimal degrees.</param>
/// <param name="RadiusM">Radius in metres. Clamped to [<see cref="MinRadiusM"/>, <see cref="MaxRadiusM"/>].</param>
public sealed record PrivateArea(double Latitude, double Longitude, double RadiusM)
{
	/// <inheritdoc cref="PrivateAreaSettings.DefaultRadiusM" />
	public const double DefaultRadiusM = PrivateAreaSettings.DefaultRadiusM;

	/// <inheritdoc cref="PrivateAreaSettings.MinRadiusM" />
	public const double MinRadiusM = PrivateAreaSettings.MinRadiusM;

	/// <inheritdoc cref="PrivateAreaSettings.MaxRadiusM" />
	public const double MaxRadiusM = PrivateAreaSettings.MaxRadiusM;

	/// <summary>
	/// What <see cref="EncodeNone"/> writes and <see cref="TryDecodeCached"/> reads back: the
	/// account was asked, and it has no private area.
	/// <para>
	/// Version-prefixed like a real value so an older build reading it gets <c>null</c> from
	/// <see cref="Decode"/> — "no area", which is what it means — rather than a parse it half
	/// believes.
	/// </para>
	/// </summary>
	public const string NoneMarker = "1|none";

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
	/// Brings values from a control — or read back from a store that once held something else —
	/// into range: the radius is clamped, and a centre that is not a real coordinate is refused.
	/// <para>
	/// One implementation, on the wire contract, because the endpoint applies the same rule and
	/// the two must not be able to disagree (§7.14).
	/// </para>
	/// </summary>
	/// <returns>The usable area, or <c>null</c> when the centre is not a point on the earth.</returns>
	public PrivateArea? Normalised() =>
		ToSettings().Normalised() is { } safe ? From(safe) : null;

	/// <summary>The area as the type that crosses the wire.</summary>
	public PrivateAreaSettings ToSettings() => new(Latitude, Longitude, RadiusM);

	/// <summary>The area as the gate and the map overlay want it.</summary>
	/// <param name="settings">What the account answered with.</param>
	public static PrivateArea From(PrivateAreaSettings settings) =>
		new(settings.Latitude, settings.Longitude, settings.RadiusM);

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

	/// <summary>What to cache when the account answered "no private area". See <see cref="NoneMarker"/>.</summary>
	public static string EncodeNone() => NoneMarker;

	/// <summary>
	/// Reads back what <see cref="Encode"/> wrote.
	/// <para>
	/// Unlike <see cref="RouteStyle.Decode"/>, which repairs a value field by field, anything not
	/// wholly readable here answers <c>null</c> — "no private area". A partially recovered circle
	/// would be a circle somewhere the rider never put one, which is worse than visibly having
	/// lost the setting: the first is silent and the second sends them back to this screen.
	/// </para>
	/// </summary>
	/// <param name="encoded">A string from <see cref="Encode"/>, or <c>null</c> where nothing is cached.</param>
	public static PrivateArea? Decode(string? encoded)
	{
		TryDecodeCached(encoded, out PrivateArea? area);
		return area;
	}

	/// <summary>
	/// Reads a cached value and — unlike <see cref="Decode"/> — says whether it was a statement
	/// about the account at all.
	/// <para>
	/// <strong>Three states, not two, and the third is the reason this method exists.</strong>
	/// "The account has a circle here", "the account has no circle", and "this device has never
	/// been told" are different, and only the first two are safe to broadcast from. A device that
	/// has never been told keeps the gate shut until the server answers — see
	/// <c>PrivateAreaState.HidesLocation</c>.
	/// </para>
	/// </summary>
	/// <param name="encoded">The cached string, or <c>null</c> where nothing is cached.</param>
	/// <param name="area">The cached area, or <c>null</c> when the cache says there is none.</param>
	/// <returns>
	/// <c>true</c> when the cache holds a readable answer — a circle or an explicit "none";
	/// <c>false</c> when this device has nothing usable and must ask the server.
	/// </returns>
	public static bool TryDecodeCached(string? encoded, out PrivateArea? area)
	{
		area = null;

		if (string.IsNullOrWhiteSpace(encoded))
		{
			return false;
		}

		if (encoded == NoneMarker)
		{
			return true;
		}

		string[] parts = encoded.Split('|');

		if (parts.Length < 4 || parts[0] != "1")
		{
			return false;
		}

		if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude)
			|| !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude)
			|| !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double radius))
		{
			return false;
		}

		area = new PrivateArea(latitude, longitude, radius).Normalised();

		// A centre that is not a point on the earth is a corrupt cache, not an account saying
		// "none" — ask the server rather than opening the gate on it.
		return area is not null;
	}
}
