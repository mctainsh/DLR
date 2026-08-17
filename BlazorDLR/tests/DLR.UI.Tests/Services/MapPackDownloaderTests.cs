using System.Net;
using System.Text;
using BlazorDLR.Shared.Services;
using DLR.UI.Tests.Fakes;

namespace DLR.UI.Tests.Services;

/// <summary>
/// Fetching a PMTiles archive onto the device from a URL (§4.4).
/// <para>
/// The interesting cases are all failures, and they share a shape: a map pack is tens or hundreds
/// of megabytes over a phone connection, so <em>every</em> way a transfer can end badly is one a
/// rider will actually meet. The one that matters most is the server that ignores a
/// <c>Range</c> header — appending a whole file onto a partial one produces a corrupt archive of
/// exactly the expected length, which nothing downstream can detect.
/// </para>
/// </summary>
public sealed class MapPackDownloaderTests
{
	/// <summary>A minimal well-formed archive: the PMTiles v3 magic, then filler.</summary>
	private static byte[] Archive(int length = 64)
	{
		byte[] bytes = new byte[length];
		"PMTiles"u8.CopyTo(bytes);
		bytes[7] = 3;
		for (int i = 8; i < length; i++)
		{
			bytes[i] = (byte)i;
		}
		return bytes;
	}

	private static readonly Uri Source = new("https://example.com/sydney.pmtiles");

	private static MapPackDownloader Build(FakeMapPackStore store, StubHandler handler) =>
		new(store, new HttpClient(handler));

	[Fact]
	public async Task AWholeArchiveIsDownloadedAndCommitted()
	{
		byte[] archive = Archive();
		FakeMapPackStore store = new();
		StubHandler handler = new(archive);

		MapPackDownloadResult result = await Build(store, handler).DownloadAsync("sydney", Source);

		result.Succeeded.ShouldBeTrue(result.Message);
		(await store.OpenReadAsync("sydney")).ShouldNotBeNull("the archive is live once committed.");
		result.Sha256.ShouldNotBeNull("reported so it can be checked against a catalogue when one exists.");
		result.Sha256.Length.ShouldBe(64);
	}

	[Fact]
	public async Task AnInterruptedDownloadResumesRatherThanStartingAgain()
	{
		byte[] archive = Archive(64);
		FakeMapPackStore store = new();

		// First attempt: the connection drops after 20 bytes.
		StubHandler dropping = new(archive) { TruncateAfter = 20 };
		MapPackDownloadResult first = await Build(store, dropping).DownloadAsync("sydney", Source);

		first.Succeeded.ShouldBeFalse("a short read is a failure, but the bytes are kept.");
		store.Partial("sydney", 1)!.Length.ShouldBe(20);

		// Second attempt: the downloader must ask for the rest, not the whole thing.
		StubHandler resuming = new(archive);
		MapPackDownloadResult second = await Build(store, resuming).DownloadAsync("sydney", Source);

		resuming.LastRangeFrom.ShouldBe(20,
			"the point of keeping the partial is that a traveller on a phone plan does not pay twice.");
		second.Succeeded.ShouldBeTrue(second.Message);

		await using Stream? stored = await store.OpenReadAsync("sydney");
		using MemoryStream copy = new();
		await stored!.CopyToAsync(copy);
		copy.ToArray().ShouldBe(archive, "and the reassembled archive is byte-identical.");
	}

	[Fact]
	public async Task AServerThatIgnoresTheRangeHeader_StartsAgainRatherThanCorruptingTheArchive()
	{
		// The failure this test exists for: appending a whole file onto a partial one gives an
		// archive of exactly the expected length with 20 bytes of garbage at the front. Nothing
		// downstream can spot that — the length checks pass and the map silently fails to draw.
		byte[] archive = Archive(64);
		FakeMapPackStore store = new();

		await Build(store, new StubHandler(archive) { TruncateAfter = 20 }).DownloadAsync("sydney", Source);
		store.Partial("sydney", 1)!.Length.ShouldBe(20);

		StubHandler ignoring = new(archive) { IgnoreRange = true };
		MapPackDownloadResult result = await Build(store, ignoring).DownloadAsync("sydney", Source);

		result.Succeeded.ShouldBeTrue(result.Message);

		await using Stream? stored = await store.OpenReadAsync("sydney");
		using MemoryStream copy = new();
		await stored!.CopyToAsync(copy);
		copy.ToArray().ShouldBe(archive, "the partial was thrown away and the whole file written fresh.");
	}

