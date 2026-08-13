using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Services;

/// <summary>
/// What the Maps screen remembers being typed into it (§4.5, §18.6).
/// <para>
/// The property that matters is the one that separates this from <see cref="MapSource"/>: it keeps
/// what was typed <em>whether or not it works</em>. A rider interrupted halfway through a tile URL
/// is exactly who this is for, and validating on the way in would throw away the only value worth
/// keeping.
/// </para>
/// </summary>
public sealed class RememberedMapSetupTests
{
	[Fact]
	public void BothHalvesOfTheScreenRoundTrip()
	{
		RememberedMapSetup setup = new(
			TileTemplate: "https://tiles.example.com/{z}/{x}/{y}.png?key=abc&v=2",
			TileAttribution: "© Example | Maps",
			TileMaxZoom: 17,
			PackUrl: "https://www.noptic1.com/Stuff/sydney.pmtiles",
			PackName: "sydney");

		RememberedMapSetup? decoded = RememberedMapSetup.Decode(setup.Encode());

		decoded.ShouldBe(setup,
			"query strings, braces and the separator itself all appear in real tile URLs.");
	}

	[Fact]
	public void AHalfTypedTemplateIsKept()
	{
		// MapSource would refuse this outright — that is the whole difference between the two.
		RememberedMapSetup setup = new(TileTemplate: "https://tiles.example.com/{z}/{x}");

		RememberedMapSetup? decoded = RememberedMapSetup.Decode(setup.Encode());

		decoded.ShouldNotBeNull();
		decoded.TileTemplate.ShouldBe("https://tiles.example.com/{z}/{x}",
			"somebody interrupted mid-URL is the person this exists for.");
	}

	[Fact]
	public void OnlyOneHalfFilledInIsStillWorthKeeping()
	{
		RememberedMapSetup setup = new(PackUrl: "https://example.com/sydney.pmtiles", PackName: "sydney");

		setup.IsEmpty.ShouldBeFalse();

		RememberedMapSetup? decoded = RememberedMapSetup.Decode(setup.Encode());

		decoded.ShouldNotBeNull();
		decoded.PackUrl.ShouldBe("https://example.com/sydney.pmtiles");
		decoded.TileTemplate.ShouldBeNull("a field nobody filled in comes back as nothing, not as an empty string.");
	}

	[Fact]
	public void AnUntouchedFormIsEmpty()
	{
		RememberedMapSetup.Empty.IsEmpty.ShouldBeTrue(
			"and the page removes the key rather than storing five blanks.");

		// Whitespace is not content — a field somebody tabbed through does not make a draft.
		new RememberedMapSetup(TileTemplate: "   ", PackName: "\t").IsEmpty.ShouldBeTrue();
	}

	[Fact]
	public void MaxZoomIsClampedBothWays()
	{
		RememberedMapSetup.Decode(new RememberedMapSetup(TileTemplate: "x", TileMaxZoom: 99).Encode())!
			.TileMaxZoom.ShouldBe(MapSource.MaxAllowedZoom);

		RememberedMapSetup.Decode(new RememberedMapSetup(TileTemplate: "x", TileMaxZoom: -3).Encode())!
			.TileMaxZoom.ShouldBe(MapSource.MinAllowedZoom);
	}

	[Fact]
	public void AnAbsurdlyLongFieldIsTruncatedRatherThanStored()
	{
		string huge = new('a', RememberedMapSetup.MaxFieldLength * 2);

		RememberedMapSetup? decoded = RememberedMapSetup.Decode(new RememberedMapSetup(TileTemplate: huge).Encode());

		decoded.ShouldNotBeNull();
		decoded.TileTemplate!.Length.ShouldBe(RememberedMapSetup.MaxFieldLength,
			"a value that long is not something somebody typed, and the device store is not where to find that out.");
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("garbage")]
	[InlineData("2|a|b|19|c|d")]   // a version this build cannot read
	[InlineData("1|a|b")]          // truncated
	public void AStoredValueThisBuildCannotRead_IsNoDraft(string? stored)
	{
		RememberedMapSetup.Decode(stored).ShouldBeNull(
			"the cost of answering null here is some retyping, which is the cheapest failure on this screen.");
	}

	[Fact]
	public void AnUnreadableZoomFallsBackWithoutLosingTheRest()
	{
		// The one field where a bad value should not cost the rider their URL.
		RememberedMapSetup? decoded = RememberedMapSetup.Decode("1|https%3A%2F%2Fx%2F%7Bz%7D||not-a-number||");

		decoded.ShouldNotBeNull();
		decoded.TileMaxZoom.ShouldBe(MapSource.OsmMaxZoom);
		decoded.TileTemplate.ShouldBe("https://x/{z}");
	}
}
