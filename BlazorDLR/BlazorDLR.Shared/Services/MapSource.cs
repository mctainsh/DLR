using System.Globalization;

namespace BlazorDLR.Shared.Services;

/// <summary>Where the base map's tiles come from (§4.5, §13 Q26).</summary>
public enum MapSourceKind
{
	/// <summary>OpenStreetMap's raster tiles. The default, and what every map drew before this type existed.</summary>
	Osm = 0,

	/// <summary>A PMTiles archive downloaded onto this device. Phone only (§18.6).</summary>
	Offline = 1,

	/// <summary>A rider-supplied XYZ raster template — a self-hosted tile server, or a service they hold a key for.</summary>
	Custom = 2,
}

/// <summary>
/// Which tiles go under the map on this device (§4.5).
/// <para>
/// <strong>A device preference, not ride data.</strong> Same posture as <see cref="RouteStyle"/>:
/// two riders on the same ride can hold different answers and neither is wrong, it is stored
/// through <see cref="IDeviceSettings"/>, and it never travels to the server. A phone in a dead
/// zone and a laptop planning next Sunday want genuinely different things under the same routes.
/// </para>
/// <para>
/// <strong>The base map is only tiles, camera, rotation and attribution.</strong> Every marker,
/// rider pin and route is drawn by the Skia overlay on top (§4.5 v0.21), so changing this changes
/// what is <em>under</em> the overlay and nothing else — no screen and no contract moves.
/// </para>
/// <para>
/// <strong>Attribution travels with the source, because the obligation does.</strong> It is not a
/// display preference: OSM's tiles are ODbL and the credit is a condition of using them, which is
/// why <see cref="MapProvider"/> is still an enum and why <see cref="Attribution"/> is required
/// rather than optional on a custom source. A rider who points this at a tile server is the one
/// who knows what that server requires, so they are asked.
/// </para>
/// </summary>
/// <param name="Kind">Which of the three.</param>
/// <param name="PackId">
/// For <see cref="MapSourceKind.Offline"/>, which downloaded archive. Null until the packs
/// themselves exist — see <see cref="Normalised"/>, which refuses an offline source without one
/// rather than leaving the map with nothing to draw.
/// </param>
/// <param name="UrlTemplate">
/// For <see cref="MapSourceKind.Custom"/>, an XYZ template carrying <c>{x}</c>, <c>{y}</c> and
/// <c>{z}</c>. <strong>HTTPS only</strong> — see <see cref="IsUsableTemplate"/>.
/// </param>
/// <param name="Attribution">
/// For <see cref="MapSourceKind.Custom"/>, the credit the tile server requires. Rendered by
/// MapLibre's own attribution control, exactly as OSM's is.
/// </param>
/// <param name="MaxZoom">
/// The deepest zoom the source has tiles for. Getting this wrong is visible: request past what a
/// server holds and the tiles 404, which reads on screen as the map dissolving at exactly the
/// zoom a marker is placed at (§16.1).
/// </param>
public sealed record MapSource(
	MapSourceKind Kind,
	string? PackId = null,
	string? UrlTemplate = null,
	string? Attribution = null,
	int MaxZoom = MapSource.OsmMaxZoom)
{
	/// <summary>
	/// The <see cref="IDeviceSettings"/> key. Namespaced like <c>dlr.route-style</c>, and
	/// versioned inside the value rather than in the key.
	/// </summary>
	public const string StorageKey = "dlr.map-source";

	/// <summary>
	/// OSM's raster tiles stop here. Requesting z20+ gets 404s — see <see cref="MaxZoom"/>, and
	/// the same number in <c>map.maplibre.js</c>, which is where it is enforced on the style.
	/// </summary>
	public const int OsmMaxZoom = 19;

	/// <summary>The shallowest a caller may claim a source goes. Below this there is no map.</summary>
	public const int MinAllowedZoom = 1;

	/// <summary>The deepest Web Mercator any of this app's surfaces addresses.</summary>
	public const int MaxAllowedZoom = 24;

	/// <summary>
	/// ODbL's credit, and it is mandatory and permanent (§4.5). Declared here and handed to the
	/// map's own attribution control, so removing it means removing the tiles.
	/// </summary>
	public const string OsmAttribution =
		"© <a href=\"https://www.openstreetmap.org/copyright\" target=\"_blank\" rel=\"noreferrer\">OpenStreetMap</a> contributors";

	/// <summary>
	/// What a device that has never chosen gets: OSM, which is what every map drew before this
	/// type existed. §13 Q26 still has to move that source before public announcement — this
	/// setting does not discharge it, it just stops OSM being the only possible answer.
	/// </summary>
	public static MapSource Default { get; } = new(MapSourceKind.Osm, MaxZoom: OsmMaxZoom);

	/// <summary>A custom raster source. Run it through <see cref="Normalised"/> before storing it.</summary>
	/// <param name="urlTemplate">An XYZ template carrying <c>{x}</c>, <c>{y}</c> and <c>{z}</c>.</param>
	/// <param name="attribution">What the tile server requires be credited.</param>
	/// <param name="maxZoom">The deepest zoom it holds tiles for.</param>
	public static MapSource Custom(string urlTemplate, string attribution, int maxZoom = OsmMaxZoom) =>
		new(MapSourceKind.Custom, null, urlTemplate, attribution, maxZoom);

	/// <summary>A downloaded archive.</summary>
	/// <param name="packId">Which pack.</param>
	public static MapSource OfflinePack(string packId) => new(MapSourceKind.Offline, packId);

	/// <summary>
	/// Whether a string is an XYZ template this app can actually load. Public so the settings
	/// screen can say what is wrong while the rider is still typing, rather than storing something
	/// that silently produces a blank map.
	/// <para>
	/// <strong>HTTPS is not a preference here.</strong> Every host serves the app over a secure
	/// scheme, so a plain-HTTP tile URL is mixed content and the WebView blocks it before iOS ATS
	/// or Android's cleartext policy get a say. A template that cannot possibly load is worth
	/// refusing at the point somebody types it.
	/// </para>
	/// </summary>
	/// <param name="template">The candidate.</param>
	public static bool IsUsableTemplate(string? template) =>
		!string.IsNullOrWhiteSpace(template)
		&& template.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
		&& template.Contains("{x}", StringComparison.Ordinal)
		&& template.Contains("{y}", StringComparison.Ordinal)
		&& template.Contains("{z}", StringComparison.Ordinal);

	/// <summary>
	/// The tile URL this source resolves to, or <c>null</c> for one that is not served over
	/// XYZ — an offline archive, which the map module reads through the <c>pmtiles://</c>
	/// protocol instead.
	/// </summary>
	public string? TileUrl => Kind switch
	{
		MapSourceKind.Osm => "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
		MapSourceKind.Custom => UrlTemplate,
		_ => null,
	};

	/// <summary>The credit that must be rendered for this source.</summary>
	public string AttributionText => Kind switch
	{
		MapSourceKind.Osm => OsmAttribution,
		MapSourceKind.Custom => Attribution ?? string.Empty,
		// An offline pack is an extract of the same data, so it carries the same obligation.
		_ => OsmAttribution,
	};

	/// <summary>Which provider this is, for the attribution obligation <see cref="MapProvider"/> exists to keep visible.</summary>
	public MapProvider Provider => Kind switch
	{
		MapSourceKind.Offline => MapProvider.Pmtiles,
		MapSourceKind.Custom => MapProvider.CustomRaster,
		_ => MapProvider.MapLibreOsm,
	};

	/// <summary>
	/// Brings a source into range, or refuses it outright.
	/// <para>
	/// All-or-nothing per kind, like <see cref="LiveMapView.Normalised"/> rather than field by
	/// field: half a tile source is a blank map, and the cost of answering <c>null</c> is that
	/// the caller falls back to <see cref="Default"/> — which is a working map, and no worse than
	/// a device that had stored nothing.
	/// </para>
	/// <para>
	/// Fields belonging to the other kinds are cleared rather than carried. A stored custom
	/// template that outlived a switch back to OSM would come back the next time somebody chose
	/// Custom, which reads as the app remembering something the rider did not ask it to.
	/// </para>
	/// </summary>
	/// <returns>The usable source, or <c>null</c> when this one cannot draw a map.</returns>
	public MapSource? Normalised()
	{
		int zoom = Math.Clamp(MaxZoom, MinAllowedZoom, MaxAllowedZoom);

		switch (Kind)
		{
			case MapSourceKind.Osm:
				return new MapSource(MapSourceKind.Osm, MaxZoom: OsmMaxZoom);

			case MapSourceKind.Offline:
				// A pack id is the whole of what an offline source is. Without one there is nothing
				// to open, and answering null here is what sends the caller back to OSM.
				return string.IsNullOrWhiteSpace(PackId)
					? null
					: new MapSource(MapSourceKind.Offline, PackId.Trim(), MaxZoom: zoom);

			case MapSourceKind.Custom:
				return IsUsableTemplate(UrlTemplate) && !string.IsNullOrWhiteSpace(Attribution)
					? new MapSource(MapSourceKind.Custom, null, UrlTemplate!.Trim(), Attribution.Trim(), zoom)
					: null;

			default:
				// A kind from a newer build. Same answer as a malformed value.
				return null;
		}
	}

	/// <summary>
	/// The record as one string for <see cref="IDeviceSettings"/>, leading <c>1</c> being the
	/// format version — the same arrangement as <see cref="LiveMapView.Encode"/>.
	/// <para>
	/// The template and the attribution are percent-encoded. A tile URL carries <c>&amp;</c> and
	/// braces as a matter of course and an attribution is free text with markup in it, and neither
	/// should be able to break the separator.
	/// </para>
	/// </summary>
	/// <exception cref="InvalidOperationException">This source cannot draw a map — see <see cref="Normalised"/>.</exception>
	public string Encode()
	{
		MapSource safe = Normalised()
			?? throw new InvalidOperationException("A map source that cannot draw a map cannot be stored.");

		return string.Join('|', [
			"1",
			safe.Kind.ToString().ToLowerInvariant(),
			Uri.EscapeDataString(safe.PackId ?? string.Empty),
			Uri.EscapeDataString(safe.UrlTemplate ?? string.Empty),
			Uri.EscapeDataString(safe.Attribution ?? string.Empty),
			safe.MaxZoom.ToString(CultureInfo.InvariantCulture),
		]);
	}

	/// <summary>
	/// Reads back what <see cref="Encode"/> wrote, or <c>null</c> for a device that has stored
	/// nothing, a format this build does not speak, or a value that no longer draws a map.
	/// </summary>
	/// <param name="encoded">A string from <see cref="Encode"/>.</param>
	public static MapSource? Decode(string? encoded)
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

		if (!Enum.TryParse(parts[1], ignoreCase: true, out MapSourceKind kind)
			|| !int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxZoom))
		{
			return null;
		}

		return new MapSource(
			kind,
			Blank(Uri.UnescapeDataString(parts[2])),
			Blank(Uri.UnescapeDataString(parts[3])),
			Blank(Uri.UnescapeDataString(parts[4])),
			maxZoom).Normalised();

		static string? Blank(string value) => string.IsNullOrEmpty(value) ? null : value;
	}
}
