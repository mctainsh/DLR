using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using static DLR.UI.Tests.Fakes.MapPackFixtures;

namespace DLR.UI.Tests.Components;

/// <summary>
/// The offer of an offline pack, on the map (§4.2).
/// <para>
/// It is <c>RideMap</c>'s banner, so it reaches every screen with a map on it at once. The tests
/// here are mostly about when it stays <em>quiet</em>, because a banner on every map is the failure
/// mode this feature has and the suppression is the part worth pinning down.
/// </para>
/// </summary>
public sealed class RideMapOfferTests : BunitContext
{
	/// <summary>Sydney, which two of the three offers below cover.</summary>
	private static readonly MapCamera SydneyCamera = new(-33.868, 151.209, 12);

	/// <summary>A view centred on Sydney - what the map reports once it has attached.</summary>
	private static readonly MapViewport OverSydney = new(
		TopLeftLatitude: -33.80, TopLeftLongitude: 151.10,
		BottomRightLatitude: -33.94, BottomRightLongitude: 151.32,
		ZoomLevel: 12,
		HeadingDeg: 0,
		// No canvas, which keeps SkiaMapOverlay unmounted: it rasterises through a JS module, and
		// none of these tests is about what it draws.
		CanvasWidthPx: 0, CanvasHeightPx: 0,
		DevicePixelRatio: 1);

	/// <summary>A view over the Atlantic, which nothing in the catalogue covers.</summary>
	private static readonly MapViewport OverTheAtlantic = OverSydney with
	{
		TopLeftLatitude = 30.1,
		TopLeftLongitude = -40.1,
		BottomRightLatitude = 29.9,
		BottomRightLongitude = -39.9,
	};

	private readonly FakeMapPackStore _packs = new();

	/// <summary>
	/// Wires a map over a device that could hold a pack. <paramref name="phone"/> false is a browser,
	/// which could not (§18.6).
	/// </summary>
	private FakeMapInterop RenderableMap(bool phone = true)
	{
		_packs.IsSupported = phone;

		FakeMapInterop map = new() { InitialViewport = OverSydney };

		Services.AddSingleton<IMapInterop>(map);
		Services.AddRideMapServices();
		Services.AddOfferedPacks(_packs);

		return map;
	}

	private IRenderedComponent<RideMap> RenderMap() =>
		Render<RideMap>(parameters => parameters.Add(p => p.Camera, SydneyCamera));

	private static void WaitForOffer(IRenderedComponent<RideMap> component, string expected) =>
		component.WaitForAssertion(
			() => component.Find(".dlr-map-offline-offer").TextContent.ShouldContain(expected),
			timeout: TimeSpan.FromSeconds(3));

	private static void WaitForNoOffer(IRenderedComponent<RideMap> component, string because)
	{
		component.WaitForAssertion(
			() => component.Instance.ShouldNotBeNull(),
			timeout: TimeSpan.FromMilliseconds(200));

		component.FindAll(".dlr-map-offline-offer").Count.ShouldBe(0, because);
	}

	/// <summary>
	/// A map drawing OpenStreetMap says so, and names the pack that would fix it. This is the offer
	/// made before anything has gone wrong - the whole point being that a rider finds out at the
	/// kitchen table rather than at the trailhead.
	/// </summary>
	[Fact]
	public void AMapDrawingOsm_OffersThePackCoveringTheGround()
	{
		FakeMapInterop map = RenderableMap();

		IRenderedComponent<RideMap> component = RenderMap();

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		WaitForOffer(component, "This map needs a signal");

		string banner = component.Find(".dlr-map-offline-offer").TextContent;
		banner.ShouldContain("New South Wales", customMessage: "the smaller of the two packs covering Sydney.");
		banner.ShouldContain("Download");
	}

