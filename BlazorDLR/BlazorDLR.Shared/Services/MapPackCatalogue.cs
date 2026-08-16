using System.Text.Json;
using DLR.Core.Contracts.Maps;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// One pack the catalogue offers, reduced to what this device can act on (§4.2).
/// <para>
/// Not <see cref="MapPackSummary"/> itself, because that is the publisher's claim and this is the
/// subset that survived checking: the id is one the pack store will accept as a directory name, and
/// the URL is absolute — resolved against the catalogue's own address when the entry gave a relative
/// one. A screen rendering this list does not have to re-ask either question.
/// </para>
/// </summary>
/// <param name="Id">The pack's slug, and what it is called on the device.</param>
/// <param name="Name">What a rider reads.</param>
/// <param name="Region">
/// The country this pack belongs to, resolved. Never blank, unlike
/// <see cref="MapPackSummary.Region"/> — the screen groups every offer under one of these, so an
/// entry with nowhere to go would be an entry nobody can reach. See <see cref="RegionFor"/>.
/// </param>
/// <param name="Version">The catalogue's build number for this extract.</param>
/// <param name="SizeBytes">How much of the phone it will take.</param>
/// <param name="Sha256">The published checksum, lowercase hex, or <c>null</c> when the entry carried none.</param>
/// <param name="Url">Where to fetch it from, absolute.</param>
public sealed record MapPackOffer(
	string Id,
	string Name,
	string Region,
	int Version,
	long SizeBytes,
	string? Sha256,
	Uri Url)
{
	// Deliberately no IsDownloadable here. It existed, the screen branched on it to draw a reason
	// instead of a button, and the page guarded the click with it as well — which made the rule true
	// in three places and, when one of them changed, produced a button that did nothing at all.
	// MapPackDownloader.IsFetchable is now the only statement of it, enforced where the transfer
	// actually starts, and a refusal comes back through MapPackState.Status like every other one.
}

/// <summary>What one read of the catalogue came back with.</summary>
/// <param name="Packs">What is on offer, empty when the read failed.</param>
/// <param name="Problem">What went wrong, in the words to put on screen, or <c>null</c> on success.</param>
public sealed record MapPackCatalogueResult(IReadOnlyList<MapPackOffer> Packs, string? Problem)
{
	/// <summary>A read that did not happen, carrying the reason.</summary>
	/// <param name="problem">What to tell the rider.</param>
	public static MapPackCatalogueResult Failed(string problem) => new([], problem);
}

/// <summary>
/// Reads the list of offline map packs on offer (§4.2).
/// <para>
/// <strong>Why this replaced a text box.</strong> The Maps screen used to take any HTTPS link and a
/// name to file it under, which was the only way to test the offline path before a catalogue
/// existed. It asked a rider to know a URL, and to invent a name that the store might then refuse —
/// two chances to get a 300 MB download wrong before it started. The catalogue answers both: the
/// name and the id come from the publisher, and the only thing left to choose is which region.
/// </para>
/// <para>
/// <strong>It owns a credential-free <see cref="HttpClient"/> for the same reason the downloader
/// does</strong> (§18.5). The registered client carries <c>BearerAuthHandler</c> and is pointed at
/// the DLR API; this request goes to a static file host that is not the API, and attaching the
/// rider's access token to it would hand their session to another server. One constructor, and the
/// client is not optional — see <see cref="MapPackDownloader"/>'s remarks for the DI accident that
/// rule exists to prevent.
/// </para>
/// <para>
/// <strong>Nothing here is trusted.</strong> The catalogue is a JSON file on a web host: an id
/// becomes a directory name, so anything the store would refuse is dropped rather than sanitised,
/// and a URL is resolved rather than concatenated. Whatever survives that is still only a claim
/// about what is at the other end — the downloader checks the PMTiles magic on what arrives.
/// </para>
/// </summary>
public sealed class MapPackCatalogue : IDisposable
{
	/// <summary>
	/// Where the packs are published today.
	/// <para>
	/// <strong>Plain HTTP, which is a deliberate and temporary compromise.</strong> The host answers
	/// on 443 with a certificate for another domain, so HTTPS cannot be verified; the Android network
	/// security config and the iOS ATS exceptions name this one host to let the fetch through, and
	/// both carry the same note. The archives are served from the same host and are fetched the same
	/// way — see <see cref="PermitsCleartext"/>, which is the whole of the exception and is scoped to
	/// this host alone.
	/// </para>
	/// </summary>
	public const string DefaultUrl = "http://pmtiles.securehub.net/catalogue.json";

