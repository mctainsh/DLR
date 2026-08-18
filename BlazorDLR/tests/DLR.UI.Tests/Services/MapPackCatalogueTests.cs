using System.Net;
using System.Text;
using BlazorDLR.Shared.Services;
using DLR.UI.Tests.Fakes;

namespace DLR.UI.Tests.Services;

/// <summary>
/// Reading the list of offline map packs on offer (§4.2).
/// <para>
/// The catalogue is a JSON file on a web host, which makes every field in it a claim rather than a
/// fact — and one of them, the id, becomes a directory name on the phone. So the tests that matter
/// here are the ones about what does <em>not</em> get through: an id the pack store would refuse, a
/// URL that will not resolve, a body that is a login page. The happy path is one test; the rest of
/// this file is the boundary.
/// </para>
/// </summary>
public sealed class MapPackCatalogueTests
{
	/// <summary>Where the catalogue lives in these tests, and the base a relative pack URL resolves against.</summary>
	private static readonly Uri Address = new("https://packs.example.com/maps/catalogue.json");

	/// <summary>Roughly New South Wales — the ground one extract covers, as the catalogue states it.</summary>
	private const string DefaultBounds = """
		"bounds": { "minLatitude": -37.52, "minLongitude": 140.99, "maxLatitude": -28.15, "maxLongitude": 153.65 }
		""";

	/// <summary>
	/// A catalogue entry, written the way <c>Build-AuMapPacks.ps1</c> writes one.
	/// </summary>
	/// <param name="region">
	/// Omitted from the JSON entirely when null, because that is the catalogue on the host today —
	/// the field is newer than the packs riders have already downloaded from it.
	/// </param>
	/// <param name="bounds">
	/// The <c>bounds</c> member as JSON, or null to leave it out altogether — which is the catalogue
	/// on the host today, since the field is newer than the packs riders have downloaded from it.
	/// </param>
	private static string Entry(
		string id,
		string name,
		string url,
		long sizeBytes = 1024,
		int version = 1,
		string? region = null,
		string? bounds = DefaultBounds) =>
		$$"""
		{
			"id": "{{id}}",
			"name": "{{name}}",
			{{(region is null ? "" : $"\"region\": \"{region}\",")}}
			{{(bounds is null ? "" : bounds + ",")}}
			"minZoom": 0,
			"maxZoom": 14,
			"sizeBytes": {{sizeBytes}},
			"sha256": "745b01e7498ed6e24c89d3396c8cfc2f0bbc596dfa7193030e94e36ef180b6a3",
			"version": {{version}},
			"url": "{{url}}"
		}
		""";

	private static string Catalogue(params string[] entries) => "[" + string.Join(",", entries) + "]";

	private static MapPackCatalogue Build(StubHttpHandler handler) =>
		new(new HttpClient(handler), Address);

	[Fact]
	public async Task ThePublishedShapeIsRead()
	{
		StubHttpHandler handler = new(Catalogue(
			Entry("au-nsw", "New South Wales", "https://packs.example.com/au-nsw.v1.pmtiles", 351089645),
			Entry("au-tas", "Tasmania", "https://packs.example.com/au-tas.v1.pmtiles", 57935628)));

		MapPackCatalogueResult result = await Build(handler).ReadAsync();

		result.Problem.ShouldBeNull();
		result.Packs.Count.ShouldBe(2);

		MapPackOffer nsw = result.Packs.Single(pack => pack.Id == "au-nsw");
		nsw.Name.ShouldBe("New South Wales");
		nsw.SizeBytes.ShouldBe(351089645, "the number a traveller decides on before spending it.");
		nsw.Url.ShouldBe(new Uri("https://packs.example.com/au-nsw.v1.pmtiles"));
		MapPackDownloader.IsFetchable(nsw.Url).ShouldBeTrue();

		// The ground it covers, which is what the settings screen draws on the map picker (§4.2).
		nsw.Bounds.ShouldNotBeNull();
		nsw.Bounds!.Value.MinLatitude.ShouldBe(-37.52);
		nsw.Bounds.Value.MaxLongitude.ShouldBe(153.65);
	}

	/// <summary>
	/// The catalogue riders are fetching from today predates the field, so this is the ordinary case
	/// rather than the odd one. It costs the offer its place on the map picker and nothing else —
	/// the dropdowns still list it, and it still downloads.
	/// </summary>
	[Fact]
	public async Task AnEntryWithNoBounds_IsStillOffered()
	{
		StubHttpHandler handler = new(Catalogue(
			Entry("au-nsw", "New South Wales", "https://packs.example.com/au-nsw.v1.pmtiles", bounds: null)));

		MapPackCatalogueResult result = await Build(handler).ReadAsync();

		MapPackOffer nsw = result.Packs.Single();
		nsw.Bounds.ShouldBeNull();
		MapPackDownloader.IsFetchable(nsw.Url).ShouldBeTrue("a missing box costs the picker, not the map.");
	}

