using DLR.Core.Display;

namespace DLR.Core.Tests.Display;

/// <summary>
/// The rider-marker colour rules (§16.3): what counts as a colour, what an account that never
/// chose gets, and which of black or white is drawn on top.
/// <para>
/// Tested here rather than through the map, because the map cannot be tested - <c>SkiaMapOverlay</c>
/// is browser-only. The half of "does the marker read" that is arithmetic lives in these two types,
/// and this is the file that pins it.
/// </para>
/// </summary>
public sealed class MarkerColourTests
{
	[Theory]
	[InlineData("#ffffff")]
	[InlineData("#000000")]
	[InlineData("#2563EB")]
	public void Hex_SixDigitsBehindAHash_IsAColour(string colour) =>
		HexColour.IsHex(colour).ShouldBeTrue();

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("#fff")]
	[InlineData("2563eb")]
	[InlineData("#2563ebff")]
	[InlineData("#gggggg")]
	[InlineData("red")]
	public void Hex_AnythingElse_IsNot(string? colour) =>
		HexColour.IsHex(colour).ShouldBeFalse(
			"#rrggbb is the only form the Skia overlay parses; accepting more here would mean a " +
			"value that saves and then silently draws as somebody's fallback.");

	[Fact]
	public void Hex_Normalise_LowerCasesAColourAndFallsBackOtherwise()
	{
		HexColour.Normalise("#2563EB", "#000000").ShouldBe("#2563eb");
		HexColour.Normalise("nonsense", "#000000").ShouldBe("#000000");
	}

	/// <summary>
	/// The pairing rule the whole feature rests on: the rider picks a background and the app picks
	/// the ink, so no choice can produce a marker nobody can read.
	/// </summary>
	[Theory]
	[InlineData("#ffffff", "#000000")]
	[InlineData("#facc15", "#000000")] // yellow - bright, wants black
	[InlineData("#84cc16", "#000000")] // lime
	[InlineData("#111827", "#ffffff")] // near-black
	[InlineData("#2563eb", "#ffffff")] // the accent blue
	[InlineData("#dc2626", "#ffffff")] // red
	public void Foreground_IsWhicheverOfBlackOrWhiteReads(string background, string expected) =>
		HexColour.ContrastingForeground(background).ShouldBe(expected);

	/// <summary>
	/// Luminance is gamma-corrected, not a channel average. Pure green and pure blue have the same
	/// average and wildly different brightness - an average would give one of them the wrong ink.
	/// </summary>
	[Fact]
	public void Foreground_GreenAndBlue_AreNotTreatedAsEquallyBright()
	{
		HexColour.RelativeLuminance("#00ff00").ShouldBeGreaterThan(HexColour.RelativeLuminance("#0000ff"));

		HexColour.ContrastingForeground("#00ff00").ShouldBe("#000000");
		HexColour.ContrastingForeground("#0000ff").ShouldBe("#ffffff");
	}

	[Fact]
	public void Foreground_UnreadableString_IsInkedAsIfItWereWhite() =>
		HexColour.ContrastingForeground("not a colour").ShouldBe("#000000",
			"a surface nobody could measure gets the ink that is safe over the default one.");

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void MarkerColour_BlankIsValidAndMeansNoChoice(string? colour)
	{
		MarkerColours.TryNormalise(colour, out string? normalised).ShouldBeTrue(
			"blank is how a traveller goes back to the default - refusing it would make the setting one-way.");

		normalised.ShouldBeNull();
	}

	[Fact]
	public void MarkerColour_AColour_IsAcceptedLowerCased()
	{
		MarkerColours.TryNormalise("#2563EB", out string? normalised).ShouldBeTrue();

		normalised.ShouldBe("#2563eb");
	}

	[Fact]
	public void MarkerColour_Rubbish_IsRefusedRatherThanDefaulted()
	{
		MarkerColours.TryNormalise("chartreuse", out string? normalised).ShouldBeFalse(
			"a client bug is found by whoever wrote it, not by a traveller wondering why their colour did not stick.");

		normalised.ShouldBeNull();
	}

	[Fact]
	public void MarkerColour_NothingStored_DrawsTheDefault()
	{
		MarkerColours.Or(null).ShouldBe(MarkerColours.Default);
		MarkerColours.Or("nonsense").ShouldBe(MarkerColours.Default,
			"an account holding something unreadable looks like one that never chose, not differently broken.");
	}

	/// <summary>
	/// The default is not the accent blue on purpose: the arrow that says which way a rider is
	/// heading is drawn in it, and a blue arrow on a blue label is one shape rather than two.
	/// </summary>
	[Fact]
	public void MarkerColour_Default_IsNotTheArrowColour() =>
		MarkerColours.Default.ShouldNotBe("#2563eb");

	[Fact]
	public void MarkerColour_EveryPaletteEntry_IsAColour() =>
		MarkerColours.Palette.ShouldAllBe(colour => HexColour.IsHex(colour));

	[Fact]
	public void MarkerColour_PaletteEntriesAreDistinct() =>
		MarkerColours.Palette.Distinct(StringComparer.Ordinal).Count()
			.ShouldBe(MarkerColours.Palette.Count,
				"two swatches drawing the same marker is a choice that is not a choice.");

	[Fact]
	public void MarkerColour_PaletteContainsTheDefault() =>
		MarkerColours.Palette.ShouldContain(MarkerColours.Default,
			"the swatch row has to be able to show what is currently in force, including for an " +
			"account that never chose.");
}
