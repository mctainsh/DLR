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

	/// <summary>
	/// Picks the offline radio, which on a phone is the second of the three.
	/// <para>
	/// Everything to do with packs is behind it — the pack list, the light / dark choice and the
	/// download form — so most tests here start with this rather than with the page as it opens.
	/// </para>
	/// </summary>
	private static Task ChooseOfflineAsync(IRenderedComponent<Maps> page) =>
		page.InvokeAsync(() => page.FindAll("input[name=map-source]")[1].Change(true));

	[Fact]
	public void OnAPhone_AllThreeSourcesAreOffered()
	{
		Wire();

		IRenderedComponent<Maps> page = RenderPage();

		page.FindAll("input[name=map-source]").Count.ShouldBe(3,
			"OpenStreetMap, an offline pack, and a tile server of the rider's own.");
	}

	[Fact]
	public async Task WithNoPackOnTheDevice_OfflineIsStillSelectable_BecauseItIsTheWayToGetOne()
	{
		Wire();

		IRenderedComponent<Maps> page = RenderPage();

		page.FindAll("input[name=map-source]")[1].HasAttribute("disabled").ShouldBeFalse(
			"the download form is behind this radio — disabling it until a pack exists would seal " +
			"off the only route to getting one.");
		page.Markup.ShouldContain("No map packs on this phone yet");

		await ChooseOfflineAsync(page);

		page.FindAll("fieldset.download").ShouldNotBeEmpty();
	}

	[Fact]
	public async Task ChoosingOffline_StoresNothingUntilThereIsAPackBehindIt()
	{
		MapSourceState state = Wire();

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);

		state.Chosen.ShouldBe(MapSource.Default,
			"an offline source with no archive behind it cannot draw a map, and MapSource refuses it.");
	}

	[Fact]
	public async Task ChoosingAPack_SelectsItAsTheOfflineSource()
	{
		MapSourceState state = Wire();
		_packs.Add("sydney", [1, 2, 3, 4]);

		IRenderedComponent<Maps> page = RenderPage();

		await ChooseOfflineAsync(page);

		// One pack is not a choice worth making somebody make — picking Offline takes it.
		state.Chosen.Kind.ShouldBe(MapSourceKind.Offline);
		state.Chosen.PackId.ShouldBe("sydney");
		state.Effective.Provider.ShouldBe(MapProvider.Pmtiles);
	}

	[Fact]
	public async Task TheDownloadFormIsBehindTheOfflineRadio()
	{
		Wire();

		IRenderedComponent<Maps> page = RenderPage();

		page.FindAll("fieldset.download").ShouldBeEmpty(
			"a rider on OpenStreetMap is not part-way through adding a pack — two text fields and a " +
			"Download button make the screen look like it has a job outstanding.");

		await ChooseOfflineAsync(page);
		page.FindAll("fieldset.download").ShouldNotBeEmpty();

		await page.InvokeAsync(() => page.FindAll("input[name=map-source]")[0].Change(true));
		page.FindAll("fieldset.download").ShouldBeEmpty("and it goes away again.");
	}

	[Fact]
	public async Task TheDownloadButtonIsOfferedOnlyForAUsableLinkAndName()
	{
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);

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

		await ChooseOfflineAsync(page);
		state.Chosen.Kind.ShouldBe(MapSourceKind.Offline);

		await page.InvokeAsync(() => page.Find("button.remove").Click());

		state.Chosen.ShouldBe(MapSource.Default,
			"a stored source pointing at a file that is gone falls back silently, which reads as the delete having done nothing.");
	}

	[Fact]
	public void InABrowser_OfflineIsNotOffered_BecauseItCanNeverApplyHere()
	{
		Wire(phone: false);

		IRenderedComponent<Maps> page = RenderPage();

		page.FindAll("input[name=map-source]").Count.ShouldBe(2,
			"§18.6: a browser has nowhere to keep a pack, so the option is not one this device can " +
			"ever take — OpenStreetMap and a tile server of the rider's own are the two real answers.");
		page.Markup.ShouldNotContain("Offline map pack");
		page.FindAll("fieldset.appearance").ShouldBeEmpty("and nothing behind it, either.");
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
		page.Find("#tile-server-problem").TextContent.ShouldContain("https://");
	}

	/// <summary>
	/// A disabled button says why, from the moment the form opens.
	/// <para>
	/// The rider cannot press it, so there is no attempt to wait for — and a control they can see
	/// but cannot use, with nothing explaining the gap, is the worst of the available states. Each
	/// message names the defect in what they typed rather than restating the rule under the field.
	/// </para>
	/// </summary>
	[Theory]
	[InlineData("", "Enter the tile server's URL")]
	[InlineData("http://tiles.example.com/{z}/{x}/{y}.png", "has to start with https://")]
	[InlineData("https://tiles.example.com/{z}/{x}.png", "missing {y}")]
	[InlineData("https://tiles.example.com/{z}.png", "missing {x} and {y}")]
	public void TheTileServerButtonSaysWhyItIsDisabled(string template, string expected)
	{
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		page.FindAll("input[name=map-source]")[2].Change(true);

		if (template.Length > 0)
		{
			page.Find("input[placeholder^='https://tiles.example.com']").Input(template);
		}

		page.Find("button.primary").HasAttribute("disabled").ShouldBeTrue();
		page.Find("#tile-server-problem").TextContent.ShouldContain(expected);

		// Tied to the button rather than announced, so a screen reader reaching the control it
		// explains reads the two together.
		page.Find("button.primary").GetAttribute("aria-describedby").ShouldBe("tile-server-problem");
	}

	[Fact]
	public void AUsableTileServer_LeavesNoProblemOnScreen()
	{
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		page.FindAll("input[name=map-source]")[2].Change(true);

		page.Find("input[placeholder^='https://tiles.example.com']")
			.Input("https://tiles.example.com/{z}/{x}/{y}.png");
		page.Find("input[placeholder='© Example Maps']").Input("© Example Maps");

		page.Find("button.primary").HasAttribute("disabled").ShouldBeFalse();
		page.FindAll("#tile-server-problem").ShouldBeEmpty();
		page.Find("button.primary").HasAttribute("aria-describedby").ShouldBeFalse(
			"the explanation is gone, so pointing at it would leave a dangling reference.");
	}

	/// <summary>
	/// Light or dark, and only for a pack (§13 Q26). The archive holds no colour — the theme picks
	/// between two style documents that both ship with the app — so the other two sources, which
	/// hand back finished raster images, have nothing to offer here.
	/// </summary>
	[Fact]
	public async Task TheLightAndDarkChoiceBelongsToTheOfflineSourceAlone()
	{
		Wire();
		_packs.Add("sydney", [1, 2, 3, 4]);

		IRenderedComponent<Maps> page = RenderPage();

		page.FindAll("fieldset.appearance").ShouldBeEmpty("OpenStreetMap's tiles arrive already painted.");

		await ChooseOfflineAsync(page);
		page.FindAll("input[name=map-theme]").Count.ShouldBe(2);

		await page.InvokeAsync(() => page.FindAll("input[name=map-source]")[2].Change(true));
		page.FindAll("fieldset.appearance").ShouldBeEmpty("nor does a rider's own tile server.");
	}

	[Fact]
	public async Task ChoosingDark_RestylesThePackWithoutTouchingWhichPack()
	{
		MapSourceState state = Wire();
		_packs.Add("sydney", [1, 2, 3, 4]);

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);

		state.Chosen.Theme.ShouldBe(MapTheme.Light, "what every pack drew before the setting existed.");

		await page.InvokeAsync(() => page.FindAll("input[name=map-theme]")[1].Change(true));

		state.Chosen.Kind.ShouldBe(MapSourceKind.Offline);
		state.Chosen.PackId.ShouldBe("sydney", "the same archive — only the style document changes.");
		state.Chosen.Theme.ShouldBe(MapTheme.Dark);

		await page.InvokeAsync(() => page.FindAll("input[name=map-theme]")[0].Change(true));
		state.Chosen.Theme.ShouldBe(MapTheme.Light);
	}

	/// <summary>
	/// Picked before a pack has been selected, it waits rather than being lost.
	/// <para>
	/// Two packs here, because that is the state in which no source is stored yet: with one, picking
	/// offline takes it. An offline source needs an archive behind it — <c>MapSource.Normalised</c>
	/// refuses one without — so a theme chosen first has nothing to be written against, and the
	/// alternative to carrying it would be silently discarding a choice the rider just made.
	/// </para>
	/// </summary>
	[Fact]
	public async Task DarkChosenBeforeAPack_IsCarriedIntoTheOneChosenNext()
	{
		MapSourceState state = Wire();
		_packs.Add("sydney", [1, 2, 3, 4]);
		_packs.Add("melbourne", [5, 6, 7, 8]);

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);

		await page.InvokeAsync(() => page.FindAll("input[name=map-theme]")[1].Change(true));
		state.Chosen.ShouldBe(MapSource.Default, "with two packs, none is chosen yet.");

		await page.InvokeAsync(() => page.FindAll("input[name=map-pack]")[0].Change(true));

		state.Chosen.Kind.ShouldBe(MapSourceKind.Offline);
		state.Chosen.PackId.ShouldNotBeNull();
		state.Chosen.Theme.ShouldBe(MapTheme.Dark);
	}

	[Fact]
	public async Task TheChosenThemeComesBackNextVisit()
	{
		MapSourceState state = Wire();
		_packs.Add("sydney", [1, 2, 3, 4]);
		await state.SetAsync(MapSource.OfflinePack("sydney", MapTheme.Dark));

		IRenderedComponent<Maps> page = RenderPage();

		page.WaitForAssertion(
			() => page.FindAll("input[name=map-theme]")[1].HasAttribute("checked").ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));
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

		// The pack link lives behind the offline radio, so the two halves of the draft are typed on
		// two different faces of this screen — which is exactly why both have to survive the trip.
		await ChooseOfflineAsync(first);

		first.Find("input[placeholder='https://example.com/sydney.pmtiles']")
			.Input("https://www.noptic1.com/Stuff/sydney.pmtiles");
		await first.InvokeAsync(() =>
			first.Find("input[placeholder='https://example.com/sydney.pmtiles']").Blur());

		// A second render over the same device store is how a test spells "came back to the screen".
		IRenderedComponent<Maps> second = RenderPage();

		// The radio is picked inside the wait, not before it. The page reads the draft after its
		// first render and then adopts the stored source, which puts the form back on OpenStreetMap
		// — a selection made before that lands would be undone by it. Choosing offline is
		// idempotent (with no packs it stores nothing), so retrying costs nothing.
		second.WaitForAssertion(() =>
		{
			second.FindAll("input[name=map-source]")[1].Change(true);
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
		await ChooseOfflineAsync(page);

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
