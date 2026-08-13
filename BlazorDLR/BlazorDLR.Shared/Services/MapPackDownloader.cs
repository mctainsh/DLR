using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace BlazorDLR.Shared.Services;

/// <summary>How a download ended.</summary>
/// <param name="PackId">Which archive.</param>
/// <param name="Succeeded">Whether it is now on the device and readable.</param>
/// <param name="Message">What to put in front of the rider — the reason on a failure, the size on a success.</param>
/// <param name="Sha256">
/// The archive's checksum, lowercase hex, when one was computed. Nothing verifies it today: a
/// rider pasting a URL has no catalogue to check it against. It is reported so they can, and so
/// the server-catalogue path (§4.2) has the value it will compare.
/// </param>
public sealed record MapPackDownloadResult(string PackId, bool Succeeded, string Message, string? Sha256 = null);

/// <summary>How far a download has got.</summary>
/// <param name="PackId">Which archive.</param>
/// <param name="BytesReceived">Bytes on the device, including any resumed from a previous attempt.</param>
/// <param name="TotalBytes">What the server said the whole thing is, or <c>null</c> when it would not say.</param>
public readonly record struct MapPackProgress(string PackId, long BytesReceived, long? TotalBytes)
{
	/// <summary>0–1, or <c>null</c> when the total is unknown and only a byte count can be shown.</summary>
	public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1) : null;
}

/// <summary>
/// Fetches a PMTiles archive from a URL onto this device (§4.4).
/// <para>
/// <strong>Any HTTPS URL, not a catalogue.</strong> The server-side catalogue described in
/// <c>Documentation/offline-maps-plan.md §4.2</c> is still the plan, but it is not what makes this
/// useful on day one: a rider — or a ride organiser — can put an extract on any web host and hand
/// the link round, which is also the only way to test the offline path before that catalogue
/// exists. The catalogue, when it lands, hands this method a URL exactly like a person does.
/// </para>
/// <para>
/// <strong>It owns its own <see cref="HttpClient"/>, and that is a security decision rather than a
/// convenience.</strong> The client every host registers carries <c>BearerAuthHandler</c> and is
/// pointed at the DLR API (§18.5). Reusing it here would attach the rider's access token to a
/// request aimed at whatever host they pasted — handing their session to a stranger's web server.
/// This one has no handler, no base address and no credentials.
/// </para>
/// <para>
/// <strong>Resumable, because the file is big and the connection is a phone.</strong> A restart
/// asks the store how much is already there and requests the rest with a <c>Range</c> header. A
/// server that ignores it and answers <c>200</c> is handled explicitly — appending a whole file
/// onto a partial one produces a corrupt archive of plausible length, which is the worst possible
/// outcome and the easiest to write by accident.
/// </para>
/// </summary>
public sealed class MapPackDownloader : IDisposable
{
	/// <summary>
	/// Read size. Large enough that a 300 MB archive is not thirty thousand progress callbacks,
	/// small enough that the bar moves while the rider is watching it.
	/// </summary>
	private const int BufferBytes = 256 * 1024;

	/// <summary>
	/// How often progress is reported, in bytes. Independent of <see cref="BufferBytes"/> so the
	/// two can be tuned apart — the buffer is about throughput, this is about a UI that re-renders.
	/// </summary>
	private const long ProgressEveryBytes = 512 * 1024;

	private readonly IMapPackStore _store;
	private readonly HttpClient _http;

	/// <summary>
	/// Creates a downloader over this device's pack store and a client to fetch with.
	/// <para>
	/// <strong>One constructor, and the client is not optional — that is the safety property.</strong>
	/// An earlier version offered a convenience overload taking only the store and building its own
	/// client. Because every host also registers an <see cref="HttpClient"/>, and the container
	/// picks the constructor with the most parameters it can satisfy, DI quietly chose the
	/// <em>other</em> one and handed this the app's client — the one carrying
	/// <c>BearerAuthHandler</c>. Every pack download would have attached the rider's access token to
	/// a request aimed at a host they pasted into a text box.
	/// </para>
	/// <para>
	/// Making the client an explicit argument means no host can register this without saying what
	/// it fetches with. <see cref="CreateCredentialFreeClient"/> is the answer they all use.
	/// </para>
	/// </summary>
	/// <param name="store">Where the archive lands.</param>
	/// <param name="http">The client to fetch with. Must carry no credentials — see the type's remarks.</param>
	public MapPackDownloader(IMapPackStore store, HttpClient http)
	{
		_store = store;
		_http = http;
	}

