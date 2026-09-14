using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using DLR.Core.Tracks;
using DLR.UI.Tests.Fakes;
using static DLR.UI.Tests.Fakes.MapPackFixtures;

namespace DLR.UI.Tests.State;

/// <summary>
/// Whether a rider is offered an offline map, and which one (§4.2).
/// <para>
/// The rules here are the whole of the app's "do not pester" policy, because every surface that
/// makes the offer asks this state rather than deciding for itself: a pack declined once stays
/// declined, a pack already in use is never suggested, and a browser is never offered an archive it
/// could not store (§18.6).
/// </para>
/// </summary>
public sealed class OfflineMapOfferStateTests
{
	/// <summary>
	/// The three things this state reads, kept together so a test can spell "the app was restarted"
	/// by building a second state over the same device.
	/// </summary>
	private sealed record Device(IDeviceSettings Settings, FakeMapPackStore Packs, MapSourceState Sources);

	/// <summary>Wires a phone. <paramref name="phone"/> false is a browser, which can hold nothing (§18.6).</summary>
	private static (OfflineMapOfferState Offers, Device Device) Wire(
		bool phone = true,
		IDeviceSettings? settings = null,
		FakeMapPackStore? store = null)
	{
		settings ??= new InMemoryDeviceSettings();
		FakeMapPackStore packs = store ?? new FakeMapPackStore();
		packs.IsSupported = phone;

		(OfflineMapOfferState offers, MapSourceState sources) = MapPackFixtures.BuildOfferState(settings, packs);

		return (offers, new Device(settings, packs, sources));
	}

	/// <summary>
	/// The most specific area containing a point is the one somebody means, and it is the smaller
	/// download - the rule the map picker already states (§4.2), applied where nobody pointed at
	/// anything.
	/// </summary>
	[Fact]
	public async Task WhereTwoPacksOverlap_TheSmallerIsOffered()
	{
		(OfflineMapOfferState offers, _) = Wire();

		OfflineMapSuggestion? suggestion = await offers.SuggestAsync(SydneyLatitude, SydneyLongitude);

		suggestion.ShouldNotBeNull();
		suggestion.Offer.Id.ShouldBe("au-nsw", "Australia contains New South Wales, and is seven times the download.");
		suggestion.IsOnDevice.ShouldBeFalse();
	}

	/// <summary>
	/// A pack the phone is already carrying beats a smaller one it is not. This is the one case in
	/// the whole feature where the fix costs nothing at all, and offering the download instead would
	/// ask a rider for 300 MB they have already spent.
	/// </summary>
	[Fact]
	public async Task APackAlreadyOnThePhone_BeatsASmallerDownload()
	{
		FakeMapPackStore packs = new();
		packs.Add("au-all", MapPackFixtures.Archive);

		(OfflineMapOfferState offers, _) = Wire(store: packs);

		OfflineMapSuggestion? suggestion = await offers.SuggestAsync(SydneyLatitude, SydneyLongitude);

		suggestion.ShouldNotBeNull();
		suggestion.Offer.Id.ShouldBe("au-all");
		suggestion.IsOnDevice.ShouldBeTrue();
		suggestion.ActionText.ShouldBe("Use this map", "there is nothing to download.");
	}

	/// <summary>
	/// The pack the rider is already drawing with is never suggested. A banner telling somebody to
	/// use the map they are using is how an app teaches people to stop reading its banners.
	/// </summary>
	[Fact]
	public async Task ThePackAlreadyInUse_IsNotOffered()
	{
		FakeMapPackStore packs = new();
		packs.Add("au-nsw", MapPackFixtures.Archive);

		(OfflineMapOfferState offers, Device device) = Wire(store: packs);
		await device.Sources.SetAsync(MapSource.OfflinePack("au-nsw"));

		OfflineMapSuggestion? suggestion = await offers.SuggestAsync(SydneyLatitude, SydneyLongitude);

		suggestion.ShouldNotBeNull("Australia still covers Sydney and is not the one in use.");
		suggestion.Offer.Id.ShouldBe("au-all");
	}

	/// <summary>Ground no published extract covers is answered with silence, not with a nearest guess.</summary>
	[Fact]
	public async Task GroundNoPackCovers_IsOfferedNothing()
	{
		(OfflineMapOfferState offers, _) = Wire();

		(await offers.SuggestAsync(51.5, -0.12)).ShouldBeNull("London is in none of these three.");
	}