	/// <summary>
	/// One tap: the archive lands and every map in the app is pointed at it. A button that downloaded
	/// without selecting would leave the rider on the same map they were just warned about.
	/// </summary>
	[Fact]
	public async Task TakingTheOffer_DownloadsThePackAndSelectsIt()
	{
		RenderableMap();

		IRenderedComponent<RideMap> component = RenderMap();

		WaitForOffer(component, "New South Wales");

		await component.InvokeAsync(() => component.Find(".dlr-offline-offer-take").Click());

		MapSourceState sources = Services.GetRequiredService<MapSourceState>();

		component.WaitForAssertion(
			() =>
			{
				sources.Chosen.Kind.ShouldBe(MapSourceKind.Offline);
				sources.Chosen.PackId.ShouldBe("au-nsw");
			},
			timeout: TimeSpan.FromSeconds(3));

		_packs.Versions.ShouldContainKey("au-nsw");

		component.WaitForAssertion(
			() => component.FindAll(".dlr-map-offline-offer").Count.ShouldBe(0,
				"the map is drawing with the pack now - there is nothing left to offer."),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// "Not now" is remembered, and remembered for the pack rather than for this map. Every other
	/// surface in the app asks the same state, so declining here is declining on Home too.
	/// </summary>
	[Fact]
	public async Task NotNow_SilencesThatPackEverywhere()
	{
		RenderableMap();

		IRenderedComponent<RideMap> component = RenderMap();

		WaitForOffer(component, "New South Wales");

		await component.InvokeAsync(() => component.Find(".dlr-offline-offer-quiet").Click());

		// Australia is still on offer - a decline is about one pack, not about the feature.
		WaitForOffer(component, "Australia");

		Services.GetRequiredService<OfflineMapOfferState>().IsDeclined("au-nsw").ShouldBeTrue();
	}

	/// <summary>
	/// A map that has run off the edge of its pack is offered the one that reaches, and says which
	/// of the two problems it has.
	/// </summary>
	[Fact]
	public async Task AMapOffTheEdgeOfItsPack_IsOfferedOneThatReaches()
	{
		FakeMapInterop map = RenderableMap();
		_packs.Add("au-tas", MapPackFixtures.Archive);

		MapSourceState sources = Services.GetRequiredService<MapSourceState>();
		await sources.SetAsync(MapSource.OfflinePack("au-tas"));

		IRenderedComponent<RideMap> component = RenderMap();

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => map.RaiseCoverage(hasTiles: false, zoomLevel: 12));

		WaitForOffer(component, "Your offline map does not reach here");
		component.Find(".dlr-map-offline-offer").TextContent.ShouldContain("New South Wales");
	}

	/// <summary>
	/// A map that has gone blank files where that happened, so Home can make the offer somewhere
	/// there is a connection to act on it. The phone that most needs a pack is the one that cannot
	/// download one right now.
	/// </summary>
	[Fact]
	public async Task AMapThatCannotDrawItsTiles_RemembersWhereForLater()
	{
		FakeMapInterop map = RenderableMap();

		IRenderedComponent<RideMap> component = RenderMap();

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => map.RaiseError("the tile server could not be reached"));

		OfflineMapOfferState offers = Services.GetRequiredService<OfflineMapOfferState>();

		component.WaitForAssertion(
			() => offers.MissedPoint.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		offers.MissedPoint!.Value.Latitude.ShouldBe(OverSydney.CentreLatitude, 0.001);
		offers.MissedPoint!.Value.Longitude.ShouldBe(OverSydney.CentreLongitude, 0.001);
	}

	/// <summary>
	/// Ground no published extract covers gets no banner. Nothing here guesses at a nearest region -
	/// a 300 MB download of somewhere else is worse than saying nothing.
	/// </summary>
	[Fact]
	public async Task GroundNoPackCovers_GetsNoBanner()
	{
		FakeMapInterop map = RenderableMap();
		map.InitialViewport = OverTheAtlantic;

		IRenderedComponent<RideMap> component = RenderMap();

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));
		await component.InvokeAsync(() => map.RaiseViewport(OverTheAtlantic));

		WaitForNoOffer(component, "no offer covers the middle of the Atlantic.");
	}

	/// <summary>
	/// The browser is never offered one, because it has nowhere to put it (§18.6). The tab would be
	/// asked for several hundred megabytes it cannot keep.
	/// </summary>
	[Fact]
	public void ABrowser_IsNeverOfferedAPack()
	{
		FakeMapInterop map = RenderableMap(phone: false);

		IRenderedComponent<RideMap> component = RenderMap();

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		WaitForNoOffer(component, "this host cannot hold an archive at all.");
	}

	/// <summary>
	/// A map whose caller is holding its source is left alone. That is the map-pack picker's world
	/// view and the settings screen's preview - both of them showing OSM deliberately, and neither
	/// of them a place to offer to change the rider's map from underneath.
	/// </summary>
	[Fact]
	public void AMapWhoseSourceItsCallerHolds_IsNeverOffered()
	{
		FakeMapInterop map = RenderableMap();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, SydneyCamera)
			.Add(p => p.Source, MapSource.Default));

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		WaitForNoOffer(component, "the caller decided what this map draws, for a reason of its own.");
	}

	/// <summary>
	/// The Settings → Maps preview is silent even though it follows the device's own setting. That
	/// screen already carries the catalogue, the download button and the progress bar, and a second
	/// copy of all three drawn over the preview of the choice being made is the feature arguing with
	/// itself.
	/// </summary>
	[Fact]
	public void AMapThatHasOptedOut_IsNeverOffered()
	{
		FakeMapInterop map = RenderableMap();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, SydneyCamera)
			.Add(p => p.OfferOfflineMaps, false));

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		WaitForNoOffer(component, "the screen this map sits on is itself the offer.");
	}

	/// <summary>
	/// A rider already drawing with a pack that covers the ground is told nothing at all. This is
	/// the ordinary state of a phone that has taken the offer, and it has to be silent or the
	/// feature never stops talking.
	/// </summary>
	[Fact]
	public async Task AMapAlreadyDrawingTheRightPack_SaysNothing()
	{
		FakeMapInterop map = RenderableMap();
		_packs.Add("au-nsw", MapPackFixtures.Archive);

		MapSourceState sources = Services.GetRequiredService<MapSourceState>();
		await sources.SetAsync(MapSource.OfflinePack("au-nsw"));

		IRenderedComponent<RideMap> component = RenderMap();

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));
		await component.InvokeAsync(() => map.RaiseCoverage(hasTiles: true, zoomLevel: 12));

		WaitForNoOffer(component, "the map is drawing the pack that covers here.");
	}
}
