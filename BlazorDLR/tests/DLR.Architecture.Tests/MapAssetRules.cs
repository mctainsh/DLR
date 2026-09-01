using DLR.Architecture.Tests.Rules;

namespace DLR.Architecture.Tests;

/// <summary>
/// The map's browser assets reach no third party except the tile server (§4.5, §13 Q26).
/// <para>
/// <strong>This is the rule the offline work rests on.</strong> MapLibre GL JS used to be pulled
/// from jsDelivr on first use, which meant a phone in a dead zone failed before it requested a
/// single tile - downloaded tiles would not have helped, because the renderer itself was the
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
			"a map without a connection - which is the one thing the offline work exists to give a " +
			"traveller at a trailhead, and it fails before a single tile is requested. Vendor the asset " +
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
			"gone, every map is one CDN outage - or one dead zone - away from a blank rectangle.");

		File.Exists(Path.Combine(libFolder, "maplibre-gl.js")).ShouldBeTrue("the UMD bundle the module loads.");
		File.Exists(Path.Combine(libFolder, "maplibre-gl.css")).ShouldBeTrue(
			"without it the canvas is unpositioned and the attribution is unreadable - and §4.5 makes " +
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

		// One document per theme (§13 Q26). Both are the style, not a style and a variant: an
		// archive holds no colour, so the theme a rider picks selects between these two files and
		// a missing one is a map that cannot draw at all in the mode they chose.
		File.Exists(Path.Combine(style, "basemap.json")).ShouldBeTrue("the light style document.");
		File.Exists(Path.Combine(style, "basemap.dark.json")).ShouldBeTrue("and the dark one.");

		// A style renders no label at all without its glyph ranges, and the failure is silent -
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
		// spaces - which every host's static-file handler then has to percent-decode identically.
		// One that does not returns a 404 body, MapLibre feeds it to the protobuf decoder, and the
		// whole map dies with "Unimplemented type: 4" naming neither the font nor the URL.
		List<string> spaced = Directory.EnumerateDirectories(glyphs)
			.Select(Path.GetFileName)
			.Where(name => name?.Contains(' ', StringComparison.Ordinal) == true)
			.ToList()!;

		spaced.ShouldBeEmpty(
			"a font stack's name becomes a URL path segment verbatim - keep it a slug.");

		// A sheet per theme, because the icons are painted for the ground under them - the light
		// sheet over the dark document draws dark glyphs on dark ground, which is not a fallback
		// anybody would choose, and the failure is per icon rather than per map.
		foreach (string theme in new[] { "light", "dark" })
		{
			File.Exists(Path.Combine(style, "sprite", $"{theme}.png")).ShouldBeTrue(
				$"the symbol layers' icon sheet for the {theme} style.");
			File.Exists(Path.Combine(style, "sprite", $"{theme}.json")).ShouldBeTrue("and its index.");
			File.Exists(Path.Combine(style, "sprite", $"{theme}@2x.png")).ShouldBeTrue(
				"phones are all retina - without the @2x sheet every icon on the map is soft.");
			File.Exists(Path.Combine(style, "sprite", $"{theme}@2x.json")).ShouldBeTrue("and its index.");
		}

		// Fonts and a basemap style are somebody else's work, shipped inside an AGPL app (§14.6).
		File.Exists(Path.Combine(glyphs, "OFL.txt")).ShouldBeTrue("the fonts' licence.");
		File.Exists(Path.Combine(style, "LICENSE-basemaps.txt")).ShouldBeTrue("the style's licence.");
	}

	/// <summary>
	/// The style documents ship pointing at Protomaps' own hosts and a placeholder tile server; the
	/// module rewrites all three at load. This asserts the rewrite still has something to find - a
	/// future style whose fields were named differently would leave the map fetching glyphs from
	/// the internet, which works on a desk and fails at a trailhead.
	/// </summary>
	[Theory]
	[InlineData("basemap.json")]
	[InlineData("basemap.dark.json")]
	public void TheStyleCarriesTheThreeFieldsTheModuleRewrites(string document)
	{
		string path = Path.Combine(
			SourceTree.Root,
			$"{MapFolder}/style/{document}".Replace('/', Path.DirectorySeparatorChar));

		using System.Text.Json.JsonDocument style = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));

		style.RootElement.TryGetProperty("glyphs", out _).ShouldBeTrue("offlineStyle() replaces this.");
		style.RootElement.TryGetProperty("sprite", out _).ShouldBeTrue("and this.");

		style.RootElement.TryGetProperty("sources", out System.Text.Json.JsonElement sources).ShouldBeTrue();
		sources.EnumerateObject().Count().ShouldBe(1,
			"offlineStyle() points the first source at the device's archive. More than one and it " +
			"would be guessing which.");
	}

	/// <summary>
	/// The style documents still declare the three ground layers the offline map is floored with.
	/// <para>
	/// <strong>What this protects.</strong> A regional pack holds only the tiles its region's box
	/// touches - one tile at z0 through z3 for Queensland - and MapLibre reads that box out of the
	/// archive and refuses to ask for anything outside it. Everything else on screen is the style's
	/// background colour, which reads as a dead band of zooms between "the world fits in the pack's
	/// z0 tile" and "the rider is inside the region". <c>addWorldUnderlay</c> in
	/// <c>map.maplibre.js</c> answers that by cloning these three layers onto a second source capped
	/// at zoom 0, so the pack's own world tile floors the map everywhere.
	/// </para>
	/// <para>
	/// It clones them <em>by id</em>, and it gives up quietly when it finds none - a map with the
	/// old grey void beats a map that throws on a style this build does not recognise. Quietly is
	/// the problem: a vendored style that renamed these would bring the void back with nothing
	/// failing anywhere, so the naming is pinned here instead.
	/// </para>
	/// </summary>
	/// <param name="document">Which theme's style document.</param>
	[Theory]
	[InlineData("basemap.json")]
	[InlineData("basemap.dark.json")]
	public void TheStyleCarriesTheGroundLayersTheOfflineUnderlayClones(string document)
	{
		string path = Path.Combine(
			SourceTree.Root,
			$"{MapFolder}/style/{document}".Replace('/', Path.DirectorySeparatorChar));

		using System.Text.Json.JsonDocument style = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));

		List<System.Text.Json.JsonElement> layers = [.. style.RootElement.GetProperty("layers").EnumerateArray()];

		layers[0].GetProperty("type").GetString().ShouldBe("background",
			"the underlay is inserted after the first layer, on the assumption it is the background.");

		foreach (string id in new[] { "earth", "landcover", "water" })
		{
			System.Text.Json.JsonElement layer = layers.SingleOrDefault(
				candidate => candidate.TryGetProperty("id", out System.Text.Json.JsonElement name)
					&& name.GetString() == id);

			layer.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Object,
				$"map.maplibre.js clones '{id}' onto the zoom-0 source that floors an offline pack. " +
				"Renaming it in the style leaves the map grey outside the downloaded region, and " +
				"nothing else says so.");

			layer.GetProperty("type").GetString().ShouldBe("fill",
				$"'{id}' floors the map, so it has to be something that paints an area.");
		}
	}

	/// <summary>
	/// Both themes ask for the same fonts.
	/// <para>
	/// The glyphs are shipped once and shared, which is what keeps a second theme at ~290 KB rather
	/// than ~1 MB. If a future dark style asked for a stack the light one does not, that stack would
	/// have no <c>.pbf</c> under <c>glyphs/</c> - and a missing range renders no label at all while
	/// roads and coastlines carry on drawing, so it would read as a cartography choice rather than
	/// as a missing file.
	/// </para>
	/// </summary>
	[Fact]
	public void BothThemesAskForTheSameFonts()
	{
		HashSet<string> light = FontStacks("basemap.json");
		HashSet<string> dark = FontStacks("basemap.dark.json");

		dark.Except(light).ShouldBeEmpty(
			"the dark style may only name fonts the light one already ships glyphs for - see glyphs/.");

		static HashSet<string> FontStacks(string document)
		{
			string path = Path.Combine(
				SourceTree.Root,
				$"{MapFolder}/style/{document}".Replace('/', Path.DirectorySeparatorChar));

			using System.Text.Json.JsonDocument style = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));

			HashSet<string> stacks = new(StringComparer.Ordinal);

			foreach (System.Text.Json.JsonElement layer in style.RootElement.GetProperty("layers").EnumerateArray())
			{
				if (!layer.TryGetProperty("layout", out System.Text.Json.JsonElement layout)
					|| !layout.TryGetProperty("text-font", out System.Text.Json.JsonElement fonts)
					|| fonts.ValueKind != System.Text.Json.JsonValueKind.Array)
				{
					continue;
				}

				foreach (System.Text.Json.JsonElement font in fonts.EnumerateArray())
				{
					if (font.ValueKind == System.Text.Json.JsonValueKind.String)
					{
						stacks.Add(font.GetString()!);
					}
				}
			}

			return stacks;
		}
	}
}