	/// <summary>
	/// Declining is remembered, and remembered across a restart - which is the whole of the "asked
	/// once" promise. It goes in a device store because a browser and a phone are different devices
	/// with different answers (§18.6).
	/// </summary>
	[Fact]
	public async Task APackTurnedDown_IsNotOfferedAgainAfterARestart()
	{
		InMemoryDeviceSettings device = new();

		(OfflineMapOfferState offers, _) = Wire(settings: device);
		await offers.LoadAsync();
		await offers.DeclineAsync("au-nsw");

		(await offers.SuggestAsync(SydneyLatitude, SydneyLongitude))!.Offer.Id
			.ShouldBe("au-all", "the next-best pack is still worth saying.");

		(OfflineMapOfferState relaunched, _) = Wire(settings: device);

		relaunched.IsDeclined("au-nsw").ShouldBeFalse("nothing has read the device yet.");
		await relaunched.LoadAsync();
		relaunched.IsDeclined("au-nsw").ShouldBeTrue();

		(await relaunched.SuggestAsync(SydneyLatitude, SydneyLongitude))!.Offer.Id.ShouldBe("au-all");
	}

	/// <summary>
	/// A dead zone is remembered so the offer can be made where it can be acted on. A phone with no
	/// signal cannot fetch 300 MB, so the screen that discovers the problem is the worst place to
	/// offer the fix.
	/// </summary>
	[Fact]
	public async Task AMapThatWentBlank_IsOfferedAPackOnTheNextLaunch()
	{
		InMemoryDeviceSettings device = new();

		(OfflineMapOfferState offers, _) = Wire(settings: device);
		await offers.RecordMissAsync(SydneyLatitude, SydneyLongitude);

		(OfflineMapOfferState relaunched, _) = Wire(settings: device);

		await relaunched.PrimeMissedAsync();

		relaunched.Missed.ShouldNotBeNull();
		relaunched.Missed.Offer.Id.ShouldBe("au-nsw");
	}

	/// <summary>A device whose map has never failed is asked nothing and offered nothing.</summary>
	[Fact]
	public async Task ADeviceThatNeverLostItsMap_IsOfferedNothingOnHome()
	{
		(OfflineMapOfferState offers, _) = Wire();

		await offers.PrimeMissedAsync();

		offers.Missed.ShouldBeNull();
	}

	/// <summary>
	/// Taking the offer does both halves: the archive lands, and every map in the app is pointed at
	/// it. Downloading without selecting is the failure §4.2 already had, where a several-hundred
	/// megabyte transfer appeared to have done nothing.
	/// </summary>
	[Fact]
	public async Task TakingTheOffer_DownloadsThePackAndDrawsWithIt()
	{
		(OfflineMapOfferState offers, Device device) = Wire();

		OfflineMapSuggestion suggestion = (await offers.SuggestAsync(SydneyLatitude, SydneyLongitude))!;

		(await offers.TakeAsync(suggestion)).ShouldBeTrue();

		device.Packs.Versions.ShouldContainKey("au-nsw");
		device.Sources.Chosen.Kind.ShouldBe(MapSourceKind.Offline);
		device.Sources.Chosen.PackId.ShouldBe("au-nsw");
	}

	/// <summary>
	/// A pack already on the phone is selected without fetching anything. The offer said "Use this
	/// map", and it has to mean it - a second download of an archive the device is holding is the
	/// most expensive possible way to change a setting.
	/// </summary>
	[Fact]
	public async Task TakingAPackAlreadyHere_SelectsItWithoutDownloading()
	{
		FakeMapPackStore packs = new();
		packs.Add("au-nsw", MapPackFixtures.Archive, version: 7);

		(OfflineMapOfferState offers, Device device) = Wire(store: packs);

		OfflineMapSuggestion suggestion = (await offers.SuggestAsync(SydneyLatitude, SydneyLongitude))!;
		suggestion.IsOnDevice.ShouldBeTrue();

		(await offers.TakeAsync(suggestion)).ShouldBeTrue();

		device.Sources.Chosen.PackId.ShouldBe("au-nsw");
		device.Packs.Versions["au-nsw"].ShouldBe(7, "nothing was fetched over the top of it.");
	}

	/// <summary>
	/// And it clears the dead zone it answered, so the Home strip does not go on offering a pack the
	/// rider has already taken.
	/// </summary>
	[Fact]
	public async Task TakingTheOffer_ForgetsTheDeadZoneItAnswered()
	{
		(OfflineMapOfferState offers, _) = Wire();

		await offers.RecordMissAsync(SydneyLatitude, SydneyLongitude);

		await offers.PrimeMissedAsync();
		await offers.TakeAsync(offers.Missed!);

		offers.MissedPoint.ShouldBeNull();
		offers.Missed.ShouldBeNull();
	}