	[Fact]
	public async Task SomethingThatIsNotAnArchive_IsRefusedRatherThanSaved()
	{
		// A login redirect, an HTML error page, a ZIP. Without the magic-byte check this commits,
		// the map goes blank, and the failure happens inside a WebView where nothing explains it.
		FakeMapPackStore store = new();
		StubHandler handler = new(Encoding.UTF8.GetBytes("<!doctype html><title>404</title>"));

		MapPackDownloadResult result = await Build(store, handler).DownloadAsync("sydney", Source);

		result.Succeeded.ShouldBeFalse();
		result.Message.ShouldContain("did not return a map pack");
		(await store.OpenReadAsync("sydney")).ShouldBeNull();
		store.Partial("sydney", 1).ShouldBeNull("and the rubbish is not left behind to be resumed.");
	}

	[Fact]
	public async Task AFailedLinkReportsWhatTheServerSaid()
	{
		FakeMapPackStore store = new();
		StubHandler handler = new([]) { Status = HttpStatusCode.NotFound };

		MapPackDownloadResult result = await Build(store, handler).DownloadAsync("sydney", Source);

		result.Succeeded.ShouldBeFalse();
		result.Message.ShouldContain("404");
	}

	[Fact]
	public async Task PlainHttpIsRefusedBeforeAConnectionIsOpened()
	{
		// The phones block cleartext to every host but loopback and the one named in
		// MapPackCatalogue.CleartextHost, so any other http:// link would fail with a platform error
		// the rider cannot act on. Said plainly instead.
		FakeMapPackStore store = new();
		StubHandler handler = new(Archive());

		MapPackDownloadResult result = await Build(store, handler)
			.DownloadAsync("sydney", new Uri("http://example.com/sydney.pmtiles"));

		result.Succeeded.ShouldBeFalse();
		result.Message.ShouldContain("https");
		handler.Requests.ShouldBe(0, "nothing should have left the device.");
	}

	/// <summary>
	/// The one cleartext host both platform configs name, because it serves a certificate for
	/// another domain. Without this the packs cannot be fetched at all today — the catalogue
	/// publishes <c>http://</c> URLs on that host, and refusing them made the screen offer a reason
	/// where a Download button should be.
	/// </summary>
	[Fact]
	public async Task PlainHttpFromTheOneHostThePlatformPermits_IsFetched()
	{
		FakeMapPackStore store = new();
		StubHandler handler = new(Archive());

		MapPackDownloadResult result = await Build(store, handler)
			.DownloadAsync("au-nsw", new Uri($"http://{MapPackCatalogue.CleartextHost}/au-nsw.v1.pmtiles"));

		result.Succeeded.ShouldBeTrue(result.Message);
		(await store.OpenReadAsync("au-nsw")).ShouldNotBeNull();
	}

	[Theory]
	[InlineData("")]
	[InlineData("-leading-hyphen")]
	[InlineData("has space")]
	[InlineData("has_underscore")]
	[InlineData("../escape")]
	public async Task ANameTheStoreWouldRefuse_IsCaughtBeforeTheDownload(string packId)
	{
		FakeMapPackStore store = new();
		StubHandler handler = new(Archive());

		MapPackDownloadResult result = await Build(store, handler).DownloadAsync(packId, Source);

		result.Succeeded.ShouldBeFalse();
		handler.Requests.ShouldBe(0,
			"telling somebody the name was wrong after three hundred megabytes would be a poor way to learn it.");
	}