	/// <summary>
	/// A client with no handler, no base address and no credentials — what a pack download must go
	/// out on, because the URL comes from a text box and points wherever the rider says.
	/// <para>
	/// The timeout is generous on purpose: this is a large file on a phone connection, and the
	/// timeout that matters is the socket's rather than the whole transfer's. A ride's worth of map
	/// should not fail at the ninety-second mark because a default said so.
	/// </para>
	/// </summary>
	public static HttpClient CreateCredentialFreeClient() => new() { Timeout = TimeSpan.FromHours(2) };

	/// <summary>
	/// Downloads <paramref name="url"/> into the store as <paramref name="packId"/>, resuming a
	/// previous attempt if one is on the device.
	/// <para>
	/// Never throws for an ordinary failure — a dead link, a full disk, a rider who cancelled all
	/// come back as a <see cref="MapPackDownloadResult"/> with something to put on screen. A screen
	/// that has to catch exceptions to tell somebody a URL was wrong is a screen that will
	/// eventually forget to.
	/// </para>
	/// </summary>
	/// <param name="packId">
	/// What to call it on this device. Must be a slug the store accepts — see
	/// <see cref="IsUsablePackId"/>, which the settings screen checks before offering the button.
	/// </param>
	/// <param name="url">Where to fetch it from. HTTPS only.</param>
	/// <param name="progress">Told roughly every half megabyte, and once at the end.</param>
	/// <param name="cancellationToken">Cancelling leaves the partial file for a later resume.</param>
	public async Task<MapPackDownloadResult> DownloadAsync(
		string packId,
		Uri url,
		IProgress<MapPackProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		if (!IsUsablePackId(packId))
		{
			return new MapPackDownloadResult(packId, false,
				"A map pack's name can use letters, numbers and hyphens, and has to start with a letter or number.");
		}

		if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
		{
			// Not pedantry: the phones refuse plain HTTP to anything but loopback (see the platform
			// network config), so an http:// link would fail with a platform error rather than
			// anything a rider could act on.
			return new MapPackDownloadResult(packId, false, "The link has to start with https://.");
		}

		if (!_store.IsSupported)
		{
			return new MapPackDownloadResult(packId, false, "This device cannot store map packs.");
		}

		int version = await _store.NextVersionAsync(packId, cancellationToken);
		long resumeFrom = await _store.PartialLengthAsync(packId, version, cancellationToken);

		try
		{
			return await TransferAsync(packId, version, url, resumeFrom, progress, cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// The partial file stays. Asking again resumes it, which is the whole point of keeping
			// one — a rider who cancelled on mobile data and reconnected to Wi-Fi loses nothing.
			return new MapPackDownloadResult(packId, false, "Download stopped. Starting it again will carry on from here.");
		}
		catch (HttpRequestException exception)
		{
			return new MapPackDownloadResult(packId, false, $"Could not download the map pack: {exception.Message}");
		}
		catch (IOException exception)
		{
			return new MapPackDownloadResult(packId, false, $"Could not write the map pack to this device: {exception.Message}");
		}
	}

	private async Task<MapPackDownloadResult> TransferAsync(
		string packId,
		int version,
		Uri url,
		long resumeFrom,
		IProgress<MapPackProgress>? progress,
		CancellationToken cancellationToken)
	{
		using HttpRequestMessage request = new(HttpMethod.Get, url);

		if (resumeFrom > 0)
		{
			request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
		}

		// ResponseHeadersRead: the body is hundreds of megabytes and must be streamed to disk, not
		// buffered into memory first. The default would materialise the whole archive in RAM.
		using HttpResponseMessage response = await _http.SendAsync(
			request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

		if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
		{
			// The partial is at or past the file's length — usually because the file on the host
			// was replaced with a shorter one. Start again rather than resuming into nothing.
			await _store.DiscardAsync(packId, version, cancellationToken);
			return new MapPackDownloadResult(packId, false, "The map pack on that link has changed. Try again to download it fresh.");
		}

		if (!response.IsSuccessStatusCode)
		{
			return new MapPackDownloadResult(packId, false,
				$"That link answered {(int)response.StatusCode} {response.ReasonPhrase}.");
		}

		// A server that ignored the Range header and sent the whole file. Appending it to what is
		// already on disk would produce a corrupt archive of an entirely plausible length.
		bool restart = resumeFrom > 0 && response.StatusCode != HttpStatusCode.PartialContent;

		if (restart)
		{
			resumeFrom = 0;
		}

		long? total = response.Content.Headers.ContentRange?.Length
			?? (response.Content.Headers.ContentLength is { } length ? resumeFrom + length : null);

		await using Stream? destination = await _store.OpenWriteAsync(packId, version, restart, cancellationToken);

		if (destination is null)
		{
			return new MapPackDownloadResult(packId, false, "There is nowhere on this device to put the map pack.");
		}

		await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);

		long received = resumeFrom;
		long sinceReport = 0;

		byte[] buffer = new byte[BufferBytes];

		while (true)
		{
			int read = await source.ReadAsync(buffer, cancellationToken);

			if (read == 0)
			{
				break;
			}

			await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

			received += read;
			sinceReport += read;

			if (sinceReport >= ProgressEveryBytes)
			{
				sinceReport = 0;
				progress?.Report(new MapPackProgress(packId, received, total));
			}
		}

		await destination.FlushAsync(cancellationToken);
		await destination.DisposeAsync();

		progress?.Report(new MapPackProgress(packId, received, total));

		if (total is { } expected && received != expected)
		{
			// Short. The connection dropped without an error — leave the partial so the next
			// attempt resumes rather than starting the whole thing again.
			return new MapPackDownloadResult(packId, false,
				$"The download stopped early ({Describe(received)} of {Describe(expected)}). Try again to carry on.");
		}

		if (!await LooksLikePmtilesAsync(packId, version, cancellationToken))
		{
			await _store.DiscardAsync(packId, version, cancellationToken);
			return new MapPackDownloadResult(packId, false,
				"That link did not return a map pack. A PMTiles archive is expected — check the URL.");
		}

		string? checksum = await ChecksumAsync(packId, version, cancellationToken);

		return await _store.CommitAsync(packId, version, cancellationToken)
			? new MapPackDownloadResult(packId, true, $"Downloaded {Describe(received)}.", checksum)
			: new MapPackDownloadResult(packId, false, "The map pack downloaded but could not be saved.");
	}

	/// <summary>
	/// Reads the first eight bytes of the finished download and checks the PMTiles v3 magic.
	/// <para>
	/// Cheap, and it catches the failure that would otherwise be baffling: a URL that answers 200
	/// with an HTML error page, a login redirect, or a ZIP. Without this the archive commits, the
	/// map goes blank, and nothing anywhere says why — the renderer's failure is inside a WebView.
	/// </para>
	/// </summary>
	private async Task<bool> LooksLikePmtilesAsync(string packId, int version, CancellationToken cancellationToken)
	{
		await using Stream? partial = await _store.OpenPartialReadAsync(packId, version, cancellationToken);

		if (partial is null)
		{
			return false;
		}

		byte[] header = new byte[8];
		int read = await partial.ReadAtLeastAsync(header, 8, throwOnEndOfStream: false, cancellationToken);

		return read == 8
			&& header.AsSpan(0, 7).SequenceEqual("PMTiles"u8)
			&& header[7] == 3;
	}

	/// <summary>
	/// SHA-256 of the finished download, lowercase hex, or <c>null</c> if it could not be read.
	/// Streamed rather than buffered — the file is the reason this class exists.
	/// </summary>
	private async Task<string?> ChecksumAsync(string packId, int version, CancellationToken cancellationToken)
	{
		try
		{
			await using Stream? partial = await _store.OpenPartialReadAsync(packId, version, cancellationToken);

			if (partial is null)
			{
				return null;
			}

			byte[] hash = await SHA256.HashDataAsync(partial, cancellationToken);
			return Convert.ToHexStringLower(hash);
		}
		catch (IOException)
		{
			// A checksum nobody is comparing against yet is not worth failing a good download over.
			return null;
		}
	}

	/// <summary>
	/// Whether a name is one the store will accept. Public so the settings screen can disable the
	/// button rather than letting somebody download 300 MB and be told the name was wrong.
	/// <para>
	/// Mirrors <c>FileMapPackStore</c>'s rule exactly: it becomes a directory name, so anything
	/// that is not a plain slug is refused rather than sanitised.
	/// </para>
	/// </summary>
	/// <param name="packId">The candidate.</param>
	public static bool IsUsablePackId(string? packId) =>
		!string.IsNullOrEmpty(packId)
		&& packId.Length <= 64
		&& char.IsAsciiLetterOrDigit(packId[0])
		&& packId.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

	/// <summary>A byte count as a rider reads it. Binary units, because that is what a phone reports.</summary>
	/// <param name="bytes">The count.</param>
	public static string Describe(long bytes) => bytes switch
	{
		< 1024 => $"{bytes} B",
		< 1024 * 1024 => (bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
		< 1024L * 1024 * 1024 => (bytes / (1024d * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
		_ => (bytes / (1024d * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " GB",
	};

	public void Dispose() => _http.Dispose();
}
