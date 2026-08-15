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

	/// <summary>A catalogue entry, written the way <c>Build-AuMapPacks.ps1</c> writes one.</summary>
	private static string Entry(string id, string name, string url, long sizeBytes = 1024, int version = 1) =>
		$$"""
		{
			"id": "{{id}}",
			"name": "{{name}}",
			"bounds": {
				"minLatitude": -37.52,
				"minLongitude": 140.99,
				"maxLatitude": -28.15,
				"maxLongitude": 153.65
			},
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
		nsw.SizeBytes.ShouldBe(351089645, "the number a rider decides on before spending it.");
		nsw.Url.ShouldBe(new Uri("https://packs.example.com/au-nsw.v1.pmtiles"));
		MapPackDownloader.IsFetchable(nsw.Url).ShouldBeTrue();
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