	/// <summary>
	/// A ride's ground is a box, not a point, and a pack holding all of it beats one holding the
	/// middle - the map is not meant to go blank on the way (§5.4).
	/// </summary>
	[Fact]
	public async Task AnAdventureSpanningTwoStates_IsOfferedThePackHoldingAllOfIt()
	{
		(OfflineMapOfferState offers, _) = Wire();

		// Sydney to the middle of the continent: inside Australia, out of New South Wales.
		TrackBounds crossCountry = new(-33.87, 133.00, -25.00, 151.21);

		OfflineMapSuggestion? suggestion = await offers.SuggestAsync(crossCountry);

		suggestion.ShouldNotBeNull();
		suggestion.Offer.Id.ShouldBe("au-all", "New South Wales holds the eastern end and nothing else.");
	}

	/// <summary>
	/// A ride wholly inside one state still gets the smaller of the two packs that hold it, so the
	/// box overload does not quietly become "always suggest the continent".
	/// </summary>
	[Fact]
	public async Task AnAdventureInsideOneState_IsOfferedThatState()
	{
		(OfflineMapOfferState offers, _) = Wire();

		TrackBounds blueMountains = new(-33.90, 150.20, -33.60, 150.60);

		(await offers.SuggestAsync(blueMountains))!.Offer.Id.ShouldBe("au-nsw");
	}

	/// <summary>
	/// The browser is offered nothing, and - the part that matters on a metered connection - the
	/// catalogue is never even fetched. It is a host with nowhere to put an archive (§18.6), so
	/// every request spent looking for one is spent on a list no screen there will render.
	/// </summary>
	[Fact]
	public async Task ABrowser_IsOfferedNothingAndFetchesNoCatalogue()
	{
		StubHttpHandler catalogue = new(MapPackFixtures.Catalogue);
		InMemoryDeviceSettings settings = new();
		FakeMapPackStore packs = new() { IsSupported = false };

		(OfflineMapOfferState offers, _) = MapPackFixtures.BuildOfferState(settings, packs, catalogue);

		offers.IsSupported.ShouldBeFalse();
		(await offers.SuggestAsync(SydneyLatitude, SydneyLongitude)).ShouldBeNull();

		await offers.PrimeMissedAsync();
		offers.Missed.ShouldBeNull();

		await offers.RecordMissAsync(SydneyLatitude, SydneyLongitude);
		offers.MissedPoint.ShouldBeNull("a browser has no dead zone worth remembering.");

		catalogue.Requests.ShouldBe(0);
	}

	/// <summary>
	/// A catalogue that will not answer is silence rather than an error. Every surface that makes
	/// this offer is a screen about something else, and none of them is the place to report on a
	/// host the rider never asked about.
	/// </summary>
	[Fact]
	public async Task ACatalogueThatCannotBeRead_OffersNothingAndDoesNotThrow()
	{
		StubHttpHandler unreachable = new("") { Fails = new HttpRequestException("no route to host") };

		(OfflineMapOfferState offers, _) = MapPackFixtures.BuildOfferState(
			new InMemoryDeviceSettings(), new FakeMapPackStore(), unreachable);

		(await offers.SuggestAsync(SydneyLatitude, SydneyLongitude)).ShouldBeNull();

		// And left alone afterwards. This caller is a map that re-asks as the rider moves, so a host
		// that will not answer must not be asked once per pan for the rest of the ride.
		(await offers.SuggestAsync(-42.88, 147.33)).ShouldBeNull();

		unreachable.Requests.ShouldBe(1);
	}

	/// <summary>
	/// Riding on through a dead zone rewrites nothing. Without this, writing to the device store
	/// would be the only thing the recorder did for the length of a ride out of coverage.
	/// </summary>
	[Fact]
	public async Task PanningInsideTheSameDeadZone_DoesNotRewriteTheDevice()
	{
		CountingDeviceSettings device = new();

		(OfflineMapOfferState offers, _) = Wire(settings: device);

		await offers.RecordMissAsync(SydneyLatitude, SydneyLongitude);
		int afterFirst = device.Writes;

		await offers.RecordMissAsync(SydneyLatitude + 0.001, SydneyLongitude + 0.001);

		device.Writes.ShouldBe(afterFirst, "that is the same blank patch, a few hundred metres on.");

		await offers.RecordMissAsync(-42.88, 147.33);
		device.Writes.ShouldBe(afterFirst + 1, "Hobart is a different dead zone.");
	}

}
