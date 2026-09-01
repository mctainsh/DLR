using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using BlazorDLR.Shared.Services.Platform;
using DLR.UI.Tests.Fakes;

namespace DLR.UI.Tests.Services;

/// <summary>
/// The loopback server that lets MapLibre read a downloaded PMTiles archive (§4.5, §13 Q26).
/// <para>
/// <strong>These run against a real socket.</strong> That is the whole reason the server lives in
/// the shared project rather than the MAUI head: an HTTP server is a thing to get wrong in a dozen
/// small ways - a range off by one, a missing CORS header, a 416 with no length - and every one of
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
	/// <summary>Sixteen bytes, each its own index - so an assertion on a range reads as its offsets.</summary>
	private static readonly byte[] Archive = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];

	private readonly FakeMapPackStore _store = new();
	private readonly LoopbackMapPackServer _server;
	private readonly HttpClient _http = new();

	public LoopbackMapPackServerTests()
	{
		_store.Add("au-nsw", Archive);
		_server = new LoopbackMapPackServer(_store);
	}

	public ValueTask InitializeAsync() => ValueTask.CompletedTask;

	public async ValueTask DisposeAsync()
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
		// and they share one store - if the handler held state across connections this is where
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
	public async Task EachResolveHandsBackAUrlTheReaderHasNotSeen()
	{
		Uri first = await UrlAsync();
		Uri second = await UrlAsync();

		second.ShouldNotBe(first,
			"pmtiles.js caches a failed header read against the archive URL and never evicts it, so a " +
			"re-resolve onto the same address is served the old rejection however healthy the server is.");

		using HttpResponseMessage response = await GetAsync(second, "bytes=4-6");

		response.StatusCode.ShouldBe(HttpStatusCode.PartialContent, "and the fresh URL must still serve the pack.");
		(await response.Content.ReadAsByteArrayAsync()).ShouldBe(new byte[] { 4, 5, 6 });
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
	public async Task ConnectionsThatAreResetBeforeTheyAreServed_DoNotStopTheServer()
	{
		// The regression this exists for. Every style swap and every `map.remove()` aborts the tile
		// fetches that were in flight, and the WebView resets those connections - some of them
		// before the accept has completed, which surfaces on this side as a SocketException against
		// a listener that is perfectly healthy.
		//
		// The accept loop used to return on one of those. The port then stopped answering for the
		// rest of the run while the resolved URL carried on naming it, so every offline map
		// afterwards failed at the fetch with the browser's bare "Load failed" - no status, no
		// body, nothing in the log - and only restarting the app brought the map back. That is what
		// a rider sees as "downloading the second pack broke the first one".
		Uri url = await UrlAsync();

		for (int i = 0; i < 20; i++)
		{
			using TcpClient client = new();
			await client.ConnectAsync(IPAddress.Loopback, url.Port);

			// Zero-length linger: closing sends RST rather than FIN, which is what an aborted
			// fetch does and what a graceful close would not reproduce.
			client.LingerState = new LingerOption(true, 0);
		}

		using HttpResponseMessage response = await GetAsync(url, "bytes=4-6");

		response.StatusCode.ShouldBe(HttpStatusCode.PartialContent,
			"a burst of aborted connections must leave the server accepting.");
		(await response.Content.ReadAsByteArrayAsync()).ShouldBe(new byte[] { 4, 5, 6 });
	}

	[Fact]
	public async Task AListenerThatDiedWithoutFailingAnAccept_IsReplacedOnTheNextResolve()
	{
		// The overnight failure, reproduced. iOS tears the listening socket down under a suspended
		// app and AcceptTcpClientAsync does not throw - it simply never completes again. Nothing is
		// counted, so the server carries on handing out a port nothing answers: the log says the
		// pack "is being served from port 51277", every tile fails with the WebView's bare
		// "TypeError: Load failed", and re-resolving returns the identical dead URL. Switching to
		// OSM and back is no help either, and only restarting the app brings the map back.
		//
		// Stopping the listener is the only way to kill the socket from a test, and stopping it does
		// raise the accept failure the phone never gets - so that verdict is waited for and then put
		// back, which leaves exactly the state the phone was in.
		Uri dead = await UrlAsync();

		await SilenceTheListenerAsync();

		Uri fresh = await UrlAsync();

		using HttpResponseMessage response = await GetAsync(fresh, "bytes=4-6");

		response.StatusCode.ShouldBe(HttpStatusCode.PartialContent,
			"a resolve must prove the listener still answers rather than assume it, or a rider whose " +
			$"phone slept on {dead} never gets their offline map back without restarting the app.");
		(await response.Content.ReadAsByteArrayAsync()).ShouldBe(new byte[] { 4, 5, 6 });
	}

	/// <summary>
	/// Leaves <see cref="_server"/> believing it is listening on a port whose socket has gone - the
	/// one state that is invisible from inside the server, and the reason a resolve now probes.
	/// <para>
	/// Reaching for the private fields is the point rather than a shortcut: what is being set up is
	/// an internal inconsistency the public surface has no way to produce, and asserting the fields
	/// are there is what makes a rename break this test rather than quietly neuter it.
	/// </para>
	/// </summary>
	private async Task SilenceTheListenerAsync()
	{
		FieldInfo listenerField = typeof(LoopbackMapPackServer)
			.GetField("_listener", BindingFlags.Instance | BindingFlags.NonPublic)
			.ShouldNotBeNull("the server must still hold its listener in _listener.");

		FieldInfo failedField = typeof(LoopbackMapPackServer)
			.GetField("_listenerFailed", BindingFlags.Instance | BindingFlags.NonPublic)
			.ShouldNotBeNull("the server must still record a written-off listener in _listenerFailed.");

		listenerField.GetValue(_server).ShouldBeOfType<TcpListener>().Stop();

		// The accept loop notices this one, which a suspended phone's does not.
		for (int attempt = 0; attempt < 100 && !(bool)failedField.GetValue(_server)!; attempt++)
		{
			await Task.Delay(20);
		}

		failedField.SetValue(_server, false);
	}

	[Fact]
	public async Task AfterDisposal_NothingIsServed()
	{
		Uri url = await UrlAsync();

		await _server.DisposeAsync();

		await Should.ThrowAsync<HttpRequestException>(() => GetAsync(url));
	}
}
