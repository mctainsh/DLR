using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using BlazorDLR.Shared.Diagnostics;

namespace BlazorDLR.Shared.Services.Platform;

/// <summary>
/// Serves this device's map archives to the WebView over loopback HTTP, so MapLibre can range-read
/// a PMTiles file it could not otherwise address (§4.5, §13 Q26).
/// <para>
/// <strong>Hand-rolled rather than Kestrel or <c>HttpListener</c>.</strong> <c>HttpListener</c> is
/// not dependable on the mobile targets, and pulling ASP.NET Core into a MAUI head to serve one
/// file is a large dependency for a small job. What is actually needed is one route, <c>GET</c>,
/// <c>HEAD</c>, <c>OPTIONS</c> and <c>Range</c> - a surface small enough to write correctly and
/// keep in one file, and small enough to test.
/// </para>
/// <para>
/// <strong>It lives in the shared project even though only the phone binds it</strong>, because it
/// needs nothing but sockets and streams. That is not tidiness: it means the whole thing is
/// exercised by <c>dotnet test</c> against a real socket, rather than being the one component only
/// a device can prove.
/// </para>
/// <para>
/// <strong>Loopback only, with a per-run secret in the path.</strong> Binding to
/// <see cref="IPAddress.Loopback"/> keeps it off every network interface the phone has, so nothing
/// off the device can reach it at all. The secret closes the remaining hole, which is other apps
/// on the same phone: without it any of them could walk the port range and read whatever this one
/// had downloaded. It is regenerated per process, so a URL cannot outlive the run that issued it.
/// </para>
/// <para>
/// <strong>CORS is required, and is the part that is easy to miss.</strong> The WebView serves the
/// app from its own scheme, so every request here is cross-origin - and <c>Range</c> is not a
/// safelisted header, so the browser sends a preflight first. Without the headers below, MapLibre
/// gets an opaque failure and the map is blank with nothing in the console worth reading.
/// </para>
/// <para>
/// <strong>Not usable in a browser, and the attribute says so.</strong> This assembly compiles to
/// WebAssembly as well as into the MAUI head (§18.2), and a socket does not exist there - the APIs
/// are present in the browser BCL and throw when called. The type still <em>compiles</em> into the
/// WASM build, which is harmless because nothing there constructs it: both browser hosts bind
/// <see cref="UnavailableMapPackServer"/>. Marking it is what stops the platform analyzer warning
/// at every call site inside, and records the constraint for the next person who reaches for it.
/// </para>
/// </summary>
[UnsupportedOSPlatform("browser")]
public sealed class LoopbackMapPackServer : IMapPackServer, IAsyncDisposable
{
	/// <summary>
	/// How much of the file is moved per write. 64 KB is comfortably more than a PMTiles range
	/// request asks for in practice, so most responses are a single pass.
	/// </summary>
	private const int TransferBufferBytes = 64 * 1024;

	/// <summary>
	/// Cap on a request line plus headers. A legitimate request here is one line and three or four
	/// headers; anything larger is a client that has lost its way, and reading it unbounded is how
	/// a server hands its memory to whoever connects.
	/// </summary>
	private const int MaxRequestBytes = 8 * 1024;

	/// <summary>
	/// How many <see cref="TcpListener.AcceptTcpClientAsync(CancellationToken)"/> failures in a row
	/// before the listener is written off and a fresh port is bound.
	/// <para>
	/// One failure is not a dead server. A client that connects and resets before the accept
	/// completes surfaces here as a <see cref="SocketException"/> against a listener that is
	/// perfectly healthy - and MapLibre produces exactly that, in bursts, every time a style swap
	/// or a <c>map.remove()</c> aborts the twenty tile fetches that were in flight. Treating one of
	/// those as fatal is what this constant exists to prevent.
	/// </para>
	/// </summary>
	private const int MaxConsecutiveAcceptFailures = 8;

	/// <summary>
	/// How long to pause after a failed accept. Small enough to be invisible to a rider, large
	/// enough that a listener failing instantly cannot spin a core while it does it.
	/// </summary>
	private const int AcceptRetryDelayMs = 50;