	/// <summary>
	/// The one host this app may talk to in the clear, besides its own loopback.
	/// <para>
	/// <strong>This constant and the two platform configs are one decision written three times</strong>
	/// — <c>Platforms/Android/Resources/xml/network_security_config.xml</c> and
	/// <c>Platforms/iOS/Info.plist</c> are the other two, and all three carry the same note. They
	/// have to agree: a host permitted here but not there fails inside the platform with an error no
	/// rider could act on, and a host permitted there but not here is a button this app refuses to
	/// press. All three go together the day it serves a certificate matching its own name.
	/// </para>
	/// </summary>
	public const string CleartextHost = "pmtiles.securehub.net";

	/// <summary>
	/// Longest catalogue this will read. A few dozen regions is a handful of kilobytes; anything past
	/// this is not a catalogue, and the point of a cap is to find that out without buffering it.
	/// </summary>
	private const int MaxBytes = 1024 * 1024;

	/// <summary>
	/// Web defaults: camelCase, case-insensitive. What <c>Build-AuMapPacks.ps1</c> writes and what
	/// the planned <c>GET /api/v1/map-packs</c> will answer with, so one reader serves both.
	/// </summary>
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	/// <summary>
	/// What to call a pack whose entry carries no region, keyed on the first segment of its id.
	/// <para>
	/// <strong>A fallback for old data, and not a second source of truth.</strong> The publisher
	/// states the region — see <see cref="MapPackSummary.Region"/> for why it is published rather than
	/// derived — but the catalogue on the host today predates the field, and that is the one riders
	/// are fetching from until it is rebuilt. Without this every one of its two hundred entries lands
	/// in a single group and the first dropdown is a control with one option in it.
	/// </para>
	/// <para>
	/// Coarser than what is published, deliberately: this maps a slug prefix to the largest thing that
	/// prefix is certainly inside, so <c>as-turkey</c> becoming Asia is a true statement that ages
	/// well. Guessing Turkiye from it would be this file trying to be the pack table, which is the
	/// thing the published field exists to avoid.
	/// </para>
	/// </summary>
	private static readonly Dictionary<string, string> RegionByIdPrefix = new(StringComparer.Ordinal)
	{
		["au"] = "Australia",
		["oc"] = "Oceania",
		["as"] = "Asia",
		["cn"] = "China",
		["mn"] = "Mongolia",
		["in"] = "India",
		["ru"] = "Russia",
		["eu"] = "Europe",
		["af"] = "Africa",
		["na"] = "North America",
		["sa"] = "South America",
	};

	/// <summary>
	/// Where a pack with no region of its own is filed. Last, because the screen sorts regions
	/// alphabetically and a bucket of leftovers is not what anybody is scrolling for.
	/// </summary>
	public const string UnknownRegion = "Other maps";

	private readonly HttpClient _http;

	/// <summary>
	/// Creates a reader over an address and a client to fetch it with.
	/// </summary>
	/// <param name="http">
	/// The client. Must carry no credentials — see the type's remarks;
	/// <see cref="CreateCredentialFreeClient"/> is what every host passes.
	/// </param>
	/// <param name="address">Where the catalogue is. <see cref="DefaultUrl"/> unless a host says otherwise.</param>
	public MapPackCatalogue(HttpClient http, Uri address)
	{
		_http = http;
		Address = address;
	}

