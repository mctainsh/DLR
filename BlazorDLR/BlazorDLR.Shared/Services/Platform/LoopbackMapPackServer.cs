using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace BlazorDLR.Shared.Services.Platform;

/// <summary>
/// Serves this device's map archives to the WebView over loopback HTTP, so MapLibre can range-read
/// a PMTiles file it could not otherwise address (§4.5, §13 Q26).
/// <para>
/// <strong>Hand-rolled rather than Kestrel or <c>HttpListener</c>.</strong> <c>HttpListener</c> is
/// not dependable on the mobile targets, and pulling ASP.NET Core into a MAUI head to serve one
/// file is a large dependency for a small job. What is actually needed is one route, <c>GET</c>,
/// <c>HEAD</c>, <c>OPTIONS</c> and <c>Range</c> — a surface small enough to write correctly and
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
/// app from its own scheme, so every request here is cross-origin — and <c>Range</c> is not a
/// safelisted header, so the browser sends a preflight first. Without the headers below, MapLibre
/// gets an opaque failure and the map is blank with nothing in the console worth reading.
/// </para>
/// <para>
/// <strong>Not usable in a browser, and the attribute says so.</strong> This assembly compiles to
/// WebAssembly as well as into the MAUI head (§18.2), and a socket does not exist there — the APIs
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

	private readonly IMapPackStore _store;
	private readonly SemaphoreSlim _startGate = new(1, 1);
	private readonly CancellationTokenSource _stopping = new();

	/// <summary>
	/// The path secret, generated once per process. 128 bits from the OS CSPRNG, base64url so it
	/// survives a URL untouched.
	/// </summary>
	private readonly string _token = Base64Url(RandomNumberGenerator.GetBytes(16));

	private TcpListener? _listener;
	private Uri? _baseUri;
	private bool _disposed;

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
				return null;
			}
		}

		Uri? baseUri = await EnsureListeningAsync(cancellationToken);

		return baseUri is null ? null : new Uri(baseUri, $"{_token}/{packId}.pmtiles");
	}

	/// <summary>
	/// Binds the listener and starts accepting, once. Concurrent callers — several maps opening at
	/// the same moment — wait on one start rather than racing to bind two ports.
	/// </summary>
	private async ValueTask<Uri?> EnsureListeningAsync(CancellationToken cancellationToken)
	{
		if (_baseUri is { } already)
		{
			return already;
		}

		await _startGate.WaitAsync(cancellationToken);
		try
		{
			if (_baseUri is { } raced)
			{
				return raced;
			}

			// Port 0: the OS picks a free one. Hard-coding a port would collide with whatever else
			// the phone is running, and the collision would be intermittent and unreproducible.
			TcpListener listener = new(IPAddress.Loopback, 0);
			listener.Start();

			_listener = listener;
			_baseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");

			// Deliberately not awaited: this is the accept loop, and it runs until disposal.
			_ = Task.Run(() => AcceptLoopAsync(listener, _stopping.Token), CancellationToken.None);

			return _baseUri;
		}
		catch (SocketException)
		{
			// A phone that will not give us a loopback port. Nothing to say to the rider that they
			// could act on — the map falls back to an online source, which is the same answer as a
			// device holding no archive.
			return null;
		}
		finally
		{
			_startGate.Release();
		}
	}

	private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			TcpClient client;

			try
			{
				client = await listener.AcceptTcpClientAsync(cancellationToken);
			}
			catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
			{
				// Disposal, or the listener going away underneath us. Either way the loop is over.
				return;
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
			// A map that navigated away mid-tile, or the app closing. Ordinary, and there is nobody
			// left to tell.
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
			// which of the two it was.
			await WriteStatusAsync(stream, 404, "Not Found", cancellationToken);
			return;
		}

		await using Stream? archive = await _store.OpenReadAsync(packId, cancellationToken);

		if (archive is null)
		{
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
	/// was asked for, which RFC 9110 permits — a multipart response is a lot of machinery for
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
	/// WebView at all — see the type's remarks — and <c>Accept-Ranges</c> is what tells a PMTiles
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

	private static async Task WriteBodyAsync(
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
					// The archive was truncated underneath us — a download replacing it, or a
					// deletion. The connection closes short, which the client reads as a failed
					// tile rather than a corrupt one.
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

	/// <summary>Base64url without padding — safe in a URL path with no escaping.</summary>
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

		await _stopping.CancelAsync();

		try { _listener?.Stop(); } catch (SocketException) { /* already down */ }

		_listener = null;
		_baseUri = null;
		_stopping.Dispose();
		_startGate.Dispose();
	}
}