	/// <summary>
	/// How long a health probe is given to complete before the listener behind it is written off.
	/// Generous for a loopback round trip that normally costs under a millisecond, because the
	/// cost of being wrong is a rebind the rider did not need.
	/// </summary>
	private const int ProbeTimeoutMs = 1500;

	/// <summary>
	/// The shortest gap between two lines about requests this server refused. A source that cannot
	/// be read fails per tile, so an unthrottled line would push the rest of the log out of the
	/// ring - see <see cref="ThrottledLog"/>, which counts what it swallows.
	/// </summary>
	private const int RefusalLogEveryMs = 2000;

	private readonly IMapPackStore _store;
	private readonly SemaphoreSlim _startGate = new(1, 1);
	private readonly CancellationTokenSource _stopping = new();

	/// <summary>Requests answered 404 - a pack this device does not hold, or a token from a previous run.</summary>
	private readonly ThrottledLog _refusals = new(RefusalLogEveryMs);

	/// <summary>Connections that failed part way through being served.</summary>
	private readonly ThrottledLog _failures = new(RefusalLogEveryMs);

	/// <summary>
	/// The path secret, generated once per process. 128 bits from the OS CSPRNG, base64url so it
	/// survives a URL untouched.
	/// </summary>
	private readonly string _token = Base64Url(RandomNumberGenerator.GetBytes(16));

	/// <summary>
	/// The listener currently accepting, read from the accept loop's thread as well as this one -
	/// which is why it is <c>volatile</c>. Identity matters: a loop unwinding from a listener that
	/// has already been replaced must not condemn its replacement.
	/// </summary>
	private volatile TcpListener? _listener;

	private Uri? _baseUri;
	private bool _disposed;

	/// <summary>How many URLs have been handed out. Only its uniqueness matters - see ResolveAsync.</summary>
	private int _resolves;

	/// <summary>
	/// Set when the accept loop gives up on a listener that is not shutting down - see
	/// <see cref="MaxConsecutiveAcceptFailures"/> - or when one fails the probe in
	/// <see cref="AnswersAsync"/>. It is what turns a dead port into a rebind on the next
	/// <see cref="ResolveAsync"/> rather than into a URL nothing answers.
	/// <para>
	/// <strong>This is the failure that produced "Load failed" on a map that had been working.</strong>
	/// The loop used to return on any <see cref="SocketException"/>, so one aborted connection -
	/// or the OS closing the socket while the app was in the background during a long pack
	/// download - stopped the server for the life of the process while <see cref="_baseUri"/>
	/// carried on handing out its address. Every offline pack then failed at the fetch, with
	/// nothing in the log to say the port had gone, and only a restart brought it back.
	/// </para>
	/// </summary>
	private volatile bool _listenerFailed;

	/// <summary>Creates a server over this device's archives.</summary>
	/// <param name="store">Where the archives are. The server holds no files of its own.</param>
	public LoopbackMapPackServer(IMapPackStore store) => _store = store;

	/// <inheritdoc />
	public bool IsSupported => _store.IsSupported;

	/// <inheritdoc />
	public async ValueTask<Uri?> ResolveAsync(string packId, CancellationToken cancellationToken = default)
	{
		if (!IsSupported || _disposed)
		{
			return null;
		}

		// Asked before the port is bound, so a device that does not hold the archive never starts a
		// listener for it. The store answers null for an unknown pack, and disposing the stream
		// immediately costs one open.
		await using (Stream? probe = await _store.OpenReadAsync(packId, cancellationToken))
		{
			if (probe is null)
			{
				// Worth a line: from the map's side this is indistinguishable from a rider who
				// never chose a pack - the source silently becomes OSM - and the two have very
				// different fixes.
				DiagnosticLog.Write($"Map pack server: this device holds no archive for '{packId}'; the map falls back to OSM.");
				return null;
			}
		}

		Uri? baseUri = await EnsureListeningAsync(cancellationToken);

		if (baseUri is null)
		{
			return null;
		}

		// The query is ignored here (see PackIdFrom) and exists for the reader on the other side.
		// pmtiles.js keys its archive cache on the URL and stores the pending header read in it
		// before knowing whether it resolves - nothing removes a rejected one, so once a read of
		// this archive has failed at the network layer, every later read of the same URL is served
		// that same rejection for the life of the page. A rebind onto the same port would come back
		// to a working socket and a map that still could not read it, which is why re-resolving has
		// to produce an address the reader has never seen.
		string nonce = Interlocked.Increment(ref _resolves).ToString(CultureInfo.InvariantCulture);

		return new Uri(baseUri, $"{_token}/{packId}.pmtiles?r={nonce}");
	}