	/// <summary>
	/// Bounds are a publisher's claim like every other field here. One that does not describe a
	/// place is dropped rather than repaired: drawn as published it would be a box across the whole
	/// world on the map picker, answering a tap anywhere on it with the wrong region.
	/// </summary>
	[Theory]
	[InlineData(""" "bounds": { "minLatitude": -28.15, "minLongitude": 140.99, "maxLatitude": -37.52, "maxLongitude": 153.65 } """)]
	[InlineData(""" "bounds": { "minLatitude": -37.52, "minLongitude": 140.99, "maxLatitude": -28.15, "maxLongitude": 999.0 } """)]
	public async Task BoundsThatDescribeNoPlace_AreDropped(string bounds)
	{
		StubHttpHandler handler = new(Catalogue(
			Entry("au-nsw", "New South Wales", "https://packs.example.com/au-nsw.v1.pmtiles", bounds: bounds)));

		MapPackCatalogueResult result = await Build(handler).ReadAsync();

		result.Packs.Single().Bounds.ShouldBeNull("and the entry itself survives — see the remarks.");
	}

	/// <summary>
	/// PowerShell's <c>Set-Content -Encoding utf8</c> writes one, and the catalogue this ships
	/// pointed at has it. <c>Utf8JsonReader</c> treats a BOM as a malformed first token rather than
	/// skipping it, so without the three-byte check this is a catalogue that never loads and a
	/// message nobody could diagnose.
	/// </summary>
	[Fact]
	public async Task AByteOrderMarkDoesNotStopItBeingRead()
	{
		byte[] withMark = [.. "\uFEFF"u8, .. Encoding.UTF8.GetBytes(
			Catalogue(Entry("au-act", "Australian Capital Territory", "https://packs.example.com/au-act.v1.pmtiles")))];

		MapPackCatalogueResult result = await Build(new StubHttpHandler(withMark)).ReadAsync();

		result.Problem.ShouldBeNull();
		result.Packs.Single().Id.ShouldBe("au-act");
	}

	/// <summary>
	/// The id becomes a directory name on the phone, and <c>FileMapPackStore</c> refuses anything
	/// that is not a plain slug rather than cleaning it up. Applying the same rule here means a
	/// catalogue cannot offer a row that only fails once somebody taps it.
	/// </summary>
	[Theory]
	[InlineData("../../etc/passwd")]
	[InlineData("au nsw")]
	[InlineData("-au-nsw")]
	[InlineData("")]
	public async Task AnIdThePackStoreWouldRefuse_IsNotOffered(string id)
	{
		StubHttpHandler handler = new(Catalogue(
			Entry(id, "Somewhere", "https://packs.example.com/x.pmtiles"),
			Entry("au-nsw", "New South Wales", "https://packs.example.com/au-nsw.v1.pmtiles")));

		MapPackCatalogueResult result = await Build(handler).ReadAsync();

		result.Packs.Select(pack => pack.Id).ShouldBe(["au-nsw"],
			"a bad entry costs its own row and not the whole list.");
	}

	/// <summary>
	/// So a publisher can list a file beside the catalogue and move hosts without rewriting every
	/// entry. Resolved rather than concatenated — the base is a <see cref="Uri"/> and the rules for
	/// what a relative reference means are not ones to reimplement with string joins.
	/// </summary>
	[Fact]
	public async Task ARelativeUrlIsResolvedAgainstTheCatalogueItself()
	{
		StubHttpHandler handler = new(Catalogue(
			Entry("au-nsw", "New South Wales", "au-nsw.v1.pmtiles"),
			Entry("au-vic", "Victoria", "/packs/au-vic.v1.pmtiles")));

		MapPackCatalogueResult result = await Build(handler).ReadAsync();

		result.Packs.Single(pack => pack.Id == "au-nsw").Url
			.ShouldBe(new Uri("https://packs.example.com/maps/au-nsw.v1.pmtiles"));
		result.Packs.Single(pack => pack.Id == "au-vic").Url
			.ShouldBe(new Uri("https://packs.example.com/packs/au-vic.v1.pmtiles"), "and an absolute path from the root.");
	}

