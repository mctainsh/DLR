using DLR.Architecture.Tests.Rules;

namespace DLR.Architecture.Tests;

/// <summary>
/// The map's browser assets reach no third party except the tile server (§4.5, §13 Q26).
/// <para>
/// <strong>This is the rule the offline work rests on.</strong> MapLibre GL JS used to be pulled
/// from jsDelivr on first use, which meant a phone in a dead zone failed before it requested a
/// single tile — downloaded tiles would not have helped, because the renderer itself was the
/// missing piece. The library is vendored now, and this stops it drifting back: a remote asset in
/// these files is invisible on a developer's desk, on CI and in every simulator, and shows up only
/// as "the map is blank" from a rider at a trailhead.
/// </para>
/// <para>
/// Metadata cannot see a <c>wwwroot</c> folder, so this rule reads the files.
/// </para>
/// </summary>
public sealed class MapAssetRules
{
	/// <summary>The shared RCL's map module folder, relative to the repository root.</summary>
	private const string MapFolder = "BlazorDLR.Shared/wwwroot/map";

	/// <summary>
	/// Hosts a map module must not fetch anything from at runtime.
	/// <para>
	/// Named rather than pattern-matched on "https://": the whole point of the module is that it
	/// requests tiles, and the tile hosts are exactly what the rider chooses. What is forbidden is
	/// pulling <em>code</em>, <em>styles</em>, <em>fonts</em> or <em>sprites</em> off somebody
	/// else's infrastructure, and in practice that means a package CDN.
	/// </para>
	/// </summary>
	private static readonly string[] AssetCdnHosts =
	[
		"cdn.jsdelivr.net",
		"unpkg.com",
		"cdnjs.cloudflare.com",
		"esm.sh",
		"skypack.dev",
		"fonts.googleapis.com",
		"fonts.gstatic.com",
	];