	/// <summary>
	/// The address this server is answering on, or <c>null</c> when there is nothing behind it -
	/// never bound, disposed, or a listener the accept loop has written off.
	/// <para>
	/// The last of those is the point. A cached URL outliving the port it names is invisible from
	/// here and fatal at the map: MapLibre's fetch fails at the network layer with no status and no
	/// body, which is the "Load failed" a rider sees.
	/// </para>
	/// </summary>
	private Uri? Live => _baseUri is { } uri && !_listenerFailed && !_disposed ? uri : null;

	/// <summary>
	/// Binds the listener and starts accepting, once. Concurrent callers - several maps opening at
	/// the same moment - wait on one start rather than racing to bind two ports.
	/// <para>
	/// Also the recovery path: a listener the accept loop gave up on, or one that no longer answers,
	/// is replaced here rather than leaving the app with no offline map until it is restarted. The
	/// new port is a new URL, which is why <see cref="IMapPackServer.ResolveAsync"/> is documented
	/// as something to ask for each time rather than to store.
	/// </para>
	/// <para>
	/// <strong>An existing listener is proven rather than assumed</strong>, which is why there is no
	/// fast path around the gate. See <see cref="AnswersAsync"/> - a socket that has died quietly
	/// looks identical from here to one that is working.
	/// </para>
	/// </summary>
	private async ValueTask<Uri?> EnsureListeningAsync(CancellationToken cancellationToken)
	{
		try
		{
			await _startGate.WaitAsync(cancellationToken);
		}
		catch (ObjectDisposedException)
		{
			// The app is closing under a map that is still opening. Nothing to serve, and nothing
			// worth throwing at a page that is going away.
			return null;
		}

		try
		{
			if (_disposed)
			{
				return null;
			}

			if (Live is { } existing)
			{
				if (await AnswersAsync(existing, cancellationToken))
				{
					return existing;
				}

				DiagnosticLog.Write(
					$"Map pack server: {existing} accepted no probe within " +
					$"{ProbeTimeoutMs.ToString(CultureInfo.InvariantCulture)} ms - the listener has gone quiet " +
					"without ever failing an accept. Binding again.");

				_listenerFailed = true;
			}

			int previousPort = 0;

			// Reached only for a listener that has been written off - a healthy one returned above -
			// and both paths that write one off have already said so in the log.
			if (_listener is { } stopped)
			{
				previousPort = _baseUri?.Port ?? 0;

				try { stopped.Stop(); } catch (SocketException) { /* already down */ }

				_listener = null;
				_baseUri = null;
			}

			// Port 0: the OS picks a free one. Hard-coding a port would collide with whatever else
			// the phone is running, and the collision would be intermittent and unreproducible.
			//
			// A rebind asks for the port it had first, and that is worth a try rather than a
			// preference: a map already on screen is holding an archive URL with the old port in
			// it, and that URL is inside a style document nothing re-reads until the source
			// changes. Come back on the same port and it simply starts working again; come back on
			// a different one and it stays broken until something restyles it.
			TcpListener listener = Bind(previousPort);

			_listenerFailed = false;
			_listener = listener;
			_baseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");

			DiagnosticLog.Write($"Map pack server: listening on {_baseUri}.");

			// Deliberately not awaited: this is the accept loop, and it runs until disposal.
			_ = Task.Run(() => AcceptLoopAsync(listener, _stopping.Token), CancellationToken.None);

			return _baseUri;
		}
		catch (SocketException exception)
		{
			// A phone that will not give us a loopback port. Nothing to say to the rider that they
			// could act on - the map falls back to an online source, which is the same answer as a
			// device holding no archive - but it is the whole diagnosis for whoever reads the log.
			DiagnosticLog.WriteError("binding the map pack server to a loopback port", exception);
			return null;
		}
		finally
		{
			try { _startGate.Release(); } catch (ObjectDisposedException) { /* disposed while binding */ }
		}
	}