	/// <summary>
	/// Listed rather than dropped, unlike a bad id: the entry is legible, the rider can see the
	/// region exists, and the reason it cannot be fetched may be fixed by the time they look again.
	/// Tapping it costs a message and no connection — see <c>MapPackDownloaderTests</c>.
	/// </summary>
	[Fact]
	public async Task APlainHttpArchiveFromAnywhereElse_IsListedButCannotBeFetched()
	{
		StubHttpHandler handler = new(Catalogue(
			Entry("au-nsw", "New South Wales", "http://packs.example.com/au-nsw.v1.pmtiles")));

		MapPackOffer offer = (await Build(handler).ReadAsync()).Packs.Single();

		offer.Id.ShouldBe("au-nsw");
		MapPackDownloader.IsFetchable(offer.Url).ShouldBeFalse(
			"every cleartext host but one is blocked beneath this code by Android's network security " +
			"config and by iOS ATS.");
	}

	/// <summary>
	/// The one exception, and the reason the packs can be fetched at all today: the host serves a
	/// certificate for another domain, so both platform configs name it and this agrees with them.
	/// The three have to say the same thing — a host permitted in one and not the others is either
	/// a button that fails inside the platform or a download this app refuses to start.
	/// </summary>
	[Fact]
	public async Task APlainHttpArchiveFromTheHostThePlatformPermits_CanBeFetched()
	{
		StubHttpHandler handler = new(Catalogue(
			Entry("au-nsw", "New South Wales", $"http://{MapPackCatalogue.CleartextHost}/au-nsw.v1.pmtiles")));

		MapPackOffer offer = (await Build(handler).ReadAsync()).Packs.Single();

		MapPackDownloader.IsFetchable(offer.Url).ShouldBeTrue();
	}

	/// <summary>
	/// Scoped to the host, not to the scheme. A general "http is fine now" would outlive the
	/// certificate being fixed and would permit hosts the platform still blocks.
	/// </summary>
	[Theory]
	[InlineData("http://evil.example.com/au-nsw.v1.pmtiles")]
	[InlineData("http://pmtiles.securehub.net.evil.example.com/au-nsw.v1.pmtiles")]
	public void CleartextIsPermittedForOneHostAndNoOther(string url)
	{
		MapPackCatalogue.PermitsCleartext(new Uri(url)).ShouldBeFalse();
		MapPackDownloader.IsFetchable(new Uri(url)).ShouldBeFalse();
	}

	[Fact]
	public async Task AUrlThatIsNotOne_IsNotOffered()
	{
		StubHttpHandler handler = new(Catalogue(
			Entry("au-nsw", "New South Wales", "javascript:alert(1)"),
			Entry("au-vic", "Victoria", "")));

		(await Build(handler).ReadAsync()).Packs.ShouldBeEmpty();
	}

	[Fact]
	public async Task TheListIsAlphabeticalByWhatTheRiderReads()
	{
		StubHttpHandler handler = new(Catalogue(
			Entry("au-vic", "Victoria", "https://packs.example.com/v.pmtiles"),
			Entry("au-act", "Australian Capital Territory", "https://packs.example.com/a.pmtiles"),
			Entry("au-nsw", "New South Wales", "https://packs.example.com/n.pmtiles")));

		MapPackCatalogueResult result = await Build(handler).ReadAsync();

		result.Packs.Select(pack => pack.Name).ShouldBe(
			["Australian Capital Territory", "New South Wales", "Victoria"],
			"the catalogue's own order is the order the extracts were built in, which means nothing to anybody looking for their state.");
	}

	/// <summary>
	/// The country is the settings screen's first dropdown, and it comes from the publisher rather
	/// than from anything worked out here — a table on the phone saying which slug belongs to which
	/// country is a copy of the pack table that ships a release behind it.
	/// </summary>
	[Fact]
	public async Task ThePublishedCountryIsWhatAPackIsFiledUnder()
	{
		StubHttpHandler handler = new(Catalogue(
			Entry("eu-france-paris", "Paris and the north", "https://packs.example.com/p.pmtiles", region: "France"),
			Entry("in-far-east", "Bangladesh, Assam and Bhutan", "https://packs.example.com/b.pmtiles", region: "India")));

		MapPackCatalogueResult result = await Build(handler).ReadAsync();

		result.Packs.Single(pack => pack.Id == "eu-france-paris").Region.ShouldBe("France");
		result.Packs.Single(pack => pack.Id == "in-far-east").Region.ShouldBe("India",
			"the catalogue groups several countries under one heading, and only it knows which.");
	}