	private static IReadOnlyList<(string Name, string Text)> MapModules()
	{
		string folder = Path.Combine(SourceTree.Root, MapFolder.Replace('/', Path.DirectorySeparatorChar));

		Directory.Exists(folder).ShouldBeTrue($"{MapFolder} is where the map's JS modules live.");

		// Top level only: `lib/` underneath it is vendored third-party code, which is allowed to
		// mention whatever it likes in its own comments and source maps.
		return Directory.EnumerateFiles(folder, "*.js", SearchOption.TopDirectoryOnly)
			.Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)))
			.ToList();
	}

	[Fact]
	public void MapModules_PullNoCodeOrFontsFromACdn()
	{
		List<string> offenders = MapModules()
			.SelectMany(module => AssetCdnHosts
				.Where(host => module.Text.Contains(host, StringComparison.OrdinalIgnoreCase))
				.Select(host => $"{MapFolder}/{module.Name} references {host}"))
			.Order(StringComparer.Ordinal)
			.ToList();

		offenders.ShouldBeEmpty(
			"a map module that fetches its library, style, glyphs or sprites from a CDN cannot draw " +
			"a map without a connection — which is the one thing the offline work exists to give a " +
			"rider at a trailhead, and it fails before a single tile is requested. Vendor the asset " +
			"under BlazorDLR.Shared/wwwroot/map/lib/ and resolve it through import.meta.url, the way " +
			"map.maplibre.js loads MapLibre GL JS.");
	}

	[Fact]
	public void TheVendoredRendererIsPresent_WithItsLicence()
	{
		string libFolder = Path.Combine(
			SourceTree.Root,
			$"{MapFolder}/lib/maplibre".Replace('/', Path.DirectorySeparatorChar));

		Directory.Exists(libFolder).ShouldBeTrue(
			"MapLibre GL JS ships with the app rather than being fetched (§4.5). If this folder has " +
			"gone, every map is one CDN outage — or one dead zone — away from a blank rectangle.");

		File.Exists(Path.Combine(libFolder, "maplibre-gl.js")).ShouldBeTrue("the UMD bundle the module loads.");
		File.Exists(Path.Combine(libFolder, "maplibre-gl.css")).ShouldBeTrue(
			"without it the canvas is unpositioned and the attribution is unreadable — and §4.5 makes " +
			"the attribution permanent.");

		// The app is AGPL and ships this file (§14.6). Redistributing a 3-Clause BSD library means
		// carrying its licence text, and the vendored fontawesome next door sets the same precedent.
		File.Exists(Path.Combine(libFolder, "LICENSE.txt")).ShouldBeTrue(
			"vendoring a third-party library means shipping its licence with it.");
	}

	[Fact]
	public void TheOfflineStyleShipsComplete()
	{
		string style = Path.Combine(SourceTree.Root, $"{MapFolder}/style".Replace('/', Path.DirectorySeparatorChar));

		Directory.Exists(style).ShouldBeTrue(
			"the vector style, its glyphs and its sprite are what an offline pack is drawn with (§13 Q26).");

		File.Exists(Path.Combine(style, "basemap.json")).ShouldBeTrue("the style document itself.");

		// A style renders no label at all without its glyph ranges, and the failure is silent —
		// roads and coastlines still draw, so an incomplete bundle looks like a cartography choice
		// rather than a missing file.
		string glyphs = Path.Combine(style, "glyphs");

		foreach (string stack in new[] { "NotoSans-Regular", "NotoSans-Medium", "NotoSans-Italic" })
		{
			File.Exists(Path.Combine(glyphs, stack, "0-255.pbf")).ShouldBeTrue(
				$"the style asks for '{stack}', so its Basic Latin range has to be here.");
		}

		// A space here cost an evening. MapLibre substitutes {fontstack} into the glyphs URL with
		// NO url-encoding, so a stack named "Noto Sans Regular" produces a request carrying literal
		// spaces — which every host's static-file handler then has to percent-decode identically.
		// One that does not returns a 404 body, MapLibre feeds it to the protobuf decoder, and the
		// whole map dies with "Unimplemented type: 4" naming neither the font nor the URL.
		List<string> spaced = Directory.EnumerateDirectories(glyphs)
			.Select(Path.GetFileName)
			.Where(name => name?.Contains(' ', StringComparison.Ordinal) == true)
			.ToList()!;

		spaced.ShouldBeEmpty(
			"a font stack's name becomes a URL path segment verbatim — keep it a slug.");

		File.Exists(Path.Combine(style, "sprite", "light.png")).ShouldBeTrue("the symbol layers' icon sheet.");
		File.Exists(Path.Combine(style, "sprite", "light.json")).ShouldBeTrue("and its index.");
		File.Exists(Path.Combine(style, "sprite", "light@2x.png")).ShouldBeTrue(
			"phones are all retina — without the @2x sheet every icon on the map is soft.");

		// Fonts and a basemap style are somebody else's work, shipped inside an AGPL app (§14.6).
		File.Exists(Path.Combine(glyphs, "OFL.txt")).ShouldBeTrue("the fonts' licence.");
		File.Exists(Path.Combine(style, "LICENSE-basemaps.txt")).ShouldBeTrue("the style's licence.");
	}

	/// <summary>
	/// The style document ships pointing at Protomaps' own hosts and a placeholder tile server; the
	/// module rewrites all three at load. This asserts the rewrite still has something to find — a
	/// future style whose fields were named differently would leave the map fetching glyphs from
	/// the internet, which works on a desk and fails at a trailhead.
	/// </summary>
	[Fact]
	public void TheStyleCarriesTheThreeFieldsTheModuleRewrites()
	{
		string path = Path.Combine(
			SourceTree.Root,
			$"{MapFolder}/style/basemap.json".Replace('/', Path.DirectorySeparatorChar));

		using System.Text.Json.JsonDocument style = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));

		style.RootElement.TryGetProperty("glyphs", out _).ShouldBeTrue("offlineStyle() replaces this.");
		style.RootElement.TryGetProperty("sprite", out _).ShouldBeTrue("and this.");

		style.RootElement.TryGetProperty("sources", out System.Text.Json.JsonElement sources).ShouldBeTrue();
		sources.EnumerateObject().Count().ShouldBe(1,
			"offlineStyle() points the first source at the device's archive. More than one and it " +
			"would be guessing which.");
	}
}
