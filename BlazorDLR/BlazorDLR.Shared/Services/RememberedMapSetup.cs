using System.Globalization;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// What the rider last typed on the Maps screen (§4.5, §18.6) — the tile server's details and the
/// map pack's link — so coming back to it does not mean typing a long URL again.
/// <para>
/// <strong>Deliberately separate from <see cref="MapSource"/>.</strong> That one is what the map
/// is <em>using</em>, and it only ever holds a source that works: <c>MapSource.Normalised</c>
/// refuses a half-typed template outright. This is the opposite — it is whatever is in the boxes,
/// finished or not. A rider who got as far as <c>https://tiles.example.com/{z}/{x}</c> and was
/// interrupted is exactly who this is for, and validating on the way in would throw away the one
/// value worth keeping.
/// </para>
/// <para>
/// <strong>Both halves of the screen in one record.</strong> They are written together, read
/// together, and neither is meaningful anywhere else; two keys would be two encodings and two
/// reads for one screen's memory.
/// </para>
/// <para>
/// Device-local and hand-encoded like <see cref="LiveMapView"/> and <see cref="RouteStyle"/>. It
/// never travels to the server — a tile server somebody uses is their business, and the pack link
/// may be a private host.
/// </para>
/// </summary>
/// <param name="TileTemplate">The custom XYZ template, as typed.</param>
/// <param name="TileAttribution">The credit that goes with it, as typed.</param>
/// <param name="TileMaxZoom">The deepest zoom claimed for it.</param>
/// <param name="PackUrl">The last map-pack link, kept after a download so the next region is an edit rather than a retype.</param>
/// <param name="PackName">What that pack was called on this device.</param>
public sealed record RememberedMapSetup(
	string? TileTemplate = null,
	string? TileAttribution = null,
	int TileMaxZoom = MapSource.OsmMaxZoom,
	string? PackUrl = null,
	string? PackName = null)
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
		&& string.IsNullOrWhiteSpace(PackUrl)
		&& string.IsNullOrWhiteSpace(PackName);

	/// <summary>
	/// The record as one string, leading <c>1</c> being the format version — the same arrangement as
	/// <see cref="LiveMapView.Encode"/>. Every field is percent-encoded: a tile URL carries
	/// <c>&amp;</c> and braces as a matter of course, and an attribution is free text.
	/// </summary>
	public string Encode() => string.Join('|', [
		"1",
		Field(TileTemplate),
		Field(TileAttribution),
		Math.Clamp(TileMaxZoom, MapSource.MinAllowedZoom, MapSource.MaxAllowedZoom)
			.ToString(CultureInfo.InvariantCulture),
		Field(PackUrl),
		Field(PackName),
	]);

	/// <summary>
	/// Reads back what <see cref="Encode"/> wrote, or <c>null</c> for a device that has stored
	/// nothing or a format this build does not speak.
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

		if (parts.Length < 6 || parts[0] != "1")
		{
			return null;
		}

		if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxZoom))
		{
			maxZoom = MapSource.OsmMaxZoom;
		}

		return new RememberedMapSetup(
			Blank(parts[1]),
			Blank(parts[2]),
			Math.Clamp(maxZoom, MapSource.MinAllowedZoom, MapSource.MaxAllowedZoom),
			Blank(parts[4]),
			Blank(parts[5]));

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
