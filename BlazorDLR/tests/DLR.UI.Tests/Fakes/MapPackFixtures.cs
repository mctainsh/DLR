using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Fakes;

/// <summary>
/// The catalogue, the archive and the wiring every offline-pack suite needs (§4.2).
/// <para>
/// One copy rather than one per suite: the three offer suites and the settings screen's all want
/// the same three regions and the same eight-byte archive, and a fixture that drifts between them
/// is a test that passes for the wrong reason.
/// </para>
/// </summary>
internal static class MapPackFixtures
{
	/// <summary>
	/// New South Wales inside Australia, with Tasmania beside them - the ordinary shape of a real
	/// catalogue rather than a contrived one. Sydney falls inside two of the three, so every test
	/// about <em>which</em> pack has a choice to get wrong.
	/// </summary>
	public const string Catalogue = """
		[
			{ "id": "au-nsw", "name": "New South Wales", "region": "Australia", "minZoom": 0, "maxZoom": 14,
			  "bounds": { "minLatitude": -37.52, "minLongitude": 140.99, "maxLatitude": -28.15, "maxLongitude": 153.65 },
			  "sizeBytes": 351089645, "sha256": "745b01e7", "version": 1,
			  "url": "https://packs.example.com/au-nsw.v1.pmtiles" },
			{ "id": "au-all", "name": "Australia", "region": "Australia", "minZoom": 0, "maxZoom": 14,
			  "bounds": { "minLatitude": -43.64, "minLongitude": 112.92, "maxLatitude": -10.06, "maxLongitude": 153.64 },
			  "sizeBytes": 2351089645, "sha256": "cc33dd44", "version": 1,
			  "url": "https://packs.example.com/au-all.v1.pmtiles" },
			{ "id": "au-tas", "name": "Tasmania", "region": "Australia", "minZoom": 0, "maxZoom": 14,
			  "bounds": { "minLatitude": -43.70, "minLongitude": 143.80, "maxLatitude": -39.50, "maxLongitude": 148.50 },
			  "sizeBytes": 57935628, "sha256": "dd9d57d3", "version": 1,
			  "url": "https://packs.example.com/au-tas.v1.pmtiles" }
		]
		""";

	/// <summary>A minimal well-formed PMTiles archive: the v3 magic and filler.</summary>
	public static byte[] Archive => [0x50, 0x4D, 0x54, 0x69, 0x6C, 0x65, 0x73, 0x03];

	/// <summary>Where the catalogue above is served from. Also the base a relative pack URL resolves against.</summary>
	public static Uri CatalogueUrl { get; } = new("https://packs.example.com/catalogue.json");

	/// <summary>Sydney - inside New South Wales, and inside Australia over the top of it.</summary>
	public const double SydneyLatitude = -33.87;

	/// <summary>Sydney.</summary>
	public const double SydneyLongitude = 151.21;

	/// <summary>
	/// Wires a device that can hold packs, over a stubbed catalogue and a stubbed archive host.
	/// <para>
	/// Registered <em>after</em> <see cref="MapTestHelpers.AddRideMapServices"/>, whose bindings are
	/// the browser's (§18.6) - the last registration is the one resolved, so this is what turns a
	/// suite's device into a phone.
	/// </para>
	/// </summary>
	/// <param name="services">The test's container.</param>
	/// <param name="packs">This device's archives. <c>IsSupported</c> false plays the browser.</param>
	/// <param name="catalogue">What the catalogue answers with.</param>
	public static void AddOfferedPacks(
		this IServiceCollection services,
		FakeMapPackStore packs,
		string catalogue = Catalogue)
	{
		services.AddScoped<IMapPackStore>(_ => packs);
		services.AddScoped(sp => new MapPackDownloader(
			sp.GetRequiredService<IMapPackStore>(),
			new HttpClient(new StubHttpHandler(Archive))));
		services.AddScoped(_ => new MapPackCatalogue(new HttpClient(new StubHttpHandler(catalogue)), CatalogueUrl));
	}

	/// <summary>
	/// The same graph without a container, for the suites that test the state directly.
	/// </summary>
	/// <param name="settings">Where declines and the last dead zone are remembered.</param>
	/// <param name="packs">This device's archives.</param>
	/// <param name="catalogue">What the catalogue answers with - or the handler, for a test about a failure.</param>
	public static (OfflineMapOfferState Offers, MapSourceState Sources) BuildOfferState(
		IDeviceSettings settings,
		FakeMapPackStore packs,
		StubHttpHandler? catalogue = null)
	{
		MapSourceState sources = new(settings, packs);

		MapPackState state = new(
			packs,
			new MapPackDownloader(packs, new HttpClient(new StubHttpHandler(Archive))),
			new MapPackCatalogue(new HttpClient(catalogue ?? new StubHttpHandler(Catalogue)), CatalogueUrl));

		return (new OfflineMapOfferState(settings, state, sources), sources);
	}
}

/// <summary>
/// An <see cref="IDeviceSettings"/> that counts what reaches the device. For the tests about how
/// often a state writes, which is the only thing they can observe -
/// <see cref="BlazorDLR.Shared.Services.Platform.InMemoryDeviceSettings"/> is sealed, so this wraps
/// one rather than extending it.
/// </summary>
internal sealed class CountingDeviceSettings : IDeviceSettings
{
	private readonly BlazorDLR.Shared.Services.Platform.InMemoryDeviceSettings _inner = new();

	/// <summary>How many values have been written.</summary>
	public int Writes { get; private set; }

	/// <summary>How many values have been read.</summary>
	public int Reads { get; private set; }

	public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
	{
		Reads++;
		return _inner.GetAsync(key, cancellationToken);
	}

	public ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default)
	{
		Writes++;
		return _inner.SetAsync(key, value, cancellationToken);
	}

	public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) =>
		_inner.RemoveAsync(key, cancellationToken);
}
