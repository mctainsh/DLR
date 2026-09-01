using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Services;

/// <summary>
/// The tile-source setting (§4.5). Two properties carry the weight here.
/// <para>
/// <strong>A source that cannot draw a map is refused rather than stored.</strong> The failure
/// this prevents is a rider who typed most of a URL, came back a week later and found a blank
/// grey rectangle with no way to tell whether the app, the network or their typing was at fault.
/// </para>
/// <para>
/// <strong>Attribution travels with the source, because the obligation does.</strong> OSM's
/// tiles are ODbL and the credit is a condition of using them (§4.5), so it is not a field a
/// custom source gets to leave empty.
/// </para>
/// </summary>
public sealed class MapSourceTests
{
	[Fact]
	public void Default_IsOpenStreetMap_WithItsCreditAttached()
	{
		MapSource.Default.Kind.ShouldBe(MapSourceKind.Osm,
			"a device that has never chosen gets what every map drew before this setting existed.");
		MapSource.Default.MaxZoom.ShouldBe(MapSource.OsmMaxZoom);
		MapSource.Default.TileUrl.ShouldNotBeNull();
		// §4.5: ODbL's credit is mandatory and permanent.
		MapSource.Default.AttributionText.ShouldContain("OpenStreetMap");
		MapSource.Default.Provider.ShouldBe(MapProvider.MapLibreOsm);
	}

	[Fact]
	public void Osm_RoundTrips()
	{
		MapSource.Decode(MapSource.Default.Encode()).ShouldBe(MapSource.Default);
	}

	[Fact]
	public void Custom_RoundTripsATemplateFullOfCharactersTheEncodingCouldHaveEaten()
	{
		// Query strings and braces are what a tile URL is made of, and '|' is the separator the
		// encoding uses - none of them may survive into the stored value unescaped.
		MapSource source = MapSource.Custom(
			"https://mt1.example.com/vt/lyrs=m&x={x}&y={y}&z={z}|v2",
			"© Example | Maps",
			maxZoom: 20);

		MapSource? decoded = MapSource.Decode(source.Encode());

		decoded.ShouldNotBeNull();
		decoded.Kind.ShouldBe(MapSourceKind.Custom);
		decoded.UrlTemplate.ShouldBe("https://mt1.example.com/vt/lyrs=m&x={x}&y={y}&z={z}|v2");
		decoded.Attribution.ShouldBe("© Example | Maps");
		decoded.MaxZoom.ShouldBe(20);
		decoded.Provider.ShouldBe(MapProvider.CustomRaster);
	}

	[Fact]
	public void OfflinePack_RoundTrips()
	{
		MapSource? decoded = MapSource.Decode(MapSource.OfflinePack("au-nsw").Encode());

		decoded.ShouldNotBeNull();
		decoded.Kind.ShouldBe(MapSourceKind.Offline);
		decoded.PackId.ShouldBe("au-nsw");
		decoded.Provider.ShouldBe(MapProvider.Pmtiles);
		// A pack is an extract of the same data, so it carries the same obligation.
		decoded.AttributionText.ShouldContain("OpenStreetMap");
	}

	[Fact]
	public void AnOfflinePacksTheme_RoundTrips()
	{
		MapSource? decoded = MapSource.Decode(MapSource.OfflinePack("au-nsw", MapTheme.Dark).Encode());

		decoded.ShouldNotBeNull();
		decoded.PackId.ShouldBe("au-nsw", "the theme picks a style document - the archive is the same one.");
		decoded.Theme.ShouldBe(MapTheme.Dark);
	}

	[Fact]
	public void APackStoredBeforeThemesExisted_ReadsAsLight()
	{
		// Six fields, which is every value any device wrote before the setting was added. Light is
		// what those packs were drawing, so this is the reading that changes nothing under anybody.
		MapSource? decoded = MapSource.Decode("1|offline|au-nsw|||19");

		decoded.ShouldNotBeNull();
		decoded.PackId.ShouldBe("au-nsw");
		decoded.Theme.ShouldBe(MapTheme.Light);
	}

	[Fact]
	public void AThemeThisBuildCannotName_ReadsAsLight_RatherThanDiscardingTheSource()
	{
		// From a build that ships a third theme. Losing the cartography is a visible but harmless
		// downgrade; losing the source would put the rider back on OSM in a dead zone.
		MapSource? decoded = MapSource.Decode("1|offline|au-nsw|||19|sepia");

		decoded.ShouldNotBeNull();
		decoded.PackId.ShouldBe("au-nsw");
		decoded.Theme.ShouldBe(MapTheme.Light);
	}

