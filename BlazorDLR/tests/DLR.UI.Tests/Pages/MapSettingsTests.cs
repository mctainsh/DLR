using System.Net;
using BlazorDLR.Shared.Pages.Settings;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Tracks;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// Settings → Maps (§4.5): which tiles go under every map in the app.
/// <para>
/// The screen's job is to make a tile URL judgeable. It is the one setting whose value cannot be
/// checked by reading it - the difference between a working template and a blank map is a
/// character somewhere in the middle - so the rules it enforces before storing anything are the
/// substance of these tests, not decoration on top of them.
/// </para>
/// </summary>
public sealed class MapSettingsTests : PageTestContext
{
	/// <summary>
	/// The preview map. <c>InitAsync</c> throws so <c>SkiaMapOverlay</c> - whose canvas is
	/// browser-only - never mounts; <c>RideMap</c> shows its stated-error branch, which is not
	/// what is under test here.
	/// </summary>
	private readonly FakeMapInterop _map = new()
	{
		InitException = new InvalidOperationException("No base map in bUnit."),
	};

	/// <summary>This device's downloaded packs. Empty unless a test adds one.</summary>
	private readonly FakeMapPackStore _packs = new();

	/// <summary>
	/// What the catalogue answers with (§4.2). Two regions unless a test replaces the body - enough
	/// that the list is a choice rather than a single row, which is the state most of these assert
	/// against.
	/// </summary>
	private StubHttpHandler _catalogue = new("""
		[
			{ "id": "au-nsw", "name": "New South Wales", "minZoom": 0, "maxZoom": 14,
			  "sizeBytes": 351089645, "sha256": "745b01e7", "version": 1,
			  "url": "https://packs.example.com/au-nsw.v1.pmtiles" },
			{ "id": "au-tas", "name": "Tasmania", "minZoom": 0, "maxZoom": 14,
			  "sizeBytes": 57935628, "sha256": "dd9d57d3", "version": 1,
			  "url": "https://packs.example.com/au-tas.v1.pmtiles" }
		]
		""");

	/// <summary>
	/// What a pack download gets back: a minimal well-formed PMTiles archive - the v3 magic and
	/// filler. Stubbed rather than left on a real client so no test here reaches the network; what
	/// a transfer does with ranges and interruptions is <c>MapPackDownloaderTests</c>' subject.
	/// </summary>
	private readonly StubHttpHandler _archive = new(new byte[] { 0x50, 0x4D, 0x54, 0x69, 0x6C, 0x65, 0x73, 0x03 });

	/// <summary>
	/// Two areas that do not overlap, each publishing the ground it covers (§4.2) - which is what
	/// puts them on the map picker at all. New South Wales and Tasmania, roughly, plus one across
	/// the Tasman so that choosing on the map has a country to move away from.
	/// </summary>
	private const string BoundedCatalogue = """
		[
			{ "id": "au-nsw", "name": "New South Wales", "region": "Australia", "minZoom": 0, "maxZoom": 14,
			  "bounds": { "minLatitude": -37.52, "minLongitude": 140.99, "maxLatitude": -28.15, "maxLongitude": 153.65 },
			  "sizeBytes": 351089645, "sha256": "745b01e7", "version": 1,
			  "url": "https://packs.example.com/au-nsw.v1.pmtiles" },
			{ "id": "au-tas", "name": "Tasmania", "region": "Australia", "minZoom": 0, "maxZoom": 14,
			  "bounds": { "minLatitude": -43.70, "minLongitude": 143.80, "maxLatitude": -39.50, "maxLongitude": 148.50 },
			  "sizeBytes": 57935628, "sha256": "dd9d57d3", "version": 1,
			  "url": "https://packs.example.com/au-tas.v1.pmtiles" },
			{ "id": "nz-sth", "name": "South Island", "region": "New Zealand", "minZoom": 0, "maxZoom": 14,
			  "bounds": { "minLatitude": -46.70, "minLongitude": 166.40, "maxLatitude": -40.50, "maxLongitude": 174.40 },
			  "sizeBytes": 91234567, "sha256": "aa11bb22", "version": 1,
			  "url": "https://packs.example.com/nz-sth.v1.pmtiles" }
		]
		""";

	/// <summary>
	/// The same, with the whole country over the top of one of its states - which is the ordinary
	/// shape of a real catalogue rather than a contrived one, and the case a tap cannot answer on
	/// its own.
	/// </summary>
	private const string NestedCatalogue = """
		[
			{ "id": "au-nsw", "name": "New South Wales", "region": "Australia", "minZoom": 0, "maxZoom": 14,
			  "bounds": { "minLatitude": -37.52, "minLongitude": 140.99, "maxLatitude": -28.15, "maxLongitude": 153.65 },
			  "sizeBytes": 351089645, "sha256": "745b01e7", "version": 1,
			  "url": "https://packs.example.com/au-nsw.v1.pmtiles" },
			{ "id": "au-all", "name": "Australia", "region": "Australia", "minZoom": 0, "maxZoom": 14,
			  "bounds": { "minLatitude": -43.64, "minLongitude": 112.92, "maxLatitude": -10.06, "maxLongitude": 153.64 },
			  "sizeBytes": 2351089645, "sha256": "cc33dd44", "version": 1,
			  "url": "https://packs.example.com/au-all.v1.pmtiles" }
		]
		""";

	/// <summary>Sydney - inside New South Wales in both catalogues above.</summary>
	private const double SydneyLatitude = -33.87;

	/// <summary>Sydney.</summary>
	private const double SydneyLongitude = 151.21;

