using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Components;

/// <summary>
/// What a map says when it is drawing ground its tiles do not cover - see
/// <see cref="IMapInterop.CoverageChanged"/> for why that failure carries no error with it.
/// <para>
/// The banner is <c>RideMap</c>'s, so it reaches every screen with a map on it at once - the two
/// settings screens and the live ride among them.
/// </para>
/// </summary>
public sealed class RideMapCoverageTests : BunitContext
{
	private static readonly MapCamera SampleCamera = new(-33.868, 151.209, 12);

	/// <summary>
	/// A fake whose first viewport has no canvas yet, which keeps <c>SkiaMapOverlay</c> unmounted:
	/// it rasterises through a JS module, and none of these tests is about what it draws.
	/// </summary>
	private FakeMapInterop RenderableMap()
	{
		FakeMapInterop map = new();
		map.InitialViewport = map.InitialViewport with { CanvasWidthPx = 0, CanvasHeightPx = 0 };

		Services.AddSingleton<IMapInterop>(map);
		Services.AddRideMapServices();
		return map;
	}

	/// <summary>
	/// The warning names the zoom, and it is not there until the base map has said something - the
	/// archive's box arrives a round trip after the style, and a map that opened on a pack would
	/// otherwise flash the banner every time.
	/// </summary>
	[Fact]
	public async Task GroundThePackDoesNotHold_IsSaidOnTheMap()
	{
		FakeMapInterop map = RenderableMap();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, SampleCamera));

		component.WaitForAssertion(
			() => map.InitCount.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		component.FindAll(".dlr-map-no-tiles").Count.ShouldBe(0,
			"a map that has not yet been told what its tiles cover must not accuse them of anything.");

		await component.InvokeAsync(() => map.RaiseCoverage(hasTiles: false, zoomLevel: 12.7));

		component.WaitForAssertion(() =>
		{
			// Floored, because that is the zoom the tiles are actually asked for at.
			component.Find(".dlr-map-no-tiles").TextContent
				.ShouldContain("No offline tiles in this area at zoom 12");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// It comes down again on its own. Riding back into the pack is the ordinary way out of this,
	/// and a warning that outlived the condition would be read as the pack being broken.
	/// </summary>
	[Fact]
	public async Task RidingBackOntoThePack_TakesTheWarningDown()
	{
		FakeMapInterop map = RenderableMap();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, SampleCamera));

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => map.RaiseCoverage(hasTiles: false, zoomLevel: 11));
		component.WaitForAssertion(
			() => component.FindAll(".dlr-map-no-tiles").Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => map.RaiseCoverage(hasTiles: true, zoomLevel: 11));

		component.WaitForAssertion(
			() => component.FindAll(".dlr-map-no-tiles").Count.ShouldBe(0,
				"the rider is back over ground the pack holds - there is nothing left to warn about."),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// A map that is failing says so once. The tile-error banner describes a map that cannot draw
	/// and this one describes a map drawing empty ground correctly; two explanations for one grey
	/// screen is worse than either alone.
	/// </summary>
	[Fact]
	public async Task ATileError_SilencesTheCoverageWarning()
	{
		FakeMapInterop map = RenderableMap();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, SampleCamera));

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => map.RaiseCoverage(hasTiles: false, zoomLevel: 9));
		component.WaitForAssertion(
			() => component.FindAll(".dlr-map-no-tiles").Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => map.RaiseError("the archive could not be reached at all"));

		component.WaitForAssertion(() =>
		{
			component.Markup.ShouldContain("Map tiles unavailable");
			component.FindAll(".dlr-map-no-tiles").Count.ShouldBe(0,
				"a map that cannot read its archive at all is not a map that has been panned off the edge of one.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// Different tiles, no verdict. The banner belongs to the source that earned it, and a rider
	/// switching to OpenStreetMap because of it must not be left reading it over a world map.
	/// </summary>
	[Fact]
	public async Task ChangingTheSource_ClearsTheWarningUntilTheNewOneAnswers()
	{
		FakeMapInterop map = RenderableMap();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, SampleCamera));

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => map.RaiseCoverage(hasTiles: false, zoomLevel: 14));
		component.WaitForAssertion(
			() => component.FindAll(".dlr-map-no-tiles").Count.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		MapSourceState sources = Services.GetRequiredService<MapSourceState>();
		await component.InvokeAsync(() =>
			sources.SetAsync(MapSource.Custom("https://tiles.example.com/{z}/{x}/{y}.png", "© Example")));

		component.WaitForAssertion(() =>
		{
			map.Sources.ShouldNotBeEmpty("the restyle is what clears the verdict.");
			component.FindAll(".dlr-map-no-tiles").Count.ShouldBe(0,
				"the warning was about the pack that has just been replaced.");
		}, timeout: TimeSpan.FromSeconds(3));
	}
}
