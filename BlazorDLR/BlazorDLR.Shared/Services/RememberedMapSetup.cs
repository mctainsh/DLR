using System.Globalization;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// What the rider last did on the Maps screen (§4.5, §18.6) — the tile server's details, and where
/// they left the preview map — so coming back to it does not mean typing a long URL again, or
/// panning back to ground they were already looking at.
/// <para>
/// <strong>Deliberately separate from <see cref="MapSource"/>.</strong> That one is what the map
/// is <em>using</em>, and it only ever holds a source that works: <c>MapSource.Normalised</c>
/// refuses a half-typed template outright. This is the opposite — it is whatever is in the boxes,
/// finished or not. A rider who got as far as <c>https://tiles.example.com/{z}/{x}</c> and was
/// interrupted is exactly who this is for, and validating on the way in would throw away the one
/// value worth keeping.
/// </para>
/// <para>
/// <strong>It used to carry a map-pack link and a name too.</strong> Those went with the form that
/// asked for them: packs come from the catalogue now (§4.2), which supplies both, so there is
/// nothing left on that half of the screen to remember. Version 1 values are still read — the two
/// trailing fields are ignored — because throwing a rider's tile URL away over a format change they
/// did not ask for would be the one avoidable loss here.
/// </para>
/// <para>
/// Device-local and hand-encoded like <see cref="LiveMapView"/> and <see cref="RouteStyle"/>. It
/// never travels to the server — a tile server somebody uses is their business.
/// </para>
/// </summary>
/// <param name="TileTemplate">The custom XYZ template, as typed.</param>
/// <param name="TileAttribution">The credit that goes with it, as typed.</param>
/// <param name="TileMaxZoom">The deepest zoom claimed for it.</param>
/// <param name="PreviewCamera">
/// Where the preview map at the foot of the screen was last left, or <c>null</c> on a device that
/// has not moved it.
/// <para>
/// Kept for the same reason as <see cref="LiveMapView"/>, on a smaller scale: the preview exists to
/// be judged against ground the rider knows, and finding that ground is a pan and a pinch they
/// should do once rather than on every visit.
/// </para>
/// <para>
/// Until they do, the page borrows <see cref="LiveMapView"/>'s camera — the ground they were last
/// riding over is ground they know, which is the whole test a preview has to pass, and it is
/// already on the device. The world is the fallback behind that, for a device that has never opened
/// a live ride either: a tile server is a thing you go looking for a place in, and starting zoomed
/// into one city would say the screen has an opinion about where you ride.
/// </para>
/// <para>
/// No heading: the preview refuses rotation (<c>AllowRotation="false"</c>), so there is never one
/// to keep.
/// </para>
/// </param>
public sealed record RememberedMapSetup(
	string? TileTemplate = null,
	string? TileAttribution = null,
	int TileMaxZoom = MapSource.OsmMaxZoom,
	MapCamera? PreviewCamera = null)
{
	/// <summary>
	/// The <see cref="IDeviceSettings"/> key. Namespaced like <c>dlr.map-source</c> beside it, with
	/// the format version inside the value rather than in the key.
	/// </summary>
	public const string StorageKey = "dlr.map-setup";

	/// <summary>
	/// Longest string kept per field. A tile URL runs to a couple of hundred characters with a key
	/// on it; anything past this is not a value somebody typed, and the device store is not the
	/// place to find that out.
	/// </summary>
	public const int MaxFieldLength = 2048;

	/// <summary>A device that has typed nothing yet.</summary>
	public static RememberedMapSetup Empty { get; } = new();

	/// <summary>Whether there is anything worth storing. An empty draft is removed rather than written.</summary>
	public bool IsEmpty =>
		string.IsNullOrWhiteSpace(TileTemplate)
		&& string.IsNullOrWhiteSpace(TileAttribution)
		&& PreviewCamera is null;

	/// <summary>
	/// A preview camera brought into range: the zoom clamped to what the base maps offer, the heading
	/// dropped, and a centre that is not a point on the earth refused outright.
	/// <para>
	/// All-or-nothing on the centre like <see cref="LiveMapView.Normalised"/>, and for the same
	/// reason — half a camera is a camera pointing somewhere nobody asked for. The cost of answering
	/// <c>null</c> is that the preview opens on the world, which is where it opens anyway.
	/// </para>
	/// </summary>
	/// <param name="camera">The camera to check, or <c>null</c>.</param>
	public static MapCamera? NormalisedCamera(MapCamera? camera)
	{
		if (camera is not { } view
			|| !double.IsFinite(view.Latitude) || !double.IsFinite(view.Longitude)
			|| view.Latitude is < -90 or > 90 || view.Longitude is < -180 or > 180)
		{
			return null;
		}

		return view with
		{
			ZoomLevel = Math.Clamp(
				double.IsFinite(view.ZoomLevel) ? view.ZoomLevel : LiveMapView.MinZoomLevel,
				LiveMapView.MinZoomLevel,
				LiveMapView.MaxZoomLevel),
			HeadingDeg = 0,
		};
	}

	/// <summary>
	/// The record as one string, leading <c>2</c> being the format version — the same arrangement as
	/// <see cref="LiveMapView.Encode"/>. The three tile fields are percent-encoded: a tile URL carries
	/// <c>&amp;</c> and braces as a matter of course, and an attribution is free text.
	/// <para>
	/// The preview camera is three more fields on the tail, and <em>not</em> a version bump — the same
	/// move <see cref="LiveMapView.HeadingUp"/> made and for the same reason. A build that has never
	/// heard of them reads the four it knows and ignores the rest; a value written before they existed
	/// reads back here as a device that has not moved the preview. Either way the tile server survives,
	/// which is the one field on this screen worth not losing.
	/// </para>
	/// <para>
	/// Five decimal places on the centre — about a metre, the resolution positions travel at and far
	/// finer than a map centre needs to come back where it was left. Blank when there is no camera:
	/// three empty fields rather than a shorter string, so the count says which is which.
	/// </para>
	/// </summary>
	public string Encode()
	{
		MapCamera? preview = NormalisedCamera(PreviewCamera);

		return string.Join('|', [
			"2",
			Field(TileTemplate),
			Field(TileAttribution),
			Math.Clamp(TileMaxZoom, MapSource.MinAllowedZoom, MapSource.MaxAllowedZoom)
				.ToString(CultureInfo.InvariantCulture),
			preview is null ? string.Empty : preview.Latitude.ToString("0.#####", CultureInfo.InvariantCulture),
			preview is null ? string.Empty : preview.Longitude.ToString("0.#####", CultureInfo.InvariantCulture),
			preview is null ? string.Empty : preview.ZoomLevel.ToString("0.##", CultureInfo.InvariantCulture),
		]);
	}

	/// <summary>
	/// Reads back what <see cref="Encode"/> wrote, or <c>null</c> for a device that has stored
	/// nothing or a format this build does not speak.
	/// <para>
	/// Version 1 — which carried a map-pack link and name after the zoom — reads as far as the zoom
	/// and drops the rest. Every encoding agrees on its first four fields precisely so that this is a
	/// length check rather than a migration.
	/// <para>
	/// That is also why the preview camera is read from seven fields and not five: version 1 ran to
	/// six, so a device holding one of those cannot have its pack link mistaken for a latitude.
	/// </para>
	/// </para>
	/// <para>
	/// Unlike the other device records, a value that fails to decode costs the rider only some
	/// retyping — so this is the one place where answering <c>null</c> is genuinely cheap.
	/// </para>
	/// </summary>
	/// <param name="encoded">A string from <see cref="Encode"/>.</param>
	public static RememberedMapSetup? Decode(string? encoded)
	{
		if (string.IsNullOrWhiteSpace(encoded))
		{
			return null;
		}

		string[] parts = encoded.Split('|');

		if (parts.Length < 4 || parts[0] is not ("1" or "2"))
		{
			return null;
		}

		if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxZoom))
		{
			maxZoom = MapSource.OsmMaxZoom;
		}

		// All three or none: two thirds of a camera is not one, and the preview opening on the world
		// is what a device that stored nothing gets anyway.
		MapCamera? preview = null;

		if (parts.Length >= 7
			&& double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude)
			&& double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude)
			&& double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom))
		{
			preview = NormalisedCamera(new MapCamera(latitude, longitude, zoom));
		}

		return new RememberedMapSetup(
			Blank(parts[1]),
			Blank(parts[2]),
			Math.Clamp(maxZoom, MapSource.MinAllowedZoom, MapSource.MaxAllowedZoom),
			preview);

		static string? Blank(string value)
		{
			string decoded = Uri.UnescapeDataString(value);
			return string.IsNullOrEmpty(decoded) ? null : decoded;
		}
	}

	private static string Field(string? value) =>
		string.IsNullOrEmpty(value)
			? string.Empty
			: Uri.EscapeDataString(value.Length > MaxFieldLength ? value[..MaxFieldLength] : value);
}
