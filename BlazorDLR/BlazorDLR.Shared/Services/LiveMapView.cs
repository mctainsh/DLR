using System.Globalization;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// Where the live ride map was last looking, and whether it was following the rider — so
/// re-opening it puts the same ground back on screen instead of the app's default camera
/// (§5.3).
/// <para>
/// <strong>Why this is persisted at all.</strong> The live map is a page, so leaving it for
/// the info screen, the marker composer or the ride thread disposes it, and every one of
/// those is a one-tap round trip a rider makes repeatedly mid-ride. Rebuilding the view by
/// hand each time — pan back to the group, zoom back in, at the side of a road — is the sort
/// of small repeated cost that makes a map feel hostile.
/// </para>
/// <para>
/// <strong>One slot, stamped with the ride it belongs to.</strong> A map of every ride a
/// device has ever opened would grow without a bound and without anything to prune it by;
/// the view that matters is the one the rider was just looking at. <see cref="RideId"/> is
/// what stops the stored camera being applied to a <em>different</em> ride, which would open
/// a Melbourne ride over Sydney — see <see cref="IsFor"/>.
/// </para>
/// <para>
/// Device-local like <see cref="RouteStyle"/> and <see cref="PrivateArea"/>: same store, same
/// hand-rolled versioned encoding, same "a value we cannot parse means the default" posture.
/// It never travels to the server — where one rider last panned to is not the ride's business.
/// </para>
/// </summary>
/// <param name="RideId">The ride the camera belongs to. <see cref="Latitude"/>/<see cref="Longitude"/>/<see cref="ZoomLevel"/> mean nothing without it.</param>
/// <param name="Latitude">Centre latitude in decimal degrees.</param>
/// <param name="Longitude">Centre longitude in decimal degrees.</param>
/// <param name="ZoomLevel">The base map's zoom, as <see cref="MapCamera.ZoomLevel"/> means it.</param>
/// <param name="FollowMe">
/// Whether the map was keeping this rider centred. Deliberately <em>not</em> gated on
/// <see cref="RideId"/> when it is read back (see <see cref="Decode"/>): the camera is about one
/// ride, but "keep me on screen" is how a rider likes to be ridden with, and it should survive
/// into the next ride the way the route style does.
/// </param>
/// <param name="HeadingUp">
/// Whether the map was being turned to the rider's heading rather than held north-up.
/// <para>
/// Carried for the same reason as <see cref="FollowMe"/> and read back the same way — which way
/// up somebody reads a map is a standing preference, not a fact about one ride's ground.
/// </para>
/// <para>
/// A flag of its own rather than a second state of <see cref="FollowMe"/>. The two are not
/// independent on the live page — a turning map is only legible with the rider at the centre of
/// it, so heading-up is never on there without following — but that is a rule about the controls,
/// enforced where the controls are, and this is a record of what the map was doing. Storing it
/// separately means a value written by a build with different rules still reads back as what it
/// said, rather than being reinterpreted by whichever pairing is current.
/// </para>
/// <para>
/// Defaulted, so the value a device stored before this existed still decodes. North-up is the
/// right default for a missing field as well as for a first run: it is what every map in the app
/// has always drawn.
/// </para>
/// </param>
public sealed record LiveMapView(
	Guid RideId,
	double Latitude,
	double Longitude,
	double ZoomLevel,
	bool FollowMe,
	bool HeadingUp = false)
{
	/// <summary>
	/// The <see cref="IDeviceSettings"/> key. Namespaced like <c>dlr.route-style</c> and
	/// <c>dlr.private-area</c>, with the format version inside the value rather than in the key.
	/// </summary>
	public const string StorageKey = "dlr.live-map-view";

	/// <summary>Widest view any of the three base maps offers — the whole world.</summary>
	public const double MinZoomLevel = 0;

	/// <summary>Closest view any of the three base maps offers.</summary>
	public const double MaxZoomLevel = 22;

	/// <summary>
	/// Whether this stored view describes <paramref name="rideId"/>, and its camera may therefore
	/// be applied. A device that last had another ride open answers <c>false</c> and the map opens
	/// where it otherwise would.
	/// </summary>
	/// <param name="rideId">The ride being opened.</param>
	public bool IsFor(Guid rideId) => RideId == rideId;

	/// <summary>The camera this view reopens on.</summary>
	public MapCamera ToCamera() => new(Latitude, Longitude, ZoomLevel);

	/// <summary>
	/// Brings a view built from a live viewport — or read back from a device that once stored
	/// something else — into range: the zoom is clamped, and a centre that is not a real
	/// coordinate is refused.
	/// </summary>
	/// <returns>The usable view, or <c>null</c> when the centre is not a point on the earth.</returns>
	public LiveMapView? Normalised()
	{
		if (!double.IsFinite(Latitude) || !double.IsFinite(Longitude)
			|| Latitude is < -90 or > 90 || Longitude is < -180 or > 180)
		{
			return null;
		}

		return this with
		{
			ZoomLevel = Math.Clamp(
				double.IsFinite(ZoomLevel) ? ZoomLevel : MinZoomLevel,
				MinZoomLevel,
				MaxZoomLevel),
		};
	}

	/// <summary>
	/// The whole record as one string for <see cref="IDeviceSettings"/>, leading <c>1</c> being
	/// the format version — the same arrangement as <see cref="RouteStyle.Encode"/> and for the
	/// same reasons.
	/// <para>
	/// Five decimal places on the centre, which is about a metre: the same resolution positions
	/// travel at (<c>PositionScale.Factor</c>), and far finer than a map centre needs to be for
	/// the view to come back where it was left.
	/// </para>
	/// </summary>
	/// <exception cref="InvalidOperationException">The centre is not a point on the earth.</exception>
	public string Encode()
	{
		LiveMapView safe = Normalised()
			?? throw new InvalidOperationException("A live map view with no valid centre cannot be stored.");

		return string.Join('|', [
			"1",
			safe.RideId.ToString("N", CultureInfo.InvariantCulture),
			safe.Latitude.ToString("0.#####", CultureInfo.InvariantCulture),
			safe.Longitude.ToString("0.#####", CultureInfo.InvariantCulture),
			safe.ZoomLevel.ToString("0.##", CultureInfo.InvariantCulture),
			safe.FollowMe ? "1" : "0",
			safe.HeadingUp ? "1" : "0",
		]);
	}

	/// <summary>
	/// Reads back what <see cref="Encode"/> wrote.
	/// <para>
	/// All-or-nothing like <see cref="PrivateArea.Decode"/> rather than field-by-field like
	/// <see cref="RouteStyle.Decode"/>: half of a camera is a camera pointing somewhere nobody
	/// asked for, and the cost of answering <c>null</c> is that the map opens where it always
	/// did, which is no worse than the device having stored nothing.
	/// </para>
	/// <para>
	/// <strong>The field count is a floor, not an equality, and that is what let <see cref="HeadingUp"/>
	/// arrive without a version bump.</strong> A flag appended to the tail reads as its default on a
	/// build that has never heard of it, and a value written by an older build reads as its default
	/// here — so a rider moving between the two loses a preference at worst, rather than the whole
	/// camera. A field whose <em>meaning</em> changed would need the version, which is what it is for.
	/// </para>
	/// </summary>
	/// <param name="encoded">A string from <see cref="Encode"/>, or <c>null</c> on a device that has never stored one.</param>
	public static LiveMapView? Decode(string? encoded)
	{
		if (string.IsNullOrWhiteSpace(encoded))
		{
			return null;
		}

		string[] parts = encoded.Split('|');

		if (parts.Length < 6 || parts[0] != "1")
		{
			return null;
		}

		if (!Guid.TryParse(parts[1], out Guid rideId)
			|| !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude)
			|| !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude)
			|| !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom))
		{
			return null;
		}

		return new LiveMapView(
			rideId,
			latitude,
			longitude,
			zoom,
			FollowMe: parts[5] == "1",
			HeadingUp: parts.Length > 6 && parts[6] == "1").Normalised();
	}
}