	/// <summary>Where this reads from. Also the base a relative pack URL is resolved against.</summary>
	public Uri Address { get; }

	/// <summary>
	/// The country to file <paramref name="entry"/> under: what it says, or the best that can be said
	/// about its id when it says nothing.
	/// <para>
	/// Never blank. The settings screen shows every offer under a region, so an entry with no region
	/// is not a row without a heading — it is a row nobody can reach, which is worse than filing it
	/// under <see cref="UnknownRegion"/> and letting the search find it.
	/// </para>
	/// </summary>
	/// <param name="entry">The catalogue entry as published.</param>
	public static string RegionFor(MapPackSummary entry)
	{
		if (!string.IsNullOrWhiteSpace(entry.Region))
			return entry.Region.Trim();

		int separator = entry.Id.IndexOf('-');
		string prefix = separator > 0 ? entry.Id[..separator] : entry.Id;

		return RegionByIdPrefix.TryGetValue(prefix, out string? region) ? region : UnknownRegion;
	}

	/// <summary>
	/// Whether <paramref name="url"/> is cleartext this app is allowed to fetch — <c>http://</c> on
	/// <see cref="CleartextHost"/>, and nothing else.
	/// <para>
	/// <strong>Narrow on purpose, and temporary.</strong> The alternative considered was letting the
	/// downloader take any <c>http://</c> now that the catalogue publishes them. That would have been
	/// a rule the platform does not share: every other cleartext host is blocked below this code, so
	/// the app would offer buttons that fail inside Android's network stack — and it would survive
	/// the certificate being fixed, quietly, as a general permission nobody decided to grant.
	/// </para>
	/// <para>
	/// What rides on it: the archive is hundreds of megabytes and is rendered as the map a rider
	/// navigates by, in the clear and with the published <c>sha256</c> still uncompared (§4.3 step 3).
	/// That is the outstanding work this makes worth doing.
	/// </para>
	/// </summary>
	/// <param name="url">The address a pack would be fetched from.</param>
	public static bool PermitsCleartext(Uri url) =>
		url.IsAbsoluteUri
		&& url.Scheme == Uri.UriSchemeHttp
		&& string.Equals(url.Host, CleartextHost, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// A client with no handler, no base address and no credentials, capped so a wrong URL cannot
	/// buffer something large into a phone's memory.
	/// <para>
	/// Short timeout, unlike the downloader's two hours: this is a few kilobytes of JSON in front of
	/// a rider who is waiting to see a list, and a host that cannot answer in twenty seconds is one
	/// to report rather than to keep waiting on.
	/// </para>
	/// </summary>
	public static HttpClient CreateCredentialFreeClient() => new()
	{
		Timeout = TimeSpan.FromSeconds(20),
		MaxResponseContentBufferSize = MaxBytes,
	};

	/// <summary>
	/// Fetches and reads the catalogue.
	/// <para>
	/// Does not throw. A host that is down, a body that is not JSON and a rider with no signal are
	/// all a <see cref="MapPackCatalogueResult"/> with something to put on screen — the same posture
	/// as the downloader, and for the same reason: a screen that has to catch exceptions to say a
	/// list could not be loaded is a screen that will eventually forget to.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancels the read.</param>
	public async Task<MapPackCatalogueResult> ReadAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			using HttpResponseMessage response = await _http.GetAsync(Address, cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				return MapPackCatalogueResult.Failed(
					$"The list of maps answered {(int)response.StatusCode} {response.ReasonPhrase}.");
			}

			byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken);

			IReadOnlyList<MapPackSummary>? published =
				JsonSerializer.Deserialize<IReadOnlyList<MapPackSummary>>(WithoutByteOrderMark(body), Json);

			if (published is null)
			{
				return MapPackCatalogueResult.Failed("The list of maps could not be read.");
			}