	/// <summary>
	/// A listener on <paramref name="preferredPort"/> if it can be had, and on whatever the OS
	/// offers otherwise. A port that is taken - by another process, or by the dead socket this is
	/// replacing while it lingers - is not a reason to fail: an offline map on a new port beats no
	/// offline map at all.
	/// </summary>
	/// <param name="preferredPort">The port to ask for, or <c>0</c> for any.</param>
	/// <returns>A listener that is already accepting.</returns>
	private static TcpListener Bind(int preferredPort)
	{
		if (preferredPort > 0)
		{
			TcpListener preferred = new(IPAddress.Loopback, preferredPort);

			try
			{
				// The bind happens here rather than in the constructor, which is why this is the
				// call inside the try.
				preferred.Start();
				return preferred;
			}
			catch (SocketException exception)
			{
				DiagnosticLog.Write(
					$"Map pack server: port {preferredPort.ToString(CultureInfo.InvariantCulture)} could not be taken again " +
					$"({DiagnosticLog.Summarise(exception)}); asking for any free port.");

				try { preferred.Stop(); } catch (SocketException) { /* never came up */ }
			}
		}

		TcpListener any = new(IPAddress.Loopback, 0);
		any.Start();
		return any;
	}

	/// <summary>
	/// Whether the listener behind <paramref name="baseUri"/> still answers, proven by a real
	/// request rather than inferred from the absence of an exception.
	/// <para>
	/// <strong>This is the failure the accept loop cannot see.</strong> A listening socket the OS
	/// tears down under a suspended app does not always fail an accept - often
	/// <c>AcceptTcpClientAsync</c> simply never completes again. Nothing throws, so nothing is
	/// counted and <see cref="Live"/> goes on handing out a dead port: a phone left overnight on a
	/// ride screen fails every tile with <c>TypeError: Load failed</c>, re-resolving and switching
	/// source both return the same address, and only a restart brings the map back.
	/// </para>
	/// <para>
	/// <c>OPTIONS</c> is answered before the path is looked up, so the probe proves the socket
	/// without opening an archive or logging a refusal. Any status line will do.
	/// </para>
	/// </summary>
	private async Task<bool> AnswersAsync(Uri baseUri, CancellationToken cancellationToken)
	{
		using CancellationTokenSource attempt =
			CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);

		attempt.CancelAfter(ProbeTimeoutMs);

