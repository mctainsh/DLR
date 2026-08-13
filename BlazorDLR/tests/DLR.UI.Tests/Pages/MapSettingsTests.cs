using BlazorDLR.Shared.Pages.Settings;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// Settings → Maps (§4.5): which tiles go under every map in the app.
/// <para>
/// The screen's job is to make a tile URL judgeable. It is the one setting whose value cannot be
/// checked by reading it — the difference between a working template and a blank map is a
/// character somewhere in the middle — so the rules it enforces before storing anything are the
/// substance of these tests, not decoration on top of them.
/// </para>
/// </summary>
public sealed class MapSettingsTests : PageTestContext
{
	/// <summary>
	/// The preview map. <c>InitAsync</c> throws so <c>SkiaMapOverlay</c> — whose canvas is
	/// browser-only — never mounts; <c>RideMap</c> shows its stated-error branch, which is not
	/// what is under test here.
	/// </summary>
	private readonly FakeMapInterop _map = new()
	{
		InitException = new InvalidOperationException("No base map in bUnit."),
	};

	/// <summary>This device's downloaded packs. Empty unless a test adds one.</summary>
	private readonly FakeMapPackStore _packs = new();

	/// <summary>Wires the page's dependencies. <paramref name="phone"/> decides whether offline is even possible (§18.6).</summary>
	private MapSourceState Wire(bool phone = true)
	{
		_packs.IsSupported = phone;

		Services.AddSingleton<IMapInterop>(_map);
		Services.AddSingleton<IOfflineStore, UnavailableOfflineStore>();
		Services.AddSingleton<IMapPackStore>(_packs);
		Services.AddSingleton<IDeviceSettings, InMemoryDeviceSettings>();
		Services.AddSingleton<MapSourceState>();
		Services.AddSingleton(sp => new MapPackDownloader(
			sp.GetRequiredService<IMapPackStore>(),
			MapPackDownloader.CreateCredentialFreeClient()));
		Services.AddSingleton<MapPackState>();
		Services.AddSingleton<RouteStyleState>();
		Services.AddRealAuthorizationPipeline();

		return Services.GetRequiredService<MapSourceState>();
	}

	private IRenderedComponent<Maps> RenderPage() => Render<Maps>();

	[Fact]
	public void AllThreeSourcesAreOffered()
	{
		Wire();

		IRenderedComponent<Maps> page = RenderPage();

		page.FindAll("input[name=map-source]").Count.ShouldBe(3,
			"OpenStreetMap, an offline pack, and a tile server of the rider's own.");
	}

	[Fact]
	public void WithNoPackOnTheDevice_OfflineIsOfferedButNotSelectable()
	{
		Wire();

		IRenderedComponent<Maps> page = RenderPage();

		page.FindAll("input[name=map-source]")[1].HasAttribute("disabled").ShouldBeTrue(
			"a radio that stores a source with no archive behind it would leave the map with nothing to draw.");
		page.Markup.ShouldContain("No map packs on this device yet");
	}

