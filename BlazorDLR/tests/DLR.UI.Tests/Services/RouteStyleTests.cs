using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Services;

/// <summary>
/// The device-local route style (§18.6). Two things are worth a test here and neither is the
/// record's own property bag:
/// <list type="bullet">
///   <item><see cref="RouteStyle.Encode"/> / <see cref="RouteStyle.Decode"/> is a round trip
///     through a string on somebody's phone that a later build has to read back. Every
///     malformed shape it can meet is a shape a real device can hand it.</item>
///   <item><see cref="RouteStyle.Normalised"/> is the only thing standing between an
///     <c>&lt;input&gt;</c> and the Skia canvas, which silently falls back to blue on a colour
///     it cannot parse - a setting that saves and then does nothing is worse than one that
///     refuses.</item>
/// </list>
/// </summary>
public sealed class RouteStyleTests
{
	[Fact]
	public void Encode_ThenDecode_RoundTripsEveryField()
	{
		RouteStyle style = new(
			FillColour: "#ff8800",
			OutlineColour: "#101820",
			LineWidthPx: 7,
			ShowDirectionArrows: false,
			ArrowColour: "#00ffcc");

		RouteStyle read = RouteStyle.Decode(style.Encode());

		read.ShouldBe(style, "a device that stored a style must read back the same one.");
	}

	[Fact]
	public void Encode_ThenDecode_KeepsThePerRoutePaletteDistinctFromAColour()
	{
		// The one field that is legitimately absent. "No override" and "black" are different
		// answers and the encoding must not conflate them - a ride with three routes drawn in
		// one colour is unreadable against its own list.
		RouteStyle read = RouteStyle.Decode(RouteStyle.Default.Encode());

		read.FillColour.ShouldBeNull();
		read.ShouldBe(RouteStyle.Default);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("nonsense")]
	[InlineData("2|#ffffff|#000000|4|1|#ffffff")] // a format version this build does not know
	[InlineData("1|#ffffff|#000000|4")]           // truncated
	public void Decode_AnythingItCannotTrust_FallsBackToTheDefaults(string? stored)
	{
		RouteStyle.Decode(stored).ShouldBe(RouteStyle.Default,
			"the value comes off a device we do not control; the failure mode is 'looks stock'.");
	}

	[Fact]
	public void Decode_OneCorruptedField_KeepsTheOthers()
	{
		// Field-by-field fallback, deliberately: losing the width because a colour got mangled
		// would turn one bad byte into "all your settings are gone".
		RouteStyle read = RouteStyle.Decode("1||not-a-colour|9|0|#00ffcc");

		read.OutlineColour.ShouldBe(RouteStyle.Default.OutlineColour);
		read.LineWidthPx.ShouldBe(9);
		read.ShowDirectionArrows.ShouldBeFalse();
		read.ArrowColour.ShouldBe("#00ffcc");
	}

	[Theory]
	[InlineData(0, RouteStyle.MinLineWidthPx)]
	[InlineData(-4, RouteStyle.MinLineWidthPx)]
	[InlineData(400, RouteStyle.MaxLineWidthPx)]
	[InlineData(double.NaN, 4)]
	public void Normalised_ClampsTheWidth_RatherThanRejectingIt(double given, double expected)
	{
		// Clamped rather than thrown: every caller is either a slider that cannot usefully
		// report a validation error, or a read of what an earlier version wrote.
		(RouteStyle.Default with { LineWidthPx = given }).Normalised().LineWidthPx.ShouldBe(expected);
	}

	[Theory]
	[InlineData("#ABCDEF", "#abcdef")]
	[InlineData("#abcdef", "#abcdef")]
	public void NormaliseColour_AcceptsSixDigitHex_AndLowerCasesIt(string given, string expected) =>
		RouteStyle.NormaliseColour(given, "#000000").ShouldBe(expected);

	[Theory]
	[InlineData("")]
	[InlineData("red")]
	[InlineData("#abc")]          // three-digit CSS shorthand the overlay's parser rejects
	[InlineData("#abcdefff")]     // eight-digit with alpha, ditto
	[InlineData("abcdef")]        // no hash
	[InlineData("#gggggg")]
	[InlineData(null)]
	public void NormaliseColour_RejectsAnythingTheOverlayCannotParse(string? given) =>
		RouteStyle.NormaliseColour(given, "#123456").ShouldBe("#123456",
			"a colour the canvas silently redraws as its fallback blue must never be stored.");

	[Fact]
	public void EffectiveFill_PrefersTheOverride_AndOtherwiseLeavesThePaletteAlone()
	{
		RouteStyle.Default.EffectiveFill("#16a34a").ShouldBe("#16a34a",
			"§5.4 assigns a colour per route by position; the default must not flatten that.");

		(RouteStyle.Default with { FillColour = "#ff8800" }).EffectiveFill("#16a34a").ShouldBe("#ff8800");
	}
}