	/// <summary>
	/// A catalogue of <paramref name="count"/> regions, for the tests about a list too long to put
	/// on a phone screen a row at a time.
	/// </summary>
	private static string ManyOffers(int count) =>
		"[" + string.Join(",", Enumerable.Range(0, count).Select(index => $$"""
			{ "id": "au-{{index}}", "name": "Region {{index}}", "minZoom": 0, "maxZoom": 14,
			  "sizeBytes": 1048576, "sha256": "745b01e7", "version": 1,
			  "url": "https://packs.example.com/au-{{index}}.v1.pmtiles" }
			""")) + "]";

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
			new HttpClient(_archive)));
		Services.AddSingleton(_ => new MapPackCatalogue(
			new HttpClient(_catalogue),
			new Uri("https://packs.example.com/catalogue.json")));
		Services.AddSingleton<MapPackState>();
		Services.AddSingleton<RouteStyleState>();
		Services.AddSingleton<ConfirmService>();
		Services.AddRealAuthorizationPipeline();

		return Services.GetRequiredService<MapSourceState>();
	}

	/// <summary>
	/// Answers the one dialog the app has. Deleting a pack asks first, so a test that wants the
	/// delete to happen has to say yes - which is the point.
	/// </summary>
	private async Task AnswerConfirmAsync(IRenderedComponent<Maps> page, bool confirmed)
	{
		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();

		page.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(), timeout: TimeSpan.FromSeconds(3));
		await page.InvokeAsync(() => confirm.Respond(confirmed));
	}

	/// <summary>
	/// The catalogue is fetched when the offline radio is picked, not when the page renders, so
	/// every assertion about the list has to wait for the request to land.
	/// </summary>
	private static void WaitForCatalogue(IRenderedComponent<Maps> page) =>
		page.WaitForAssertion(
			() => page.FindAll("select.offers option").Count.ShouldBeGreaterThan(1),
			timeout: TimeSpan.FromSeconds(3));

	/// <summary>
	/// Picks a pack in the dropdown by catalogue id. The first option is the "Choose a map…"
	/// placeholder, so a test never selects by index.
	/// </summary>
	private static Task ChoosePackToDownloadAsync(IRenderedComponent<Maps> page, string packId) =>
		page.InvokeAsync(() => page.Find("select.offers").Change(packId));

	/// <summary>
	/// The frame an online source puts the preview on: the world, stopping at the latitude Web
	/// Mercator stops at. Matches <c>Maps.WorldFrame</c>, which is the value under test.
	/// </summary>
	private static readonly TrackBounds TheWholeWorld = new(-85, -180, 85, 180);

	/// <summary>
	/// Lets the base map attach, which the tests about framing need and the rest do not: a fit is
	/// applied to a map that came up, so <see cref="FakeMapInterop.InitException"/> - the default
	/// here - would swallow every one of them. Attaching also mounts <c>SkiaMapOverlay</c>, which
	/// talks to its own JS module.
	/// </summary>
	private void AttachTheBaseMap()
	{
		_map.InitException = null;
		JSInterop.SetupModule("./_content/BlazorDLR.Shared/map/overlay.js")
			.SetupVoid("present", _ => true)
			.SetVoidResult();
	}

	private IRenderedComponent<Maps> RenderPage() => Render<Maps>();

	/// <summary>
	/// Opens the pick-an-area-on-a-map window (§4.2). It is only offered once the catalogue has
	/// landed and something in it published the ground it covers, so every caller waits first.
	/// </summary>
	private static Task OpenTheMapAsync(IRenderedComponent<Maps> page)
	{
		WaitForCatalogue(page);
		page.WaitForAssertion(
			() => page.FindAll("button.pick-on-map").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		return page.InvokeAsync(() => page.Find("button.pick-on-map").Click());
	}

	/// <summary>
	/// Stands in for a finger on the base map. The fake interop is registered once and both maps on
	/// this page listen to it - the preview wires no click handler, so only the picker answers.
	/// </summary>
	private Task TapTheMapAsync(IRenderedComponent<Maps> page, double latitude, double longitude) =>
		page.InvokeAsync(() => _map.RaiseClick(latitude, longitude));

	/// <summary>
	/// Picks the offline radio, which on a phone is the second of the three.
	/// <para>
	/// Everything to do with packs is behind it - the pack list, the light / dark choice and the
	/// download form - so most tests here start with this rather than with the page as it opens.
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
			"OpenStreetMap, an offline pack, and a tile server of the traveller's own.");
	}

	[Fact]
	public async Task WithNoPackOnTheDevice_OfflineIsStillSelectable_BecauseItIsTheWayToGetOne()
	{
		Wire();

		IRenderedComponent<Maps> page = RenderPage();

		page.FindAll("input[name=map-source]")[1].HasAttribute("disabled").ShouldBeFalse(
			"the catalogue is behind this radio - disabling it until a pack exists would seal " +
			"off the only route to getting one.");
		page.Markup.ShouldContain("No map packs on this phone yet");

		await ChooseOfflineAsync(page);

		page.FindAll("fieldset.catalogue").ShouldNotBeEmpty();
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

		// One pack is not a choice worth making somebody make - picking Offline takes it.
		state.Chosen.Kind.ShouldBe(MapSourceKind.Offline);
		state.Chosen.PackId.ShouldBe("sydney");
		state.Effective.Provider.ShouldBe(MapProvider.Pmtiles);
	}

	[Fact]
	public async Task TheCatalogueIsBehindTheOfflineRadio()
	{
		Wire();

		IRenderedComponent<Maps> page = RenderPage();

		page.FindAll("fieldset.catalogue").ShouldBeEmpty(
			"a traveller on OpenStreetMap is not part-way through adding a pack - a list of regions and a " +
			"Download button make the screen look like it has a job outstanding.");
		_catalogue.Requests.ShouldBe(0,
			"and the list is not fetched for a screen that is not showing it - that is a request to a " +
			"host which is not the API, spent on a traveller's connection.");

		await ChooseOfflineAsync(page);
		page.FindAll("fieldset.catalogue").ShouldNotBeEmpty();

		await page.InvokeAsync(() => page.FindAll("input[name=map-source]")[0].Change(true));
		page.FindAll("fieldset.catalogue").ShouldBeEmpty("and it goes away again.");
	}

	/// <summary>
	/// The screen the whole change is for: what was a link and a name to invent is a dropdown of
	/// regions with the size on each, because a rider knows which state they are riding through and
	/// does not know a URL.
	/// </summary>
	[Fact]
	public async Task ThePacksOnOfferAreOneDropdownWithWhatEachCosts()
	{
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);

		WaitForCatalogue(page);

		// Two regions plus the "Choose a map…" placeholder.
		page.FindAll("select.offers option").Count.ShouldBe(3);

		// The size is on the option itself: a native picker shows nothing beside it, and the size is
		// the number a rider decides on before spending it - a phone's units, not the catalogue's.
		page.Find("select.offers").TextContent.ShouldContain("New South Wales - 334.8 MB");
		page.Find("select.offers").TextContent.ShouldContain("Tasmania");

		page.FindAll("input[placeholder='https://example.com/sydney.pmtiles']").ShouldBeEmpty(
			"and there is nothing left to type - the id and the name come from the publisher now.");
	}

	/// <summary>
	/// The reason it is a dropdown at all: the catalogue is meant to grow past what a phone screen
	/// can show, and a row per region would bury the source radios and the preview map under it.
	/// </summary>
	[Fact]
	public async Task AHundredPacksStillTakeOneLineOfTheScreen()
	{
		_catalogue = new StubHttpHandler(ManyOffers(120));
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);

		WaitForCatalogue(page);

		page.FindAll("select.offers").Count.ShouldBe(1);
		page.FindAll("select.offers option").Count.ShouldBe(121, "the placeholder and every region.");
		page.FindAll("button.download").Count.ShouldBe(1, "one Download, not one per region.");
	}

	/// <summary>
	/// There is no search box over either dropdown, however long the catalogue is - and this is here
	/// so one does not come back by accident.
	/// <para>
	/// Both were tried. The area search only ever narrowed what the country dropdown above it had
	/// already narrowed, and the country search filtered a list a rider scrolls straight to their own
	/// country in; two text fields stacked over two pickers cost more height on a phone than they
	/// saved in scrolling.
	/// </para>
	/// </summary>
	[Fact]
	public async Task NeitherDropdownCarriesASearchBox()
	{
		_catalogue = new StubHttpHandler(ManyOffers(120));
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);
		WaitForCatalogue(page);

		page.FindAll("input[type=search]").ShouldBeEmpty("the country dropdown is the filter this list needs.");
		page.FindAll("select.offers option").Count.ShouldBe(121, "and every map is still reachable.");
	}

	/// <summary>
	/// Nothing picked is not something to download. The button carries the accent colour, so it is
	/// the loudest control on the screen - it has to be inert until there is an answer for it.
	/// </summary>
	[Fact]
	public async Task TheDownloadButtonIsTheAffirmativeOne_AndWaitsForAChoice()
	{
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);
		WaitForCatalogue(page);

		page.Find("button.download").ClassList.ShouldContain("primary");
		page.Find("button.download").HasAttribute("disabled").ShouldBeTrue("nothing is picked yet.");

		await ChoosePackToDownloadAsync(page, "au-nsw");

		page.Find("button.download").HasAttribute("disabled").ShouldBeFalse();
	}

	/// <summary>
	/// A pack already on the phone is not something to fetch again from here.
	/// <para>
	/// This button offered "Download again" for one, which is a second several-hundred-megabyte
	/// transfer over the top of a file that is already good - and the rider who taps it is nearly
	/// always the one who thinks the first one failed. The way to a fresh copy is Delete in the list
	/// above, which frees the space before it is spent rather than doubling it during the transfer.
	/// </para>
	/// </summary>
	[Fact]
	public async Task APackAlreadyOnThePhone_CannotBeDownloadedAgain_WithoutDeletingItFirst()
	{
		Wire();
		_packs.Add("au-nsw", [1, 2, 3, 4]);

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);
		WaitForCatalogue(page);

		await ChoosePackToDownloadAsync(page, "au-nsw");

		page.Find("button.download").TextContent.Trim().ShouldBe("Download",
			"there is no second label - the button is simply not available for this one.");
		page.Find("button.download").HasAttribute("disabled").ShouldBeTrue();
		page.Find("#pack-already-here").TextContent.ShouldContain("Delete it from the list above",
			customMessage: "a disabled button cannot say why it is grey.");

		// And a pack the phone does not hold is unaffected.
		await ChoosePackToDownloadAsync(page, "au-tas");

		page.Find("button.download").HasAttribute("disabled").ShouldBeFalse();
		page.FindAll("#pack-already-here").ShouldBeEmpty();
	}

	/// <summary>
	/// A rider already drawing with a pack arrives with offline <em>adopted</em> rather than chosen -
	/// no tap, no <c>ChooseAsync</c> - and they are the person most likely to want a second region.
	/// Hanging the fetch off the radio alone meant the list stayed empty until they selected
	/// OpenStreetMap and came back to it.
	/// </summary>
	[Fact]
	public async Task ArrivingWithOfflineAlreadySet_ShowsTheListWithoutTouchingARadio()
	{
		MapSourceState state = Wire();
		_packs.Add("au-tas", [1, 2, 3, 4]);
		await state.SetAsync(MapSource.OfflinePack("au-tas", MapTheme.Light));

		IRenderedComponent<Maps> page = RenderPage();

		page.WaitForAssertion(
			() => page.FindAll("select.offers option").Count.ShouldBe(3),
			timeout: TimeSpan.FromSeconds(3));

		_catalogue.Requests.ShouldBe(1);
	}

	/// <summary>
	/// Filed under the catalogue's id rather than anything a rider chose, which is the point of
	/// having a catalogue: <c>MapSource</c> stores that id, so two phones holding New South Wales
	/// agree on what it is called.
	/// </summary>
	[Fact]
	public async Task DownloadingFromTheCatalogue_FilesThePackUnderItsPublishedId()
	{
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);

		WaitForCatalogue(page);

		await ChoosePackToDownloadAsync(page, "au-nsw");
		await page.InvokeAsync(() => page.Find("button.download").Click());

		// The option saying so is what tells the test the transfer finished - the download runs in
		// the scoped state so it survives the rider leaving, which also means it does not finish
		// inside the click.
		page.WaitForAssertion(
			() => page.Find("select.offers").TextContent.ShouldContain("(on this phone)"),
			timeout: TimeSpan.FromSeconds(3));

		_archive.LastRequest.ShouldBe(new Uri("https://packs.example.com/au-nsw.v1.pmtiles"));
		(await _packs.ListAsync()).Select(pack => pack.PackId).ShouldContain("au-nsw");
	}

	/// <summary>
	/// A download does not select what it fetched. A rider adding Victoria while riding on the New
	/// South Wales pack should keep drawing the map in front of them.
	/// </summary>
	[Fact]
	public async Task DownloadingAPack_DoesNotSwitchTheMapToIt()
	{
		MapSourceState state = Wire();
		_packs.Add("au-tas", [1, 2, 3, 4]);

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);
		state.Chosen.PackId.ShouldBe("au-tas", "the one pack already on the phone.");

		WaitForCatalogue(page);
		await ChoosePackToDownloadAsync(page, "au-nsw");
		await page.InvokeAsync(() => page.Find("button.download").Click());

		page.WaitForAssertion(
			() => page.FindAll("select.offers option")[1].TextContent.ShouldContain("(on this phone)"),
			timeout: TimeSpan.FromSeconds(3));

		state.Chosen.PackId.ShouldBe("au-tas", "switching is a separate, deliberate tap.");
	}

	/// <summary>
	/// The list is the whole interface here, so failing to read it is not a footnote - and the
	/// commonest cause is a rider with no signal, who is exactly the person trying to download a map
	/// for later.
	/// </summary>
	[Fact]
	public async Task ACatalogueThatCannotBeRead_SaysSo()
	{
		_catalogue = new StubHttpHandler("") { Status = HttpStatusCode.ServiceUnavailable };
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);

		page.WaitForAssertion(
			() => page.Find("fieldset.catalogue .error-text").TextContent.ShouldContain("503"),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// Read the first time the form needs it, then held. A catalogue is rebuilt when somebody
	/// publishes a new extract - weeks apart - so a second read inside one run of the app is a
	/// request that answers the same thing.
	/// </summary>
	[Fact]
	public async Task TheCatalogueIsReadOnce_AndThatCopyIsHeld()
	{
		MapSourceState state = Wire();
		_packs.Add("au-tas", [1, 2, 3, 4]);

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);

		WaitForCatalogue(page);
		_catalogue.Requests.ShouldBe(1);

		// Everything that puts the form back on the offline source: a theme change, a pack
		// selection, and picking the radio again. Each one re-renders the list from the copy.
		await page.InvokeAsync(() => page.FindAll("input[name=map-theme]")[1].Change(true));
		await page.InvokeAsync(() => page.FindAll("input[name=map-source]")[0].Change(true));
		await ChooseOfflineAsync(page);

		_catalogue.Requests.ShouldBe(1, "the copy is kept until the app restarts.");
		state.Chosen.PackId.ShouldBe("au-tas");
		page.FindAll("select.offers option").Count.ShouldBe(3, "and the list is still on screen.");
	}

	/// <summary>
	/// With no refresh control, a failed first read has to be retriable - otherwise a rider who
	/// opened this in a tunnel has no route to the list but restarting the app. Coming back to the
	/// offline source is that gesture.
	/// </summary>
	[Fact]
	public async Task AFailedRead_IsAskedAgainWhenTheRiderComesBackToTheOfflineSource()
	{
		_catalogue = new StubHttpHandler("") { Status = HttpStatusCode.ServiceUnavailable };
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);

		page.WaitForAssertion(
			() => page.Find("fieldset.catalogue .error-text").ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		await page.InvokeAsync(() => page.FindAll("input[name=map-source]")[0].Change(true));
		await ChooseOfflineAsync(page);

		_catalogue.Requests.ShouldBe(2, "a failed read is not a copy, so it is not held.");
	}

	/// <summary>
	/// But not from a re-render of a form they are already looking at. A theme change lands on the
	/// offline source again, and asking a host that just refused at each of those is a retry loop on
	/// the connection least able to serve one.
	/// </summary>
	[Fact]
	public async Task AFailedRead_IsNotRetriedByAThemeChange()
	{
		_catalogue = new StubHttpHandler("") { Status = HttpStatusCode.ServiceUnavailable };
		Wire();
		_packs.Add("au-tas", [1, 2, 3, 4]);

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);

		page.WaitForAssertion(
			() => page.Find("fieldset.catalogue .error-text").ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		await page.InvokeAsync(() => page.FindAll("input[name=map-theme]")[1].Change(true));
		await page.InvokeAsync(() => page.FindAll("input[name=map-theme]")[0].Change(true));

		_catalogue.Requests.ShouldBe(1, "picking dark is not asking for the list again.");
	}

	/// <summary>
	/// There is no refresh control, and this is here so one does not come back by accident. A button
	/// offering to re-fetch a file rebuilt weeks apart invites pulling it can never reward.
	/// </summary>
	[Fact]
	public async Task ThereIsNothingToPressToReadTheCatalogueAgain()
	{
		_catalogue = new StubHttpHandler(BoundedCatalogue);
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);
		WaitForCatalogue(page);

		page.FindAll("button.refresh").ShouldBeEmpty();
		page.FindAll("fieldset.catalogue button").Select(button => button.TextContent.Trim())
			.ShouldBe(["Select area from map", "Download"],
				"two ways to answer which area, and one way to commit to it - nothing else.");
	}

	/// <summary>
	/// The screen the map picker is for: somebody planning a trip through country they have not
	/// ridden knows where they are going on a map, and does not know that the extract covering it is
	/// published under a name they have never heard.
	/// </summary>
	[Fact]
	public async Task PointingAtGroundOnTheMap_PicksTheAreaCoveringIt()
	{
		_catalogue = new StubHttpHandler(BoundedCatalogue);
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);
		await OpenTheMapAsync(page);

		page.FindAll(".area-picker").ShouldNotBeEmpty();

		await TapTheMapAsync(page, SydneyLatitude, SydneyLongitude);

		page.FindAll(".area-picker").ShouldBeEmpty("choosing closes the window.");
		page.Find("select.offers").GetAttribute("value").ShouldBe("au-nsw");
		page.Find("button.download").HasAttribute("disabled").ShouldBeFalse();
	}

	/// <summary>
	/// And it stops there. What the map answers is which area; the several hundred megabytes are
	/// still committed to by pressing Download, because a window that started a transfer as it
	/// closed would spend a rider's connection on a tap they made to look at a map.
	/// </summary>
	[Fact]
	public async Task ChoosingOnTheMap_DoesNotStartTheDownload()
	{
		_catalogue = new StubHttpHandler(BoundedCatalogue);
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);
		await OpenTheMapAsync(page);

		await TapTheMapAsync(page, SydneyLatitude, SydneyLongitude);

		_archive.Requests.ShouldBe(0);
		(await _packs.ListAsync()).ShouldBeEmpty();

		await page.InvokeAsync(() => page.Find("button.download").Click());

		page.WaitForAssertion(
			() => page.Find("select.offers").TextContent.ShouldContain("(on this phone)"),
			timeout: TimeSpan.FromSeconds(3));

		_archive.LastRequest.ShouldBe(new Uri("https://packs.example.com/au-nsw.v1.pmtiles"),
			"and pressing it fetches what the map chose.");
	}

	/// <summary>
	/// The second dropdown lists the chosen country's areas and <c>Chosen</c> resolves against that
	/// list, so an area set on its own under some other country would be a selection the rider
	/// cannot see and a Download button that stayed grey for no stated reason.
	/// </summary>
	[Fact]
	public async Task ChoosingOnTheMap_MovesTheCountryToMatch()
	{
		_catalogue = new StubHttpHandler(BoundedCatalogue);
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);
		WaitForCatalogue(page);

		await page.InvokeAsync(() => page.Find("select.regions").Change("New Zealand"));
		page.Find("select.offers").TextContent.ShouldNotContain(
			"New South Wales", customMessage: "narrowed to the wrong country.");

		await OpenTheMapAsync(page);
		await TapTheMapAsync(page, SydneyLatitude, SydneyLongitude);

		page.Find("select.regions").GetAttribute("value").ShouldBe("Australia");
		page.Find("select.offers").GetAttribute("value").ShouldBe("au-nsw");
		page.Find("button.download").HasAttribute("disabled").ShouldBeFalse();
	}

	/// <summary>
	/// A country and the states inside it are both on offer, so a point is regularly in two boxes at
	/// once. The tap cannot answer that on its own and neither can the app: it is a real choice
	/// between a small download and a large one, and it is put to the rider.
	/// </summary>
	[Fact]
	public async Task WhereAreasOverlap_TheRiderIsAskedWhichOne()
	{
		_catalogue = new StubHttpHandler(NestedCatalogue);
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);
		await OpenTheMapAsync(page);

		await TapTheMapAsync(page, SydneyLatitude, SydneyLongitude);

		page.FindAll(".area-picker").ShouldNotBeEmpty("nothing is chosen until they say which.");
		page.Find("select.offers").GetAttribute("value").ShouldBeNullOrEmpty();

		// Smallest first: the most specific area containing a point is what somebody pointing at it
		// means, and it is also the smaller of the two downloads.
		page.FindAll(".area-picker .choices li button .name").Select(choice => choice.TextContent)
			.ShouldBe(["New South Wales", "Australia"]);

		await page.InvokeAsync(() => page.FindAll(".area-picker .choices li button")[1].Click());

		page.FindAll(".area-picker").ShouldBeEmpty();
		page.Find("select.offers").GetAttribute("value").ShouldBe("au-all", "they asked for the whole country.");
	}

	/// <summary>
	/// A tap in the ocean is not an error and does not close anything - it is somebody still
	/// looking, and taking the map away from them would be the wrong answer to a near miss.
	/// </summary>
	[Fact]
	public async Task PointingWhereNoAreaIsOffered_SaysSoAndLeavesTheMapUp()
	{
		_catalogue = new StubHttpHandler(BoundedCatalogue);
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);
		await OpenTheMapAsync(page);

		// The middle of the Pacific.
		await TapTheMapAsync(page, -20.0, -140.0);

		page.FindAll(".area-picker").ShouldNotBeEmpty();
		page.Find(".area-picker .status").TextContent.ShouldContain("No map on offer covers that point");
		page.Find("select.offers").GetAttribute("value").ShouldBeNullOrEmpty();
	}

	/// <summary>
	/// Leaving without choosing changes nothing. The window is a way of reading the catalogue, and
	/// opening one should never be a commitment.
	/// </summary>
	[Fact]
	public async Task ClosingTheMapWithoutChoosing_LeavesTheFormAlone()
	{
		_catalogue = new StubHttpHandler(BoundedCatalogue);
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);
		WaitForCatalogue(page);

		await ChoosePackToDownloadAsync(page, "au-tas");
		await OpenTheMapAsync(page);
		await page.InvokeAsync(() => page.Find(".area-picker .close").Click());

		page.FindAll(".area-picker").ShouldBeEmpty();
		page.Find("select.offers").GetAttribute("value").ShouldBe("au-tas", "what was picked before is untouched.");
	}

	/// <summary>
	/// The field is newer than the catalogue riders are fetching from today, so a list with no
	/// bounds in it is a state that exists in the wild. The button is absent rather than disabled:
	/// a disabled control says somebody is one step away from something, and here they are not -
	/// nothing they can do on this device will put boxes on that map.
	/// </summary>
	[Fact]
	public async Task WithNoPublishedBounds_ThereIsNoMapToPickFrom()
	{
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);
		WaitForCatalogue(page);

		page.FindAll("button.pick-on-map").ShouldBeEmpty(
			"the catalogue on the host today publishes no bounds, and a world map with nothing " +
			"drawn on it answers no taps.");
		page.FindAll("select.offers option").Count.ShouldBe(3, "and every area is still reachable by name.");
	}

	/// <summary>
	/// The bug this pair of tests exists for. The catalogue publishes <c>http://</c> URLs on the one
	/// host the platform configs permit; the screen drew a Download button for them and the click
	/// handler then refused it against a rule of its own, which is a button that does nothing and
	/// says nothing.
	/// </summary>
	[Fact]
	public async Task APackOfferedOverPlainHttpFromThePermittedHost_IsFetched()
	{
		_catalogue = new StubHttpHandler($$"""
			[
				{ "id": "au-nsw", "name": "New South Wales", "minZoom": 0, "maxZoom": 14,
				  "sizeBytes": 351089645, "sha256": "745b01e7", "version": 1,
				  "url": "http://{{MapPackCatalogue.CleartextHost}}/au-nsw.v1.pmtiles" }
			]
			""");
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);

		WaitForCatalogue(page);
		await ChoosePackToDownloadAsync(page, "au-nsw");
		await page.InvokeAsync(() => page.Find("button.download").Click());

		page.WaitForAssertion(
			() => page.Find("select.offers").TextContent.ShouldContain("(on this phone)"),
			timeout: TimeSpan.FromSeconds(3));

		_archive.LastRequest.ShouldBe(
			new Uri($"http://{MapPackCatalogue.CleartextHost}/au-nsw.v1.pmtiles"));
	}

	/// <summary>
	/// Every other cleartext host is blocked beneath this code, so the transfer would end inside the
	/// platform. Tapping it has to say that rather than do nothing - a silent no-op reads as a broken
	/// button, which is exactly how this was first reported.
	/// </summary>
	[Fact]
	public async Task APackOfferedOverPlainHttpFromAnywhereElse_SaysWhyRatherThanNothing()
	{
		_catalogue = new StubHttpHandler("""
			[
				{ "id": "au-nsw", "name": "New South Wales", "minZoom": 0, "maxZoom": 14,
				  "sizeBytes": 351089645, "sha256": "745b01e7", "version": 1,
				  "url": "http://packs.example.com/au-nsw.v1.pmtiles" }
			]
			""");
		Wire();

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);

		WaitForCatalogue(page);
		await ChoosePackToDownloadAsync(page, "au-nsw");
		await page.InvokeAsync(() => page.Find("button.download").Click());

		page.WaitForAssertion(
			() => page.Find("fieldset.catalogue .status").TextContent.ShouldContain("https"),
			timeout: TimeSpan.FromSeconds(3));

		_archive.Requests.ShouldBe(0, "and nothing left the device to find that out.");
	}

	[Fact]
	public void InABrowser_ThereIsNowhereToDownloadTo_SoTheCatalogueIsAbsent()
	{
		Wire(phone: false);

		IRenderedComponent<Maps> page = RenderPage();

		page.FindAll("fieldset.catalogue").ShouldBeEmpty(
			"§18.6: a list that downloads 300 MB into a tab that forgets it is worse than absent.");
		_catalogue.Requests.ShouldBe(0, "and nothing is fetched to fill a list this host does not render.");
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
		await AnswerConfirmAsync(page, confirmed: true);

		// The answer resolves a task the page is awaiting, so the delete happens after Respond
		// returns rather than inside it.
		page.WaitForAssertion(
			() => state.Chosen.ShouldBe(MapSource.Default,
				"a stored source pointing at a file that is gone falls back silently, which reads as "
				+ "the delete having done nothing."),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// Delete is a small control in a row next to the radio somebody taps to choose a map, on a
	/// phone, often in gloves - and what it throws away is the largest thing this app puts on the
	/// device. The cost of a mis-tap is the Wi-Fi they have to find again, which on a trip may not
	/// exist.
	/// </summary>
	[Fact]
	public async Task DeletingAPack_AsksFirst_AndNothingGoesBeforeTheAnswer()
	{
		Wire();
		_packs.Add("sydney", [1, 2, 3, 4]);

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);

		await page.InvokeAsync(() => page.Find("button.remove").Click());

		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();
		page.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(), timeout: TimeSpan.FromSeconds(3));

		(await _packs.ListAsync()).ShouldNotBeEmpty("nothing may go before the traveller has answered.");
		confirm.Current!.Danger.ShouldBeTrue("an accidental delete should look like an accident.");
	}

	[Fact]
	public async Task CancellingTheDelete_KeepsThePack()
	{
		MapSourceState state = Wire();
		_packs.Add("sydney", [1, 2, 3, 4]);

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);

		await page.InvokeAsync(() => page.Find("button.remove").Click());
		await AnswerConfirmAsync(page, confirmed: false);

		// The pack list re-renders on any change, so give the answer the same chance to have done
		// something as the confirming test does before asserting that it did not.
		page.WaitForAssertion(
			() => page.FindAll("input[name=map-pack], .packs li").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		(await _packs.ListAsync()).Select(pack => pack.PackId).ShouldContain("sydney");
		state.Chosen.PackId.ShouldBe("sydney", "and the map is still drawn from it.");
	}

	/// <summary>
	/// The size is in the question because it is the number that makes the consequence concrete -
	/// and because reclaiming it is why a rider came to this screen when the phone said it was full.
	/// </summary>
	[Fact]
	public async Task TheDeleteQuestionNamesThePackAndWhatItFrees()
	{
		Wire();
		_packs.Add("au-nsw", new byte[2048]);

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);
		WaitForCatalogue(page);

		await page.InvokeAsync(() => page.Find("button.remove").Click());

		ConfirmService confirm = Services.GetRequiredService<ConfirmService>();
		page.WaitForAssertion(() => confirm.Current.ShouldNotBeNull(), timeout: TimeSpan.FromSeconds(3));

		// The catalogue's name for it, not the slug - the same thing the row itself shows.
		confirm.Current!.Title.ShouldBe("Delete New South Wales?");
		confirm.Current.Message.ShouldContain("2 KB");
	}

	[Fact]
	public void InABrowser_OfflineIsNotOffered_BecauseItCanNeverApplyHere()
	{
		Wire(phone: false);

		IRenderedComponent<Maps> page = RenderPage();

		page.FindAll("input[name=map-source]").Count.ShouldBe(2,
			"§18.6: a browser has nowhere to keep a pack, so the option is not one this device can " +
			"ever take - OpenStreetMap and a tile server of the traveller's own are the two real answers.");
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
			"picking the radio only moves the form - a source with no URL in it cannot draw anything.");
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
	/// The rider cannot press it, so there is no attempt to wait for - and a control they can see
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
	/// Light or dark, and only for a pack (§13 Q26). The archive holds no colour - the theme picks
	/// between two style documents that both ship with the app - so the other two sources, which
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
		page.FindAll("fieldset.appearance").ShouldBeEmpty("nor does a traveller's own tile server.");
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
		state.Chosen.PackId.ShouldBe("sydney", "the same archive - only the style document changes.");
		state.Chosen.Theme.ShouldBe(MapTheme.Dark);

		await page.InvokeAsync(() => page.FindAll("input[name=map-theme]")[0].Change(true));
		state.Chosen.Theme.ShouldBe(MapTheme.Light);
	}

	/// <summary>
	/// Picked before a pack has been selected, it waits rather than being lost.
	/// <para>
	/// Two packs here, because that is the state in which no source is stored yet: with one, picking
	/// offline takes it. An offline source needs an archive behind it - <c>MapSource.Normalised</c>
	/// refuses one without - so a theme chosen first has nothing to be written against, and the
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
		// The long URL is the point: a rider interrupted mid-URL should come back to their own text
		// rather than to an empty box and a tile server to look up again.
		Wire();

		IRenderedComponent<Maps> first = RenderPage();
		first.FindAll("input[name=map-source]")[2].Change(true);

		first.Find("input[placeholder^='https://tiles.example.com']").Input("https://tiles.example.com/{z}/{x}");
		await first.InvokeAsync(() => first.Find("input[placeholder^='https://tiles.example.com']").Blur());

		// A second render over the same device store is how a test spells "came back to the screen".
		IRenderedComponent<Maps> second = RenderPage();

		// The radio is picked inside the wait, not before it. The page reads the draft after its
		// first render and then adopts the stored source, which puts the form back on OpenStreetMap
		// - a selection made before that lands would be undone by it.
		second.WaitForAssertion(() =>
		{
			second.FindAll("input[name=map-source]")[2].Change(true);
			second.Find("input[placeholder^='https://tiles.example.com']")
				.GetAttribute("value").ShouldBe("https://tiles.example.com/{z}/{x}",
					"kept even though it is half-typed - MapSource would have refused it.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// An online source opens the preview on the whole world, not on a city.
	/// <para>
	/// A fixed city is an opinion this screen has no business holding. The live map falls back to
	/// Sydney because it has a ride to show and must show it somewhere; this one is a sample of a
	/// tile server with no place in mind, and opening on one city tells every traveller who does not
	/// live there that the check they came to make starts with a pan.
	/// </para>
	/// </summary>
	[Fact]
	public void ThePreviewOpensOnTheWholeWorld()
	{
		Wire();

		RenderPage();

		// Recorded even though InitAsync throws - the camera a map opens on is decided before the
		// base map gets a chance to fail.
		_map.LastOptions!.Camera.ShouldBe(new MapCamera(0, 0, 0));
	}

	/// <summary>
	/// And the frame agrees with the camera: the world, to the edge of what Web Mercator draws.
	/// <para>
	/// The camera is where the base map opens, before it has a canvas to measure; the fit is the
	/// correction once it has one, which is why this test lets the map attach and the one above does
	/// not.
	/// </para>
	/// </summary>
	[Fact]
	public void AnOnlineSource_FramesThePreviewOnTheWholeWorld()
	{
		AttachTheBaseMap();
		Wire();

		IRenderedComponent<Maps> page = RenderPage();

		page.WaitForAssertion(
			() => _map.Fits.ShouldContain(TheWholeWorld),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// A map pack is previewed over the ground it covers instead (§4.2).
	/// <para>
	/// The world would be a preview of a grey rectangle with the answer off in a corner: a pack
	/// draws one region and nothing outside it, so its own extent is the only frame that shows what
	/// was downloaded. The extent comes from the catalogue, which is read when the offline source is
	/// picked.
	/// </para>
	/// </summary>
	[Fact]
	public async Task AMapPack_FramesThePreviewOnTheGroundItCovers()
	{
		AttachTheBaseMap();
		_catalogue = new StubHttpHandler(BoundedCatalogue);
		Wire();

		// One pack on the device, so picking the offline source also selects it.
		_packs.Add("au-tas", [1, 2, 3, 4]);

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);

		page.WaitForAssertion(
			() => _map.Fits.ShouldContain(new TrackBounds(-43.70, 143.80, -39.50, 148.50)),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// And going back to an online source frames the world again - the preview follows whichever
	/// source it is drawing with, in both directions.
	/// </summary>
	[Fact]
	public async Task LeavingThePack_FramesThePreviewOnTheWorldAgain()
	{
		AttachTheBaseMap();
		_catalogue = new StubHttpHandler(BoundedCatalogue);
		Wire();
		_packs.Add("au-tas", [1, 2, 3, 4]);

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);

		page.WaitForAssertion(
			() => _map.Fits.ShouldContain(new TrackBounds(-43.70, 143.80, -39.50, 148.50)),
			timeout: TimeSpan.FromSeconds(3));

		await page.InvokeAsync(() => page.FindAll("input[name=map-source]")[0].Change(true));

		page.WaitForAssertion(
			() => _map.Fits[^1].ShouldBe(TheWholeWorld),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// A pack whose entry publishes no box leaves the preview on the world. The field is newer than
	/// the catalogue travellers are fetching from today, so this is the ordinary case rather than a
	/// broken one - and the world is a worse frame, not a wrong one.
	/// </summary>
	[Fact]
	public async Task APackWithNoPublishedExtent_LeavesThePreviewOnTheWorld()
	{
		AttachTheBaseMap();
		Wire();
		_packs.Add("au-tas", [1, 2, 3, 4]);

		IRenderedComponent<Maps> page = RenderPage();
		await ChooseOfflineAsync(page);
		WaitForCatalogue(page);

		_map.Fits.ShouldNotBeEmpty("the world was framed on the way in.");
		_map.Fits.ShouldAllBe(box => box == TheWholeWorld, "and nothing moved it off.");
	}

	[Fact]
	public async Task TheFormOpensOnWhatIsAlreadyStored()
	{
		MapSourceState state = Wire();
		await state.SetAsync(MapSource.Custom("https://tiles.example.com/{z}/{x}/{y}.png", "© Example", 17));

		IRenderedComponent<Maps> page = RenderPage();

		page.WaitForAssertion(
			() => page.FindAll("fieldset.custom").ShouldNotBeEmpty(
				"a traveller returning to the screen should find their own choice selected."),
			timeout: TimeSpan.FromSeconds(3));

		page.Find("input[placeholder^='https://tiles.example.com']")
			.GetAttribute("value").ShouldBe("https://tiles.example.com/{z}/{x}/{y}.png");
	}

	// ---------- How long a rider's pin outlives their last fix (§5.3) ----------

	/// <summary>
	/// The one control on this screen that changes what is drawn on the map rather than what it is
	/// drawn over. It is here because it is a map preference held on this device, like the tile
	/// source above it - and, like it, it never reaches the server.
	/// </summary>
	[Fact]
	public void PinExpiry_OffersTheSixValues_AndOpensOnTenMinutes()
	{
		Wire();

		IRenderedComponent<Maps> page = RenderPage();

		page.FindAll("fieldset.pin-expiry option")
			.Select(option => option.TextContent)
			.ShouldBe(new[] { "5 minutes", "10 minutes", "30 minutes", "1 hour", "2 hours", "6 hours" });

		page.Find("fieldset.pin-expiry select").GetAttribute("value").ShouldBe("10",
			"a device that has never chosen gets PinExpiry.Default, and the dropdown has to say so.");
	}

	[Fact]
	public async Task ChoosingHowLongPinsLast_StoresItOnThisDevice()
	{
		Wire();
		IDeviceSettings settings = Services.GetRequiredService<IDeviceSettings>();

		IRenderedComponent<Maps> page = RenderPage();
		await page.InvokeAsync(() => page.Find("fieldset.pin-expiry select").Change("30"));

		(await settings.GetAsync(PinExpiry.StorageKey)).ShouldBe("30",
			"the live map reads this key when it opens - nothing else carries the choice to it.");
		page.Find("fieldset.pin-expiry select").GetAttribute("value").ShouldBe("30");
	}

	/// <summary>
	/// The same rule the tile fields follow: a rider coming back to this screen finds their own
	/// answer selected rather than the app's default, which is the only way to tell what the map
	/// is currently doing.
	/// </summary>
	[Fact]
	public async Task ThePinExpiryDropdown_OpensOnWhatIsAlreadyStored()
	{
		Wire();
		IDeviceSettings settings = Services.GetRequiredService<IDeviceSettings>();
		await settings.SetAsync(PinExpiry.StorageKey, "360");

		IRenderedComponent<Maps> page = RenderPage();

		page.WaitForAssertion(
			() => page.Find("fieldset.pin-expiry select").GetAttribute("value").ShouldBe("360"),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// A value from a build that offered something this one does not - the store is on a phone we
	/// do not control, and the list is meant to be able to change. It lands on an offered value, so
	/// the dropdown cannot show one thing while the map does another.
	/// </summary>
	[Fact]
	public async Task APinExpiryThisBuildDoesNotOffer_OpensOnTheNearestOneItDoes()
	{
		Wire();
		IDeviceSettings settings = Services.GetRequiredService<IDeviceSettings>();
		await settings.SetAsync(PinExpiry.StorageKey, "20");

		IRenderedComponent<Maps> page = RenderPage();

		page.WaitForAssertion(
			() => page.Find("fieldset.pin-expiry select").GetAttribute("value").ShouldBe("30"),
			timeout: TimeSpan.FromSeconds(3));
	}
}