	[Fact]
	public void WithAPackOnTheDevice_OfflineBecomesSelectable()
	{
		Wire();
		_packs.Add("sydney", [1, 2, 3, 4]);

		IRenderedComponent<Maps> page = RenderPage();

		page.WaitForAssertion(
			() => page.FindAll("input[name=map-source]")[1].HasAttribute("disabled").ShouldBeFalse(),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task ChoosingAPack_SelectsItAsTheOfflineSource()
	{
		MapSourceState state = Wire();
		_packs.Add("sydney", [1, 2, 3, 4]);

		IRenderedComponent<Maps> page = RenderPage();

		page.WaitForAssertion(
			() => page.FindAll("input[name=map-source]")[1].HasAttribute("disabled").ShouldBeFalse(),
			timeout: TimeSpan.FromSeconds(3));

		await page.InvokeAsync(() => page.FindAll("input[name=map-source]")[1].Change(true));

		// One pack is not a choice worth making somebody make — picking Offline takes it.
		state.Chosen.Kind.ShouldBe(MapSourceKind.Offline);
		state.Chosen.PackId.ShouldBe("sydney");
		state.Effective.Provider.ShouldBe(MapProvider.Pmtiles);
	}

	[Fact]
	public void TheDownloadButtonIsOfferedOnlyForAUsableLinkAndName()
	{
		Wire();

		IRenderedComponent<Maps> page = RenderPage();

		page.Find("button.primary").HasAttribute("disabled").ShouldBeTrue("nothing typed yet.");

		page.Find("input[placeholder='https://example.com/sydney.pmtiles']")
			.Input("https://www.example.com/Stuff/sydney.pmtiles");
		page.Find("input[placeholder='sydney']").Input("has space");

		page.Find("button.primary").HasAttribute("disabled").ShouldBeTrue(
			"a name the store would refuse must be caught before the download, not after it.");

		page.Find("input[placeholder='sydney']").Input("sydney");

		page.Find("button.primary").HasAttribute("disabled").ShouldBeFalse();
	}

	[Fact]
	public void InABrowser_ThereIsNowhereToDownloadTo_SoTheFormIsAbsent()
	{
		Wire(phone: false);

		IRenderedComponent<Maps> page = RenderPage();

		page.FindAll("fieldset.download").ShouldBeEmpty(
			"§18.6: a field that downloads 26 MB into a tab that forgets it is worse than absent.");
	}

	[Fact]
	public async Task DeletingTheSelectedPack_StopsDrawingWithIt()
	{
		MapSourceState state = Wire();
		_packs.Add("sydney", [1, 2, 3, 4]);

		IRenderedComponent<Maps> page = RenderPage();

		page.WaitForAssertion(
			() => page.FindAll("input[name=map-source]")[1].HasAttribute("disabled").ShouldBeFalse(),
			timeout: TimeSpan.FromSeconds(3));

		await page.InvokeAsync(() => page.FindAll("input[name=map-source]")[1].Change(true));
		state.Chosen.Kind.ShouldBe(MapSourceKind.Offline);

		await page.InvokeAsync(() => page.Find("button.remove").Click());

		state.Chosen.ShouldBe(MapSource.Default,
			"a stored source pointing at a file that is gone falls back silently, which reads as the delete having done nothing.");
	}

	[Fact]
	public void InABrowser_OfflineSaysWhyRatherThanDisappearing()
	{
		Wire(phone: false);

		IRenderedComponent<Maps> page = RenderPage();

		page.FindAll("input[name=map-source]").Count.ShouldBe(3,
			"§18.6: hiding it would leave a rider who set it on their phone with no explanation here.");
		page.Markup.ShouldContain("Only on the phone app");
	}

	[Fact]
	public void ChoosingAnotherTileServer_RevealsTheFields_AndStoresNothingYet()
	{
		MapSourceState state = Wire();

		IRenderedComponent<Maps> page = RenderPage();
		page.FindAll("input[name=map-source]")[2].Change(true);

		page.FindAll("fieldset.custom").ShouldNotBeEmpty();
		state.Chosen.ShouldBe(MapSource.Default,
			"picking the radio only moves the form — a source with no URL in it cannot draw anything.");
	}

	[Fact]
	public void AnIncompleteTileServer_CannotBeApplied()
	{
		MapSourceState state = Wire();

		IRenderedComponent<Maps> page = RenderPage();
		page.FindAll("input[name=map-source]")[2].Change(true);

		// A template but no attribution: §4.5 makes the credit a condition of using the tiles.
		page.Find("input[placeholder^='https://tiles.example.com']")
			.Input("https://tiles.example.com/{z}/{x}/{y}.png");

		page.Find("button.primary").HasAttribute("disabled").ShouldBeTrue();
		state.Chosen.ShouldBe(MapSource.Default);
	}

	[Fact]
	public void PlainHttp_IsRefusedWithAReasonTheRiderCanActon()
	{
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		page.FindAll("input[name=map-source]")[2].Change(true);

		page.Find("input[placeholder^='https://tiles.example.com']")
			.Input("http://mt1.google.com/vt/lyrs=m&x={x}&y={y}&z={z}");
		page.Find("input[placeholder='© Example Maps']").Input("© Google");

		page.Find("button.primary").HasAttribute("disabled").ShouldBeTrue(
			"the app is served over a secure scheme, so plain http is mixed content and the WebView blocks it.");
	}

	[Fact]
	public async Task ACompleteTileServer_IsAppliedAndStored()
	{
		MapSourceState state = Wire();

		IRenderedComponent<Maps> page = RenderPage();
		page.FindAll("input[name=map-source]")[2].Change(true);

		page.Find("input[placeholder^='https://tiles.example.com']")
			.Input("https://tiles.example.com/{z}/{x}/{y}.png");
		page.Find("input[placeholder='© Example Maps']").Input("© Example Maps");

		await page.InvokeAsync(() => page.Find("button.primary").Click());

		state.Chosen.Kind.ShouldBe(MapSourceKind.Custom);
		state.Chosen.UrlTemplate.ShouldBe("https://tiles.example.com/{z}/{x}/{y}.png");
		state.Chosen.Attribution.ShouldBe("© Example Maps");
		state.Effective.Provider.ShouldBe(MapProvider.CustomRaster);
	}

	[Fact]
	public async Task ChoosingOpenStreetMap_AppliesAtOnce()
	{
		MapSourceState state = Wire();
		await state.SetAsync(MapSource.Custom("https://tiles.example.com/{z}/{x}/{y}.png", "© Example"));

		IRenderedComponent<Maps> page = RenderPage();
		await page.InvokeAsync(() => page.FindAll("input[name=map-source]")[0].Change(true));

		state.Chosen.ShouldBe(MapSource.Default, "there is nothing to fill in, so there is nothing to confirm.");
	}

	[Fact]
	public async Task WhatWasTypedComesBackNextVisit()
	{
		// The long URL is the point: a rider adding Melbourne after Sydney should be editing two
		// words, not finding the link again.
		Wire();

		IRenderedComponent<Maps> first = RenderPage();
		first.FindAll("input[name=map-source]")[2].Change(true);

		first.Find("input[placeholder^='https://tiles.example.com']").Input("https://tiles.example.com/{z}/{x}");
		await first.InvokeAsync(() => first.Find("input[placeholder^='https://tiles.example.com']").Blur());

		first.Find("input[placeholder='https://example.com/sydney.pmtiles']")
			.Input("https://www.noptic1.com/Stuff/sydney.pmtiles");
		await first.InvokeAsync(() =>
			first.Find("input[placeholder='https://example.com/sydney.pmtiles']").Blur());

		// A second render over the same device store is how a test spells "came back to the screen".
		IRenderedComponent<Maps> second = RenderPage();

		second.WaitForAssertion(() =>
		{
			second.Find("input[placeholder='https://example.com/sydney.pmtiles']")
				.GetAttribute("value").ShouldBe("https://www.noptic1.com/Stuff/sydney.pmtiles");
		}, timeout: TimeSpan.FromSeconds(3));

		second.FindAll("input[name=map-source]")[2].Change(true);

		second.Find("input[placeholder^='https://tiles.example.com']")
			.GetAttribute("value").ShouldBe("https://tiles.example.com/{z}/{x}",
				"kept even though it is half-typed — MapSource would have refused it.");
	}

	[Fact]
	public async Task ASuccessfulDownloadLeavesTheLinkInPlace()
	{
		Wire();

		IRenderedComponent<Maps> page = RenderPage();

		page.Find("input[placeholder='https://example.com/sydney.pmtiles']")
			.Input("https://example.com/sydney.pmtiles");
		page.Find("input[placeholder='sydney']").Input("sydney");

		// The download itself fails here — there is no server behind the fake handler — but the
		// fields must survive either way, which is the behaviour under test.
		await page.InvokeAsync(() => page.Find("button.primary").Click());

		page.Find("input[placeholder='https://example.com/sydney.pmtiles']")
			.GetAttribute("value").ShouldBe("https://example.com/sydney.pmtiles");
		page.Find("input[placeholder='sydney']").GetAttribute("value").ShouldBe("sydney");
	}

	[Fact]
	public async Task TheFormOpensOnWhatIsAlreadyStored()
	{
		MapSourceState state = Wire();
		await state.SetAsync(MapSource.Custom("https://tiles.example.com/{z}/{x}/{y}.png", "© Example", 17));

		IRenderedComponent<Maps> page = RenderPage();

		page.WaitForAssertion(
			() => page.FindAll("fieldset.custom").ShouldNotBeEmpty(
				"a rider returning to the screen should find their own choice selected."),
			timeout: TimeSpan.FromSeconds(3));

		page.Find("input[placeholder^='https://tiles.example.com']")
			.GetAttribute("value").ShouldBe("https://tiles.example.com/{z}/{x}/{y}.png");
	}
}
