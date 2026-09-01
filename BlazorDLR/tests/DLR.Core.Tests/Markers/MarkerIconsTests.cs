using DLR.Core.Markers;

namespace DLR.Core.Tests.Markers;

/// <summary>
/// §16.2's curated icon key set + GPX &lt;sym&gt; mapping. Three invariants:
/// <list type="bullet">
///   <item>An unknown-shaped-like-a-key icon is stored rather than flattened - the
///     forward-compat rule that lets a v-N+1 client's <c>ferry</c> round-trip through
///     a v-N server.</item>
///   <item>A foreign symbol (with spaces / capitals) that is not in the map falls back
///     to <c>note</c> - no attempt to guess.</item>
///   <item>The Known set contains every fallback the code documents.</item>
/// </list>
/// </summary>
public sealed class MarkerIconsTests
{
	[Fact]
	public void Known_ContainsFallbackAndCoreKeys()
	{
		MarkerIcons.Known.ShouldContain(MarkerIcons.Fallback);
		MarkerIcons.Known.ShouldContain("hazard");
		MarkerIcons.Known.ShouldContain("gravel");
		MarkerIcons.Known.ShouldContain("start");
		MarkerIcons.Known.ShouldContain("finish");
	}

	[Fact]
	public void IsKnown_MatchesTheCuratedSet()
	{
		MarkerIcons.IsKnown("hazard").ShouldBeTrue();
		MarkerIcons.IsKnown("ferry").ShouldBeFalse(
			"§16.2: 'ferry' is a v-N+1 icon and this version does not draw it - but ForSymbol still stores it.");
	}

	[Fact]
	public void ForSymbol_NullOrBlank_ReturnsFallback()
	{
		MarkerIcons.ForSymbol(null).ShouldBe("note");
		MarkerIcons.ForSymbol("").ShouldBe("note");
		MarkerIcons.ForSymbol("   ").ShouldBe("note");
	}

	[Fact]
	public void ForSymbol_MapsKnownGpxNames_ToCuratedKeys()
	{
		MarkerIcons.ForSymbol("Gas Station").ShouldBe("fuel", "GPX symbols are case-insensitive.");
		MarkerIcons.ForSymbol("restaurant").ShouldBe("food");
		MarkerIcons.ForSymbol("Flag, Green").ShouldBe("start");
		MarkerIcons.ForSymbol("Flag, Red").ShouldBe("finish");
		MarkerIcons.ForSymbol("Ford").ShouldBe("water-crossing");
	}

	[Fact]
	public void ForSymbol_UnknownButKeyShaped_IsStoredAsIs()
	{
		// A future icon key like 'ferry' or 'bakery' passes IsStorable - lowercase ASCII
		// letters plus hyphens, under 32 chars - and survives the mapping.
		MarkerIcons.ForSymbol("ferry").ShouldBe("ferry",
			"§16.2 forward-compat: a v-N+1 client's key is stored unchanged so it survives export/import through this version.");
		MarkerIcons.ForSymbol("bakery-hot").ShouldBe("bakery-hot");
	}

	[Fact]
	public void ForSymbol_UnknownAndForeignShaped_FallsBackToNote()
	{
		// A GPX symbol shaped like a human phrase (spaces, capitals) does not survive as
		// an icon key - the code correctly flattens it.
		MarkerIcons.ForSymbol("Scenic Overlook").ShouldBe("note");
		MarkerIcons.ForSymbol("Flag, Blue").ShouldBe("note",
			"foreign symbols not in the map and not key-shaped fall back rather than become junk keys.");
	}

	[Fact]
	public void ToGpxSymbol_UnknownKey_IsWrittenAsItself()
	{
		// A client that received a v-N+1 icon has to export it as itself so the file
		// carries the same key on the way out.
		MarkerIcons.ToGpxSymbol("ferry").ShouldBe("ferry",
			"§16.2: the exporter does not flatten a stored unknown to the fallback - that would be lossy on round trip.");
	}

	[Fact]
	public void ToGpxSymbol_KnownKey_UsesMappedName()
	{
		MarkerIcons.ToGpxSymbol("fuel").ShouldBe("fuel");
		MarkerIcons.ToGpxSymbol("water-crossing").ShouldBe("water-crossing");
	}

	[Theory]
	[InlineData("hazard", true)]
	[InlineData("water-crossing", true)]
	[InlineData("has-hyphens-and-numbers2", true)]
	[InlineData("Bad Casing", false)]
	[InlineData("with spaces", false)]
	[InlineData("with_underscore", false)]
	[InlineData(null, false)]
	[InlineData("", false)]
	[InlineData("   ", false)]
	public void IsStorable_EnforcesLowercaseAsciiPlusDigitsAndHyphen(string? icon, bool expected)
	{
		MarkerIcons.IsStorable(icon).ShouldBe(expected);
	}

	[Fact]
	public void IsStorable_EnforcesMaxLength()
	{
		string tooLong = new('a', MarkerIcons.MaxLength + 1);
		MarkerIcons.IsStorable(tooLong).ShouldBeFalse(
			$"§16.7: keys longer than {MarkerIcons.MaxLength} chars do not fit the column.");
	}
}