		try
		{
			using TcpClient client = new();
			await client.ConnectAsync(IPAddress.Loopback, baseUri.Port, attempt.Token);

			await using NetworkStream stream = client.GetStream();

			byte[] request = Encoding.ASCII.GetBytes(
				$"OPTIONS /{_token}/probe HTTP/1.1\r\nHost: 127.0.0.1:{baseUri.Port.ToString(CultureInfo.InvariantCulture)}\r\n" +
				"Connection: close\r\n\r\n");

			await stream.WriteAsync(request, attempt.Token);

			byte[] reply = new byte[8];
			int read = await stream.ReadAtLeastAsync(reply, reply.Length, throwOnEndOfStream: false, attempt.Token);

			return read == reply.Length && Encoding.ASCII.GetString(reply).StartsWith("HTTP/1.", StringComparison.Ordinal);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// The caller gave up, not the server. Rebinding on the strength of that would replace a
			// working listener every time a map page was closed while it was opening.
			throw;
		}
		catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
		{
			return false;
		}
	}

	/// <summary>
	/// Accepts until the server is disposed, or until the listener stops being one.
	/// <para>
	/// <strong>A failed accept is not the end of the loop.</strong> It used to be, and that is the
	/// bug this method was rewritten around: a client that resets before its connection is accepted
	/// raises a <see cref="SocketException"/> here against a listener that is still perfectly good,
	/// and MapLibre raises a burst of exactly those every time a style swap aborts the tile fetches
	/// in flight. Returning on the first one stopped the server for the rest of the run while
	/// <see cref="_baseUri"/> carried on advertising it, so every later offline map failed at the
	/// fetch with nothing anywhere saying why.
	/// </para>
	/// <para>
	/// A listener that really has gone - the OS closing the socket under a backgrounded app is the
	/// realistic case - fails every time rather than once, so it is told apart by counting rather
	/// than by exception type, and answered by writing the listener off so the next map binds a new
	/// port.
	/// </para>
	/// </summary>
	private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
	{
		int consecutiveFailures = 0;

		while (!cancellationToken.IsCancellationRequested)
		{
			TcpClient client;

			try
			{
				client = await listener.AcceptTcpClientAsync(cancellationToken);
				consecutiveFailures = 0;
			}
			catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
			{
				if (cancellationToken.IsCancellationRequested || _disposed)
				{
					// Ordinary shutdown. Nothing to say and nothing to recover.
					return;
				}

				if (!ReferenceEquals(_listener, listener))
				{
					// This loop's listener has already been replaced, and the exception is almost
					// certainly the Stop() that replaced it. Writing off from here would condemn the
					// fresh listener the next resolve just bound, and the two would take turns
					// replacing each other for the rest of the run.
					return;
				}

				consecutiveFailures++;

				DiagnosticLog.Write(
					$"Map pack server: accept on {_baseUri} failed ({DiagnosticLog.Summarise(exception)}) - " +
					$"{consecutiveFailures} in a row of {MaxConsecutiveAcceptFailures} allowed.");

				// A disposed or unbound socket is not going to start working: it is the listener
				// itself that has gone, so there is nothing to be gained by trying again.
				if (exception is ObjectDisposedException or InvalidOperationException
					|| consecutiveFailures >= MaxConsecutiveAcceptFailures)
				{
					_listenerFailed = true;

					DiagnosticLog.Write(
						"Map pack server: this listener is finished. The next map to ask for a pack binds a fresh port.");

					return;
				}

				await Task.Delay(AcceptRetryDelayMs, CancellationToken.None);
				continue;
			}

			// One task per connection, and never awaited here: a slow reader must not stop the next
			// map from being served. Every failure inside is contained by the handler itself.
			_ = Task.Run(() => ServeAsync(client, cancellationToken), CancellationToken.None);
		}
	}

	/// <summary>
	/// Serves exactly one request and closes.
	/// <para>
	/// <strong>No keep-alive, deliberately.</strong> Persistent connections would mean tracking
	/// request boundaries and idle timeouts, which is most of the complexity of a real HTTP server
	/// and all of its failure modes. Over loopback a fresh connection costs microseconds, and every
	/// response says <c>Connection: close</c> so the client never waits for a second one.
	/// </para>
	/// </summary>
	private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
	{
		try
		{
			using (client)
			{
				client.NoDelay = true;

				await using NetworkStream stream = client.GetStream();

				if (await ReadRequestAsync(stream, cancellationToken) is not { } request)
				{
					await WriteStatusAsync(stream, 400, "Bad Request", cancellationToken);
					return;
				}

				await HandleAsync(stream, request, cancellationToken);
			}
		}
		catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
		{
			// A map that navigated away mid-tile, or the app closing. Ordinary one at a time, which
			// is why the line is throttled - but a map that draws nothing while these arrive by the
			// hundred is a different thing entirely, and the count is what shows the difference.
			_failures.Write($"Map pack server: a connection failed while being served ({DiagnosticLog.Summarise(exception)}).");
		}
	}

	private async Task HandleAsync(NetworkStream stream, Request request, CancellationToken cancellationToken)
	{
		// A preflight for the Range header. Answered before anything is looked up: it asks what the
		// server permits, not what it holds.
		if (request.Method == "OPTIONS")
		{
			await WriteStatusAsync(stream, 204, "No Content", cancellationToken);
			return;
		}

		if (request.Method is not ("GET" or "HEAD"))
		{
			await WriteStatusAsync(stream, 405, "Method Not Allowed", cancellationToken);
			return;
		}

		if (PackIdFrom(request.Path) is not { } packId)
		{
			// A wrong token and an unknown pack answer alike, so the port cannot be probed to learn
			// which of the two it was. The log is allowed to know the difference: a URL carrying
			// the wrong token is a map holding an address from a listener that has been replaced,
			// which is worth being able to read rather than guess at.
			_refusals.Write($"Map pack server: 404 for '{request.Method} {request.Path}' - that path is not this run's token and a pack.");
			await WriteStatusAsync(stream, 404, "Not Found", cancellationToken);
			return;
		}

		await using Stream? archive = await _store.OpenReadAsync(packId, cancellationToken);

		if (archive is null)
		{
			_refusals.Write($"Map pack server: 404 for pack '{packId}' - this device no longer holds it.");
			await WriteStatusAsync(stream, 404, "Not Found", cancellationToken);
			return;
		}

		long length = archive.Length;

		if (request.Range is not { } range)
		{
			await WriteBodyAsync(stream, 200, "OK", archive, 0, length, contentRange: null, request.Method == "HEAD", cancellationToken);
			return;
		}

		if (Resolve(range, length) is not { } resolved)
		{
			// A PMTiles reader asking past the end of the archive means the file is shorter than
			// its own directory says - a truncated download that passed the magic-byte check, or a
			// version swapped underneath a reader. Neither is visible from the map's side.
			_refusals.Write(
				$"Map pack server: 416 for pack '{packId}' - asked for bytes " +
				$"{range.First?.ToString(CultureInfo.InvariantCulture) ?? ""}-{range.Last?.ToString(CultureInfo.InvariantCulture) ?? ""} " +
				$"of an archive that is {length.ToString(CultureInfo.InvariantCulture)} bytes.");

			// 416 has to carry the real length, or a client cannot correct its next attempt.
			await WriteStatusAsync(stream, 416, "Range Not Satisfiable", cancellationToken,
				extraHeader: $"Content-Range: bytes */{length.ToString(CultureInfo.InvariantCulture)}");
			return;
		}

		(long start, long count) = resolved;

		await WriteBodyAsync(
			stream, 206, "Partial Content", archive, start, count,
			contentRange: $"bytes {start.ToString(CultureInfo.InvariantCulture)}-" +
				$"{(start + count - 1).ToString(CultureInfo.InvariantCulture)}/" +
				length.ToString(CultureInfo.InvariantCulture),
			headOnly: request.Method == "HEAD",
			cancellationToken);
	}

	/// <summary>
	/// The pack a request path names, or <c>null</c> when the path is not
	/// <c>/{token}/{packId}.pmtiles</c> with <em>this</em> run's token.
	/// </summary>
	private string? PackIdFrom(string path)
	{
		ReadOnlySpan<char> remaining = path.AsSpan();

		if (remaining.Length == 0 || remaining[0] != '/')
		{
			return null;
		}

		remaining = remaining[1..];

		if (!remaining.StartsWith(_token, StringComparison.Ordinal))
		{
			return null;
		}

		remaining = remaining[_token.Length..];

		if (remaining.Length == 0 || remaining[0] != '/')
		{
			return null;
		}

		remaining = remaining[1..];

		return remaining.EndsWith(".pmtiles", StringComparison.Ordinal)
			? remaining[..^".pmtiles".Length].ToString()
			: null;
	}

	/// <summary>
	/// Turns a parsed range into an offset and a count against a known length, or <c>null</c> when
	/// it cannot be satisfied.
	/// <para>
	/// Handles all three forms RFC 9110 allows: <c>a-b</c>, <c>a-</c> to the end, and <c>-n</c>
	/// meaning the last n bytes. The suffix form is the one hand-rolled parsers usually miss, and
	/// PMTiles readers do use it to find the footer.
	/// </para>
	/// </summary>
	private static (long Start, long Count)? Resolve((long? First, long? Last) range, long length)
	{
		if (length == 0)
		{
			return null;
		}

		if (range.First is not { } first)
		{
			// Suffix: the last `Last` bytes, clamped to the whole file.
			if (range.Last is not { } suffix || suffix <= 0)
			{
				return null;
			}

			long take = Math.Min(suffix, length);
			return (length - take, take);
		}

		if (first >= length)
		{
			return null;
		}

		long lastByte = range.Last is { } explicitLast ? Math.Min(explicitLast, length - 1) : length - 1;

		return lastByte < first ? null : (first, lastByte - first + 1);
	}

	// -- Wire reading and writing -------------------------------------------------------------

	private readonly record struct Request(string Method, string Path, (long? First, long? Last)? Range);

	/// <summary>
	/// Reads the request line and headers, stopping at the blank line. Never reads a body: nothing
	/// here accepts one, and a client that sends one has its connection closed under it.
	/// </summary>
	private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
	{
		byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxRequestBytes);

		try
		{
			int filled = 0;
			int headerEnd = -1;

			while (filled < MaxRequestBytes)
			{
				int read = await stream.ReadAsync(buffer.AsMemory(filled, MaxRequestBytes - filled), cancellationToken);

				if (read == 0)
				{
					break;
				}

				filled += read;
				headerEnd = IndexOfHeaderEnd(buffer.AsSpan(0, filled));

				if (headerEnd >= 0)
				{
					break;
				}
			}

			if (headerEnd < 0)
			{
				return null;
			}

			string text = Encoding.ASCII.GetString(buffer, 0, headerEnd);
			string[] lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

			if (lines.Length == 0)
			{
				return null;
			}

			string[] requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);

			if (requestLine.Length < 2)
			{
				return null;
			}

			(long? First, long? Last)? range = null;

			foreach (string line in lines.Skip(1))
			{
				if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
				{
					range = ParseRange(line["Range:".Length..].Trim());
				}
			}

			// Query strings are not used here, and a fragment never reaches a server. Trimming both
			// means a client that appends a cache-buster still resolves to the same pack.
			string path = requestLine[1];
			int cut = path.IndexOfAny(['?', '#']);

			return new Request(
				requestLine[0].ToUpperInvariant(),
				cut >= 0 ? path[..cut] : path,
				range);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}

	private static int IndexOfHeaderEnd(ReadOnlySpan<byte> buffer)
	{
		ReadOnlySpan<byte> terminator = "\r\n\r\n"u8;
		int index = buffer.IndexOf(terminator);
		return index;
	}

	/// <summary>
	/// Parses a single-range <c>bytes=</c> header. Multi-range requests are answered as if no range
	/// was asked for, which RFC 9110 permits - a multipart response is a lot of machinery for
	/// something no PMTiles reader sends.
	/// </summary>
	private static (long? First, long? Last)? ParseRange(string value)
	{
		const string Unit = "bytes=";

		if (!value.StartsWith(Unit, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		ReadOnlySpan<char> spec = value.AsSpan(Unit.Length).Trim();

		if (spec.Contains(','))
		{
			return null;
		}

		int dash = spec.IndexOf('-');

		if (dash < 0)
		{
			return null;
		}

		ReadOnlySpan<char> firstText = spec[..dash].Trim();
		ReadOnlySpan<char> lastText = spec[(dash + 1)..].Trim();

		long? first = null;
		long? last = null;

		if (!firstText.IsEmpty)
		{
			if (!long.TryParse(firstText, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
			{
				return null;
			}
			first = parsed;
		}

		if (!lastText.IsEmpty)
		{
			if (!long.TryParse(lastText, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
			{
				return null;
			}
			last = parsed;
		}

		return first is null && last is null ? null : (first, last);
	}

	/// <summary>
	/// The headers every response carries. The CORS trio is what makes this reachable from the
	/// WebView at all - see the type's remarks - and <c>Accept-Ranges</c> is what tells a PMTiles
	/// reader it may range-read rather than pulling the whole archive.
	/// </summary>
	private static void AppendCommonHeaders(StringBuilder headers) =>
		headers
			.Append("Accept-Ranges: bytes\r\n")
			.Append("Access-Control-Allow-Origin: *\r\n")
			.Append("Access-Control-Allow-Methods: GET, HEAD, OPTIONS\r\n")
			.Append("Access-Control-Allow-Headers: Range, If-Match, If-None-Match\r\n")
			// Without this the JS side can read the body but not the headers that describe it, and
			// a PMTiles reader needs Content-Range to know what it actually got.
			.Append("Access-Control-Expose-Headers: Accept-Ranges, Content-Range, Content-Length\r\n")
			.Append("Cache-Control: no-store\r\n")
			.Append("Connection: close\r\n");

	private static async Task WriteStatusAsync(
		NetworkStream stream,
		int status,
		string reason,
		CancellationToken cancellationToken,
		string? extraHeader = null)
	{
		StringBuilder headers = new();
		headers.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {reason}\r\n");
		AppendCommonHeaders(headers);

		if (extraHeader is not null)
		{
			headers.Append(extraHeader).Append("\r\n");
		}

		headers.Append("Content-Length: 0\r\n\r\n");

		await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), cancellationToken);
		await stream.FlushAsync(cancellationToken);
	}

	private async Task WriteBodyAsync(
		NetworkStream stream,
		int status,
		string reason,
		Stream archive,
		long start,
		long count,
		string? contentRange,
		bool headOnly,
		CancellationToken cancellationToken)
	{
		StringBuilder headers = new();
		headers.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {reason}\r\n");
		AppendCommonHeaders(headers);
		headers.Append("Content-Type: application/octet-stream\r\n");

		if (contentRange is not null)
		{
			headers.Append("Content-Range: ").Append(contentRange).Append("\r\n");
		}

		headers.Append(CultureInfo.InvariantCulture, $"Content-Length: {count}\r\n\r\n");

		await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), cancellationToken);

		if (headOnly || count == 0)
		{
			await stream.FlushAsync(cancellationToken);
			return;
		}

		archive.Seek(start, SeekOrigin.Begin);

		byte[] buffer = ArrayPool<byte>.Shared.Rent(TransferBufferBytes);

		try
		{
			long remaining = count;

			while (remaining > 0)
			{
				int wanted = (int)Math.Min(remaining, buffer.Length);
				int read = await archive.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken);

				if (read <= 0)
				{
					// The archive was truncated underneath us - a download replacing it, or a
					// deletion. The connection closes short, which the client reads as a failed
					// tile rather than a corrupt one, and which says nothing at all on the phone
					// unless it says it here.
					_failures.Write(
						$"Map pack server: the archive ended {remaining.ToString(CultureInfo.InvariantCulture)} bytes short of the " +
						$"{count.ToString(CultureInfo.InvariantCulture)} asked for at offset {start.ToString(CultureInfo.InvariantCulture)}.");
					break;
				}

				await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
				remaining -= read;
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}

		await stream.FlushAsync(cancellationToken);
	}

	/// <summary>
	/// One log line that will not flood the ring, however often it is written.
	/// <para>
	/// Every failure this server has is per tile, and a map draws twenty of those a pan. The line
	/// that matters is the first one and the count that follows it - a thousand copies of the same
	/// sentence would push the rest of the ride out of <see cref="DiagnosticLog"/> and take the
	/// evidence with it.
	/// </para>
	/// <para>
	/// <see cref="Environment.TickCount64"/> rather than a <c>TimeProvider</c>: this measures a gap
	/// between two log lines, not anything a test asserts on, and §10.4's rule exists for logic a
	/// fake clock has to be able to move.
	/// </para>
	/// </summary>
	/// <param name="everyMs">The shortest gap between two lines from this instance.</param>
	private sealed class ThrottledLog(int everyMs)
	{
		private readonly Lock _gate = new();

		private long _lastTicks;
		private bool _written;
		private int _suppressed;

		/// <summary>Writes <paramref name="message"/>, or counts it against the next line that gets through.</summary>
		/// <param name="message">What happened.</param>
		public void Write(string message)
		{
			int suppressed;

			lock (_gate)
			{
				long now = Environment.TickCount64;

				if (_written && now - _lastTicks < everyMs)
				{
					_suppressed++;
					return;
				}

				_lastTicks = now;
				_written = true;
				suppressed = _suppressed;
				_suppressed = 0;
			}

			DiagnosticLog.Write(suppressed > 0
				? $"{message} ({suppressed.ToString(CultureInfo.InvariantCulture)} more like it since the last line.)"
				: message);
		}
	}

	/// <summary>Base64url without padding - safe in a URL path with no escaping.</summary>
	private static string Base64Url(byte[] bytes) =>
		Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	/// <summary>
	/// Stops accepting and releases the port. The app closing is the ordinary caller; a rider who
	/// deletes their last archive keeps the listener until then, which costs a bound loopback port
	/// and nothing else.
	/// </summary>
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;

		if (_baseUri is { } served)
		{
			DiagnosticLog.Write($"Map pack server: stopping {served}.");
		}

		await _stopping.CancelAsync();

		try { _listener?.Stop(); } catch (SocketException) { /* already down */ }

		_listener = null;
		_baseUri = null;
		_stopping.Dispose();
		_startGate.Dispose();
	}
}