	[Fact]
	public void TheRasterSources_HaveNoThemeToStore()
	{
		// OSM's tiles and a rider's own arrive as finished images. A stored preference that could
		// never be honoured would come back the next time somebody chose a pack, which reads as the
		// app remembering something nobody asked it to.
		new MapSource(MapSourceKind.Osm, Theme: MapTheme.Dark).Normalised()!
			.Theme.ShouldBe(MapTheme.Light);

		(MapSource.Custom("https://tiles.example.com/{z}/{x}/{y}.png", "© Example") with { Theme = MapTheme.Dark })
			.Normalised()!.Theme.ShouldBe(MapTheme.Light);
	}

	[Theory]
	// The one you would actually type, and the reason it is refused: every host serves the app
	// over a secure scheme, so plain HTTP is mixed content and the WebView blocks it outright.
	[InlineData("http://mt1.google.com/vt/lyrs=m&x={x}&y={y}&z={z}")]
	[InlineData("https://tiles.example.com/{z}/{x}.png")]   // no {y}
	[InlineData("https://tiles.example.com/{x}/{y}.png")]   // no {z}
	[InlineData("tiles.example.com/{z}/{x}/{y}.png")]       // no scheme
	[InlineData("")]
	[InlineData(null)]
	public void ATemplateThatCannotLoad_IsRefused(string? template)
	{
		MapSource.IsUsableTemplate(template).ShouldBeFalse();

		MapSource.Custom(template!, "© Someone").Normalised().ShouldBeNull(
			"storing a template that can never load is how a traveller ends up with a blank map and no explanation.");
	}

	[Fact]
	public void HttpsIsAccepted_IncludingWithAQueryString()
	{
		MapSource.IsUsableTemplate("https://mt1.google.com/vt/lyrs=m&x={x}&y={y}&z={z}").ShouldBeTrue(
			"the mechanism takes any well-formed https template - what the traveller points it at is their decision.");
	}

	[Fact]
	public void CustomWithoutAttribution_IsRefused()
	{
		MapSource.Custom("https://tiles.example.com/{z}/{x}/{y}.png", "   ").Normalised().ShouldBeNull(
			"§4.5: the credit is a condition of using somebody's tiles, so it is not an optional field.");
	}

	[Fact]
	public void OfflineWithoutAPack_IsRefused()
	{
		new MapSource(MapSourceKind.Offline).Normalised().ShouldBeNull(
			"a pack id is the whole of what an offline source is - without one there is nothing to open.");
	}

	[Fact]
	public void SwitchingKind_DoesNotCarryTheOtherKindsFieldsAlong()
	{
		// A stored template that outlived a switch back to OSM would reappear the next time
		// somebody chose Custom, which reads as the app remembering something nobody asked it to.
		MapSource? normalised = new MapSource(
			MapSourceKind.Osm,
			PackId: "au-nsw",
			UrlTemplate: "https://tiles.example.com/{z}/{x}/{y}.png",
			Attribution: "© Example").Normalised();

		normalised.ShouldNotBeNull();
		normalised.PackId.ShouldBeNull();
		normalised.UrlTemplate.ShouldBeNull();
		normalised.Attribution.ShouldBeNull();
	}

	[Fact]
	public void MaxZoom_IsClampedIntoRange()
	{
		MapSource.Custom("https://tiles.example.com/{z}/{x}/{y}.png", "© Example", maxZoom: 99)
			.Normalised()!.MaxZoom.ShouldBe(MapSource.MaxAllowedZoom);

		MapSource.Custom("https://tiles.example.com/{z}/{x}/{y}.png", "© Example", maxZoom: -4)
			.Normalised()!.MaxZoom.ShouldBe(MapSource.MinAllowedZoom);
	}

	[Fact]
	public void OsmMaxZoom_IsNotTheRidersToRaise()
	{
		// OSM's raster stops at 19. Asking for more gets 404s, which reads on screen as the map
		// dissolving at exactly the zoom a marker is placed at (§16.1).
		new MapSource(MapSourceKind.Osm, MaxZoom: 22).Normalised()!.MaxZoom.ShouldBe(MapSource.OsmMaxZoom);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("garbage")]
	[InlineData("2|osm|||19")]                                  // a version this build cannot read
	[InlineData("1|hologram|||19")]                             // a kind from a newer build
	[InlineData("1|osm|||not-a-number")]
	[InlineData("1|custom||http%3A%2F%2Fx%2F%7Bz%7D|%C2%A9|19")] // decodes to a refused template
	public void AStoredValueThisBuildCannotUse_IsNoSource(string? stored)
	{
		MapSource.Decode(stored).ShouldBeNull(
			"the caller falls back to the default, which is a working map - no worse than a device that stored nothing.");
	}

	[Fact]
	public void Encode_RefusesASourceThatCannotDrawAMap()
	{
		Should.Throw<InvalidOperationException>(() => new MapSource(MapSourceKind.Offline).Encode());
	}
}
