using System.Net;
using System.Net.Http.Headers;
using BlazorDLR.Shared.Services.Platform;
using DLR.UI.Tests.Fakes;

namespace DLR.UI.Tests.Services;

/// <summary>
/// The loopback server that lets MapLibre read a downloaded PMTiles archive (§4.5, §13 Q26).
/// <para>
/// <strong>These run against a real socket.</strong> That is the whole reason the server lives in
/// the shared project rather than the MAUI head: an HTTP server is a thing to get wrong in a dozen
/// small ways — a range off by one, a missing CORS header, a 416 with no length — and every one of
/// them surfaces on a phone as a blank map with nothing useful in the console. Here they surface
/// as a red test in <c>dotnet test</c>.
/// </para>
/// <para>
/// The archives are a few bytes each. The server only seeks and copies, so it cannot tell the
/// difference between these and a gigabyte of New South Wales.
/// </para>
/// </summary>
public sealed class LoopbackMapPackServerTests : IAsyncLifetime
{
	/// <summary>Sixteen bytes, each its own index — so an assertion on a range reads as its offsets.</summary>
	private static readonly byte[] Archive = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];

	private readonly FakeMapPackStore _store = new();
	private readonly LoopbackMapPackServer _server;
	private readonly HttpClient _http = new();

	public LoopbackMapPackServerTests()
	{
		_store.Add("au-nsw", Archive);
		_server = new LoopbackMapPackServer(_store);
	}

	public Task InitializeAsync() => Task.CompletedTask;

	public async Task DisposeAsync()
	{
		_http.Dispose();
		await _server.DisposeAsync();
	}

	private async Task<Uri> UrlAsync(string packId = "au-nsw")
	{
		Uri? url = await _server.ResolveAsync(packId);
		url.ShouldNotBeNull("the store holds this pack, so the server must be able to serve it.");
		return url;
	}

	private async Task<HttpResponseMessage> GetAsync(Uri url, string? range = null, HttpMethod? method = null)
	{
		using HttpRequestMessage request = new(method ?? HttpMethod.Get, url);

		if (range is not null)
		{
			request.Headers.TryAddWithoutValidation("Range", range);
		}

		return await _http.SendAsync(request);
	}

	[Fact]
	public async Task TheUrlIsLoopbackOnly()
	{
		Uri url = await UrlAsync();

		url.Host.ShouldBe("127.0.0.1",
			"bound to loopback so nothing off the device can reach a traveller's downloaded maps at all.");
		url.Scheme.ShouldBe("http", "which is why the platforms need an explicit cleartext allowance for it.");
		url.AbsolutePath.ShouldEndWith("/au-nsw.pmtiles");
	}

	[Fact]
	public async Task AWholeArchiveComesBackWithAcceptRanges()
	{
		using HttpResponseMessage response = await GetAsync(await UrlAsync());

		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		(await response.Content.ReadAsByteArrayAsync()).ShouldBe(Archive);
		response.Headers.AcceptRanges.ShouldContain("bytes",
			"without it a PMTiles reader pulls the whole archive instead of ranging into it.");
	}

	[Theory]
	// The three forms RFC 9110 allows. The suffix form is the one hand-rolled parsers miss, and it
	// is exactly what a PMTiles reader uses to find the footer at the end of the file.
	[InlineData("bytes=0-3", 0, 4)]
	[InlineData("bytes=4-7", 4, 4)]
	[InlineData("bytes=8-", 8, 8)]
	[InlineData("bytes=-5", 11, 5)]
	[InlineData("bytes=12-99", 12, 4)]  // clamped to the end rather than refused
	public async Task ARangeComesBackAsPartialContent(string range, int start, int count)
	{
		using HttpResponseMessage response = await GetAsync(await UrlAsync(), range);

		response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);

		byte[] body = await response.Content.ReadAsByteArrayAsync();
		body.ShouldBe(Archive.Skip(start).Take(count).ToArray());

		ContentRangeHeaderValue? contentRange = response.Content.Headers.ContentRange;
		contentRange.ShouldNotBeNull("a PMTiles reader needs Content-Range to know what it actually got.");
		contentRange.From.ShouldBe(start);
		contentRange.To.ShouldBe(start + count - 1);
		contentRange.Length.ShouldBe(Archive.Length);
	}

	[Fact]
	public async Task ARangePastTheEnd_Is416WithTheRealLength()
	{
		using HttpResponseMessage response = await GetAsync(await UrlAsync(), "bytes=99-200");

		response.StatusCode.ShouldBe(HttpStatusCode.RequestedRangeNotSatisfiable);
		response.Content.Headers.ContentRange?.Length.ShouldBe(Archive.Length,
			"a client cannot correct its next attempt without being told how long the file actually is.");
	}

	[Fact]
	public async Task CorsHeadersAreOnEveryResponse()
	{
		// The WebView serves the app from its own scheme, so every one of these is cross-origin.
		// Without the headers MapLibre gets an opaque failure and the map is blank.
		using HttpResponseMessage response = await GetAsync(await UrlAsync(), "bytes=0-1");

		response.Headers.GetValues("Access-Control-Allow-Origin").ShouldContain("*");
		response.Headers.GetValues("Access-Control-Expose-Headers").ShouldContain(
			value => value.Contains("Content-Range", StringComparison.Ordinal),
			1,
			"the JS side can read the body but not the headers describing it unless they are exposed.");
	}

	[Fact]
	public async Task ThePreflightForRangeIsAnswered()
	{
		// Range is not a CORS-safelisted header, so the browser sends OPTIONS first. An unanswered
		// preflight fails the real request before it is ever sent.
		using HttpResponseMessage response = await GetAsync(await UrlAsync(), method: HttpMethod.Options);

		response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		response.Headers.GetValues("Access-Control-Allow-Headers").ShouldContain(
			value => value.Contains("Range", StringComparison.Ordinal), 1);
	}

	[Fact]
	public async Task HeadReturnsTheLengthWithoutTheBody()
	{
		using HttpResponseMessage response = await GetAsync(await UrlAsync(), method: HttpMethod.Head);

		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		response.Content.Headers.ContentLength.ShouldBe(Archive.Length);
		(await response.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
	}

	[Fact]
	public async Task AWrongTokenIs404_AndSaysNothingMore()
	{
		Uri url = await UrlAsync();
		Uri forged = new(url, "/not-the-token/au-nsw.pmtiles");

		using HttpResponseMessage response = await GetAsync(forged);

		response.StatusCode.ShouldBe(HttpStatusCode.NotFound,
			"the secret is what stops another app on the phone walking the port range and reading " +
			"a traveller's downloaded maps. It must answer exactly as an unknown pack does, so the port " +
			"cannot be probed to learn which of the two it was.");
	}

	[Fact]
	public async Task APackTheDeviceDoesNotHold_HasNoUrlAtAll()
	{
		(await _server.ResolveAsync("au-vic")).ShouldBeNull(
			"which is what sends MapSourceState.Effective back to an online source.");
	}

	[Fact]
	public async Task APathTraversalAttemptIs404()
	{
		Uri url = await UrlAsync();
		Uri climbing = new(url, url.AbsolutePath.Replace("au-nsw", "..%2F..%2Fsecrets", StringComparison.Ordinal));

		using HttpResponseMessage response = await GetAsync(climbing);

		response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task AQueryStringDoesNotChangeWhichPackIsServed()
	{
		// A cache-buster appended by the renderer must still resolve to the same archive.
		Uri url = await UrlAsync();

		using HttpResponseMessage response = await GetAsync(new Uri(url + "?v=2"));

		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		(await response.Content.ReadAsByteArrayAsync()).ShouldBe(Archive);
	}

	[Fact]
	public async Task ManyRangesAtOnce_AreAllServedCorrectly()
	{
		// A pan asks for a screenful of tiles at once. Each connection is served on its own task,
		// and they share one store — if the handler held state across connections this is where
		// it would show.
		Uri url = await UrlAsync();

		HttpResponseMessage[] responses = await Task.WhenAll(
			Enumerable.Range(0, 12).Select(i => GetAsync(url, $"bytes={i}-{i}")));

		try
		{
			for (int i = 0; i < responses.Length; i++)
			{
				responses[i].StatusCode.ShouldBe(HttpStatusCode.PartialContent);
				(await responses[i].Content.ReadAsByteArrayAsync()).ShouldBe(new[] { (byte)i });
			}
		}
		finally
		{
			foreach (HttpResponseMessage response in responses)
			{
				response.Dispose();
			}
		}

		_store.OpenStreams.ShouldBe(0, "every archive stream the server opened must be disposed.");
	}

	[Fact]
	public async Task ResolvingTwice_ReusesOnePort()
	{
		Uri first = await UrlAsync();
		Uri second = await UrlAsync();

		second.Port.ShouldBe(first.Port, "several maps opening at once must not each bind a port.");
	}

	[Fact]
	public async Task AHostThatHoldsNoArchives_ServesNothing()
	{
		// The browser hosts' shape (§18.6), asserted through the same class so the two cannot drift.
		FakeMapPackStore empty = new() { IsSupported = false };
		await using LoopbackMapPackServer server = new(empty);

		server.IsSupported.ShouldBeFalse();
		(await server.ResolveAsync("au-nsw")).ShouldBeNull();
	}

	[Fact]
	public async Task AfterDisposal_NothingIsServed()
	{
		Uri url = await UrlAsync();

		await _server.DisposeAsync();

		await Should.ThrowAsync<HttpRequestException>(() => GetAsync(url));
	}
}
