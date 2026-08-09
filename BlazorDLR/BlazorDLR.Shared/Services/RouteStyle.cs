using System.Globalization;
using DLR.Core.Display;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// How planned routes (§5.4) and imported GPX tracks are drawn on the map: the casing under
/// the line, the line itself, how thick it is, and whether chevrons along it say which way
/// round the route goes.
/// <para>
/// <strong>A device preference, not ride data.</strong> Two riders on the same ride can hold
/// different answers and neither is wrong — this is legibility on the screen in front of you,
/// which depends on the phone, the mount, the sunlight and the eyes. It is stored through
/// <see cref="IDeviceSettings"/>, never sent to the server, and never shared with the ride.
/// </para>
/// <para>
/// <strong><see cref="FillColour"/> is nullable and that is the interesting part.</strong>
/// <c>null</c> means "leave the per-route colour alone", which is what <see cref="RoutePalette"/>
/// assigns by position so a ride's second route is a different colour from its first. Setting it
/// paints every route the same, which is what somebody with one route and a strong opinion
/// actually wants — and is a real loss on a ride with three, so it is off by default.
/// </para>
/// </summary>
/// <param name="FillColour">The line's own colour as <c>#rrggbb</c>, or <c>null</c> to keep the per-route palette.</param>
/// <param name="OutlineColour">The casing drawn under and slightly wider than the line, as <c>#rrggbb</c>.</param>
/// <param name="LineWidthPx">Line thickness in canvas pixels. Clamped to [<see cref="MinLineWidthPx"/>, <see cref="MaxLineWidthPx"/>].</param>
/// <param name="ShowDirectionArrows">Whether chevrons are drawn along the line in its direction of travel.</param>
/// <param name="ArrowColour">The chevrons' colour as <c>#rrggbb</c>. Ignored when <paramref name="ShowDirectionArrows"/> is false.</param>
public sealed record RouteStyle(
	string? FillColour,
	string OutlineColour,
	double LineWidthPx,
	bool ShowDirectionArrows,
	string ArrowColour)
{
	/// <summary>Thinnest line the slider offers. Below this the casing swallows the line.</summary>
	public const double MinLineWidthPx = 1;

	/// <summary>Thickest line the slider offers. Above this the line hides the road under it.</summary>
	public const double MaxLineWidthPx = 12;

	/// <summary>
	/// What a device that has never been configured draws.
	/// <para>
	/// The width and the per-route palette are what the overlay drew before this record
	/// existed, so an untouched device looks unchanged in those respects. The dark casing and
	/// the white chevrons are new: a bare 4 px line over OSM tiles that run from near-white
	/// sand to dark forest is legible over about half of them, and a route with no arrows does
	/// not say which end is the start.
	/// </para>
	/// </summary>
	public static RouteStyle Default { get; } = new(
		FillColour: null,
		OutlineColour: "#1f2937",
		LineWidthPx: 4,
		ShowDirectionArrows: true,
		ArrowColour: "#ffffff");

	/// <summary>
	/// The colour a route at a given place in the ride's list is actually drawn in: the
	/// override when there is one, otherwise whatever the palette assigned.
	/// <para>
	/// Both the map overlay and the route list on the info page ask this, so the swatch beside
	/// a route's name never claims a colour the line is not drawn in.
	/// </para>
	/// </summary>
	/// <param name="paletteColour">The per-route colour, normally from <see cref="RoutePalette.At"/>.</param>
	public string EffectiveFill(string paletteColour) => FillColour ?? paletteColour;

	/// <summary>
	/// Brings a record built from user input (or read back from a device that once stored
	/// something else) into range: colours that are not <c>#rrggbb</c> fall back to the
	/// default's, and the width is clamped rather than rejected.
	/// <para>
	/// Clamping rather than throwing, because every caller is either a UI control that cannot
	/// usefully report a validation error or a read of a value some earlier version wrote.
	/// </para>
	/// </summary>
	public RouteStyle Normalised() => new(
		FillColour: FillColour is null ? null : NormaliseColour(FillColour, Default.OutlineColour),
		OutlineColour: NormaliseColour(OutlineColour, Default.OutlineColour),
		LineWidthPx: Math.Clamp(double.IsFinite(LineWidthPx) ? LineWidthPx : Default.LineWidthPx, MinLineWidthPx, MaxLineWidthPx),
		ShowDirectionArrows: ShowDirectionArrows,
		ArrowColour: NormaliseColour(ArrowColour, Default.ArrowColour));

	/// <summary>
	/// The whole record as one string for <see cref="IDeviceSettings"/>.
	/// <para>
	/// One key rather than five, so reading the style is one round trip to <c>localStorage</c>
	/// instead of five, and so a device can never hold three of the five fields from one
	/// version and two from another. Hand-rolled rather than JSON: the shared project is
	/// trimmed into a WASM download, and a reflection-based serialiser for five scalars is a
	/// trimming footgun bought for nothing.
	/// </para>
	/// <para>
	/// Leading <c>1</c> is the format version. A future field appends; a reader that does not
	/// know the version keeps its defaults rather than guessing at fields it cannot place.
	/// </para>
	/// </summary>
	public string Encode()
	{
		RouteStyle safe = Normalised();

		// Empty fill field means "no override" — the one value that is legitimately absent.
		return string.Join('|', [
			"1",
			safe.FillColour ?? "",
			safe.OutlineColour,
			safe.LineWidthPx.ToString("0.##", CultureInfo.InvariantCulture),
			safe.ShowDirectionArrows ? "1" : "0",
			safe.ArrowColour,
		]);
	}

	/// <summary>
	/// Reads back what <see cref="Encode"/> wrote.
	/// <para>
	/// Field by field, each falling back to <see cref="Default"/> on its own. A single
	/// corrupted colour costs that colour, not the width and the arrows with it — the value
	/// comes off a device we do not control and the failure mode should be "one thing looks
	/// stock", not "all your settings are gone".
	/// </para>
	/// </summary>
	/// <param name="encoded">A string from <see cref="Encode"/>, or <c>null</c> for a device that has never stored one.</param>
	/// <returns>The stored style, or <see cref="Default"/> when nothing usable was stored.</returns>
	public static RouteStyle Decode(string? encoded)
	{
		if (string.IsNullOrWhiteSpace(encoded))
		{
			return Default;
		}

		string[] parts = encoded.Split('|');

		// Version first. Anything else was written by a version that structured the fields
		// differently, and guessing at them is how a purple line ends up 40 px wide.
		if (parts.Length < 6 || parts[0] != "1")
		{
			return Default;
		}

		double width = double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
			? parsed
			: Default.LineWidthPx;

		return new RouteStyle(
			FillColour: parts[1].Length == 0 ? null : NormaliseColour(parts[1], Default.OutlineColour),
			OutlineColour: NormaliseColour(parts[2], Default.OutlineColour),
			LineWidthPx: width,
			ShowDirectionArrows: parts[4] != "0",
			ArrowColour: NormaliseColour(parts[5], Default.ArrowColour)).Normalised();
	}

	/// <summary>
	/// Lower-cases a <c>#rrggbb</c> colour, or answers <paramref name="fallback"/> when the
	/// string is not one.
	/// <para>
	/// <c>#rrggbb</c> exactly, because that is the only form the Skia overlay's own parser
	/// accepts and the only form an <c>&lt;input type="color"&gt;</c> ever produces. Accepting
	/// more here would mean a value that saves, persists, and then silently draws as the
	/// overlay's fallback blue.
	/// </para>
	/// </summary>
	/// <param name="colour">The candidate colour.</param>
	/// <param name="fallback">What to answer when it is not a six-digit hex colour.</param>
	public static string NormaliseColour(string? colour, string fallback) =>
		HexColour.Normalise(colour, fallback);
}