	[Fact]
	public async Task AHostThatCannotStorePacks_SaysSoWithoutDownloading()
	{
		FakeMapPackStore store = new() { IsSupported = false };
		StubHandler handler = new(Archive());

		MapPackDownloadResult result = await Build(store, handler).DownloadAsync("sydney", Source);

		result.Succeeded.ShouldBeFalse();
		handler.Requests.ShouldBe(0);
	}

	[Fact]
	public async Task ARedownloadLandsBesideTheOldOne_AndOnlyReplacesItOnceWhole()
	{
		byte[] first = Archive(64);
		FakeMapPackStore store = new();
		await Build(store, new StubHandler(first)).DownloadAsync("sydney", Source);

		store.Versions["sydney"].ShouldBe(1);

		byte[] second = Archive(128);
		MapPackDownloadResult result = await Build(store, new StubHandler(second)).DownloadAsync("sydney", Source);

		result.Succeeded.ShouldBeTrue(result.Message);
		store.Versions["sydney"].ShouldBe(2,
			"a fresh download claims the next version so it never writes into the file a map is reading.");
	}

	[Fact]
	public async Task ProgressIsReported_AndEndsAtTheFullLength()
	{
		byte[] archive = Archive(1024);
		FakeMapPackStore store = new();
		List<MapPackProgress> reports = [];

		await Build(store, new StubHandler(archive))
			.DownloadAsync("sydney", Source, new Progress<MapPackProgress>(reports.Add));

		// Progress<T> posts asynchronously; the last report is what the screen settles on.
		await Task.Delay(50);

		reports.ShouldNotBeEmpty();
		reports[^1].BytesReceived.ShouldBe(archive.Length);
		reports[^1].TotalBytes.ShouldBe(archive.Length);
		reports[^1].Fraction.ShouldBe(1);
	}

	[Fact]
	public void ByteCountsReadAsAPersonWouldSayThem()
	{
		MapPackDownloader.Describe(512).ShouldBe("512 B");
		MapPackDownloader.Describe(27_477_036).ShouldBe("26.2 MB");
		MapPackDownloader.Describe(3L * 1024 * 1024 * 1024).ShouldBe("3 GB");
	}

	/// <summary>
	/// A fake tile host. Serves one byte array, honours <c>Range</c> unless told not to, and can cut
	/// the response short to stand in for a dropped connection.
	/// </summary>
	private sealed class StubHandler(byte[] content) : HttpMessageHandler
	{
		/// <summary>How many requests actually left the client.</summary>
		public int Requests { get; private set; }

		/// <summary>The <c>Range</c> start of the last request, or null when none was sent.</summary>
		public long? LastRangeFrom { get; private set; }

		/// <summary>Answer the whole file even when a range was asked for.</summary>
		public bool IgnoreRange { get; set; }

		/// <summary>Send only this many bytes of the body, then stop.</summary>
		public int? TruncateAfter { get; set; }

		/// <summary>What to answer with. Non-success skips the body.</summary>
		public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests++;
			LastRangeFrom = request.Headers.Range?.Ranges.FirstOrDefault()?.From;

			if (Status != HttpStatusCode.OK)
			{
				return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent([]) });
			}

			long from = IgnoreRange ? 0 : LastRangeFrom ?? 0;
			byte[] slice = content.Skip((int)from).ToArray();

			byte[] body = TruncateAfter is { } cut && cut < slice.Length ? slice.Take(cut).ToArray() : slice;

			HttpResponseMessage response = new(from > 0 && !IgnoreRange ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
			{
				Content = new ByteArrayContent(body),
			};

			// The declared length is the whole remaining slice even when the body is cut short —
			// which is exactly how a dropped connection looks from the client's side.
			response.Content.Headers.ContentLength = slice.Length;

			if (from > 0 && !IgnoreRange)
			{
				response.Content.Headers.ContentRange =
					new System.Net.Http.Headers.ContentRangeHeaderValue(from, content.Length - 1, content.Length);
			}

			return Task.FromResult(response);
		}
	}
}