	/// <summary>
	/// The catalogue on the host predates the field, and it is the one riders are fetching from until
	/// it is rebuilt. Without a fallback every entry in it lands in one group and the first dropdown
	/// is a control with a single option in it.
	/// <para>
	/// Coarser than what a rebuild publishes, deliberately: the prefix is mapped to the largest thing
	/// it is certainly inside. Guessing France from <c>eu-france-paris</c> would be the client trying
	/// to be the pack table, which is what publishing the field exists to avoid.
	/// </para>
	/// </summary>
	[Theory]
	[InlineData("au-nsw", "Australia")]
	[InlineData("eu-france-paris", "Europe")]
	[InlineData("na-us-texas", "North America")]
	[InlineData("cn-north", "China")]
	[InlineData("zz-atlantis", MapPackCatalogue.UnknownRegion)]
	[InlineData("nowhere", MapPackCatalogue.UnknownRegion)]
	public async Task AnEntryWithNoCountry_FallsBackToWhatItsIdIsCertainlyInside(string id, string expected)
	{
		StubHttpHandler handler = new(Catalogue(
			Entry(id, "Somewhere", "https://packs.example.com/s.pmtiles")));

		(await Build(handler).ReadAsync()).Packs.Single().Region.ShouldBe(expected);
	}

	/// <summary>
	/// Both dropdowns read from this one list, so the order it comes back in is the order both of them
	/// show. A screen re-sorting a copy for each is two orderings that can drift apart.
	/// </summary>
	[Fact]
	public async Task TheListIsOrderedByCountryAndThenByWhatTheRiderReads()
	{
		StubHttpHandler handler = new(Catalogue(
			Entry("eu-france-paris", "Paris and the north", "https://packs.example.com/1.pmtiles", region: "France"),
			Entry("au-vic", "Victoria", "https://packs.example.com/2.pmtiles", region: "Australia"),
			Entry("eu-france-alsace", "Alsace and Lorraine", "https://packs.example.com/3.pmtiles", region: "France"),
			Entry("au-act", "Australian Capital Territory", "https://packs.example.com/4.pmtiles", region: "Australia")));

		MapPackCatalogueResult result = await Build(handler).ReadAsync();

		result.Packs.Select(pack => pack.Name).ShouldBe([
			"Australian Capital Territory",
			"Victoria",
			"Alsace and Lorraine",
			"Paris and the north",
		]);
	}

	[Fact]
	public async Task AnEntryWithNoName_FallsBackToItsId()
	{
		StubHttpHandler handler = new(Catalogue(Entry("au-nsw", "  ", "https://packs.example.com/n.pmtiles")));

		(await Build(handler).ReadAsync()).Packs.Single().Name.ShouldBe("au-nsw");
	}

	[Fact]
	public async Task AHostThatAnswersWithSomethingElse_ReportsItRatherThanThrowing()
	{
		// A captive portal's login page is the one a rider actually meets: they are on hotel Wi-Fi,
		// downloading a map for tomorrow, and every request answers 200 with HTML.
		StubHttpHandler handler = new("<!doctype html><title>Sign in</title>", "text/html");

		MapPackCatalogueResult result = await Build(handler).ReadAsync();

		result.Packs.ShouldBeEmpty();
		// Naming the host is what makes this actionable: the rider can tell a captive portal from a
		// server that is simply down.
		result.Problem.ShouldNotBeNull();
		result.Problem!.ShouldContain("packs.example.com");
	}

	[Fact]
	public async Task AFailedRequest_ReportsWhatTheServerSaid()
	{
		StubHttpHandler handler = new("") { Status = HttpStatusCode.NotFound };

		MapPackCatalogueResult result = await Build(handler).ReadAsync();

		result.Packs.ShouldBeEmpty();
		result.Problem.ShouldNotBeNull();
		result.Problem!.ShouldContain("404");
	}

	[Fact]
	public async Task AHostThatCannotBeReached_IsAProblemAndNotAnException()
	{
		// The commonest case by a distance, and the one the whole no-throw posture exists for: no
		// signal, which is the state a rider downloading an offline map is trying to prepare for.
		StubHttpHandler handler = new("") { Fails = new HttpRequestException("No such host is known.") };

		MapPackCatalogueResult result = await Build(handler).ReadAsync();

		result.Problem.ShouldNotBeNull();
		result.Problem!.ShouldContain("No such host is known.");
	}

	/// <summary>
	/// The one place a caller's cancellation must not be swallowed: it means the rider left the
	/// screen, which is not a failure to put in front of them.
	/// </summary>
	[Fact]
	public async Task ARiderLeavingTheScreen_IsNotReportedAsAFailure()
	{
		using CancellationTokenSource cancelled = new();
		await cancelled.CancelAsync();

		StubHttpHandler handler = new(Catalogue(Entry("au-nsw", "New South Wales", "https://packs.example.com/n.pmtiles")));

		await Should.ThrowAsync<OperationCanceledException>(
			async () => await Build(handler).ReadAsync(cancelled.Token));
	}
}