			return new MapPackCatalogueResult(Usable(published), null);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			// HttpClient reports its own timeout as a cancellation with no token behind it, which is
			// otherwise indistinguishable from the rider leaving the screen.
			return MapPackCatalogueResult.Failed("The list of maps took too long to answer.");
		}
		catch (HttpRequestException exception)
		{
			return MapPackCatalogueResult.Failed($"Could not reach the list of maps: {exception.Message}");
		}
		catch (JsonException)
		{
			// A captive portal's login page, an error page, an HTML 404. Naming the address is worth
			// more than the parser's column number to anybody who can act on this.
			return MapPackCatalogueResult.Failed($"{Address.Host} did not answer with a list of maps.");
		}
	}

	/// <summary>
	/// Drops the entries this device could not act on and resolves the rest.
	/// <para>
	/// Dropped rather than shown-and-refused: an id the store would reject is a publishing mistake
	/// with nothing a rider can do about it, and a row explaining that would be noise on the one
	/// screen where the list is the whole interface. What does get shown is an entry that is fine
	/// except for its scheme — see <see cref="MapPackOffer.IsDownloadable"/> — because that one is
	/// legible and may be fixed by the time they look again.
	/// </para>
	/// </summary>
	/// <param name="published">The entries as the catalogue gave them.</param>
	private List<MapPackOffer> Usable(IReadOnlyList<MapPackSummary> published)
	{
		List<MapPackOffer> offers = [];

		foreach (MapPackSummary entry in published)
		{
			if (entry is null || !MapPackDownloader.IsUsablePackId(entry.Id))
			{
				// The id becomes a directory name on the phone. FileMapPackStore refuses anything that
				// is not a plain slug rather than cleaning it up, so a `..` or a separator here never
				// becomes a path — this is the same rule, applied before anything is offered.
				continue;
			}

			if (string.IsNullOrWhiteSpace(entry.Url)
				|| !Uri.TryCreate(Address, entry.Url.Trim(), out Uri? url)
				|| (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
			{
				// Relative against the catalogue's own address, so a publisher can list
				// `au-nsw.v1.pmtiles` beside it and move hosts without rewriting every entry.
				continue;
			}

			offers.Add(new MapPackOffer(
				entry.Id,
				string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name.Trim(),
				RegionFor(entry),
				Math.Max(entry.Version, 1),
				Math.Max(entry.SizeBytes, 0),
				string.IsNullOrWhiteSpace(entry.Sha256) ? null : entry.Sha256.Trim(),
				url));
		}

		// Alphabetical by what the rider reads, country first. The catalogue's own order is the order
		// the extracts were built in, which means nothing to anybody looking for their state in a list.
		//
		// Sorted here rather than on the screen because both dropdowns read from this one list, and a
		// screen that re-sorts a copy for each of them is two orderings that can drift apart. What the
		// screen does decide is where the leftovers go — see MapSettings' RegionsOnOffer.
		offers.Sort((left, right) =>
		{
			int byRegion = string.Compare(left.Region, right.Region, StringComparison.OrdinalIgnoreCase);
			return byRegion != 0
				? byRegion
				: string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
		});

		return offers;
	}

	/// <summary>
	/// Skips a UTF-8 byte order mark.
	/// <para>
	/// <c>Utf8JsonReader</c> treats one as a malformed first token rather than skipping it, and the
	/// catalogue this ships pointed at has one — PowerShell's <c>Set-Content -Encoding utf8</c> writes
	/// it. Three bytes here rather than an unreadable-catalogue message nobody could diagnose.
	/// </para>
	/// </summary>
	/// <param name="body">The response body.</param>
	private static ReadOnlySpan<byte> WithoutByteOrderMark(ReadOnlySpan<byte> body) =>
		// 0xEF 0xBB 0xBF, spelled as the escape rather than as an invisible character in the source.
		body.StartsWith("\uFEFF"u8) ? body[3..] : body;

	public void Dispose() => _http.Dispose();
}
