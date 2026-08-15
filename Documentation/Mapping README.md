# Map JS modules

One ES module, one provider, every host (§4.5 v0.24, §18.3).

| File               | Provider              | Bound in                                         | Tiles          |
| ------------------ | --------------------- | ------------------------------------------------ | -------------- |
| `map.maplibre.js`  | MapLibre GL JS        | `BlazorDLR.Shared/Services/MapLibreInterop.cs`    | OpenStreetMap  |
| `interop.js`       | —                     | `BlazorDLR.Shared/Services/MapBridge.cs`          | —              |

v0.24 removed `map.mapkit.js`, `map.googlemaps.js` and `map.openlayers.js` along with the
per-host interops that loaded them. The reason is in §4.5: between them those three cost a
`.p8` on the server, a browser API key in the app bundle, and a token endpoint that made the
map a server dependency — and after v0.21 moved every marker onto the Skia overlay, none of
them was drawing anything the others could not.

## Contract

The module exports a single factory:

```js
export async function createMap(hostElement, options, callbacks) { ... }
```

returning an object with:

- `setCamera(camera)`
- `dispose()`

Base-map role only — tiles, camera, rotation, attribution. **It draws no markers, tracks or
rider pins**; those are `SkiaMapOverlay.razor` on top (§4.5 v0.21). The C# side is
`IMapInterop` in `BlazorDLR.Shared/Services/IMapInterop.cs`, which mirrors this one-to-one.

Callbacks go back to C# through `interop.js`'s `dispatch`, never by calling the
`DotNetObjectReference` directly — the reason is written at the top of that file, and it has
already been got wrong once.

## Vendored assets — `lib/`

| Path | What | Version | Licence |
| ---- | ---- | ------- | ------- |
| `lib/maplibre/maplibre-gl.js`  | UMD bundle, defines the `maplibregl` global | 4.7.1 | 3-Clause BSD (`LICENSE.txt` beside it) |
| `lib/maplibre/maplibre-gl.css` | Control and canvas styling                  | 4.7.1 | as above |
| `lib/pmtiles/pmtiles.js`       | `pmtiles://` protocol plugin, defines the `pmtiles` global | 3.x | BSD-3-Clause (`LICENSE.txt` beside it) |
| `style/basemap.json`           | Protomaps `light` theme, English labels — 68 layers over one vector source | protomaps-themes-base 4.5.0 | `LICENSE-basemaps.txt` |
| `style/basemap.dark.json`      | Protomaps `dark` theme — the same 68 layers, same source, painted for night | as above | as above |
| `style/glyphs/NotoSans-*/0-255.pbf` | Noto Sans Regular / Medium / Italic, Basic Latin + Latin-1 | basemaps-assets | OFL (`glyphs/OFL.txt`) |
| `style/sprite/light*`          | Icon sheet the light style's symbol layers draw from | basemaps-assets v4 | as above |
| `style/sprite/dark*`           | The same 53 icons painted for the dark style | as above | as above |

**Two themes, one archive.** A PMTiles pack holds vector geometry with no colour in it, so light
and dark are two style documents over the same tiles — a rider switches with no download and no
second pack. `MapTheme` in `MapSource.cs` is the C# half; it reaches the module as
`options.source.theme` and is read by `offlineStyle()` alone, because the raster sources arrive as
finished images and have nothing to restyle. The glyphs are shared: a font carries no colour, and
one copy is the difference between the second theme costing ~290 KB and ~1 MB. `MapAssetRules`
asserts the dark style names no font stack the light one does not.

**To re-vendor either style**, take the prebuilt document from the npm package and apply the one
transform below — nothing else is edited:

```
curl -o basemap.json      https://unpkg.com/protomaps-themes-base@4.5.0/dist/styles/light/en.json
curl -o basemap.dark.json https://unpkg.com/protomaps-themes-base@4.5.0/dist/styles/dark/en.json
# then, in each: "Noto Sans Regular" -> "NotoSans-Regular", and the same for Medium and Italic
```

The sprites are `https://protomaps.github.io/basemaps-assets/sprites/v4/{light,dark}{,@2x}.{json,png}`,
taken verbatim.

**Font stack names carry no spaces, and that is load-bearing.** Upstream the style asks for
`"Noto Sans Regular"`; the vendored copy is rewritten to `"NotoSans-Regular"` and the glyph folders
are named to match. MapLibre substitutes `{fontstack}` into the glyphs URL **with no
URL-encoding**, so a name with spaces produces a request carrying literal spaces, which every
host's static-file handler then has to percent-decode identically — and one that does not returns
a 404 body, which MapLibre feeds to its protobuf decoder. The whole map then dies with
`Unimplemented type: 4`, naming neither the font nor the URL. `MapAssetRules` fails the build if a
space reappears.

**About the glyph ranges.** Only `0-255` ships, which is ASCII plus the Latin-1 accents — enough
for Australian and most western-European place names. A style renders *no* label at all without
the range it needs, so adding one is a drop-in: fetch
`https://protomaps.github.io/basemaps-assets/fonts/{stack}/{range}.pbf` into the same folder and it
is picked up with no code change. `256-511` (Latin Extended-A: Polish, Czech, Turkish) is the
obvious next one at ~128 KB per stack. Roads and coastlines draw regardless — a missing range
degrades rather than breaks.

**The style's three URLs are rewritten at runtime**, in `offlineStyle()`. Upstream they point at
`protomaps.github.io` and a placeholder tile server; the module swaps in the local glyphs, the
local sprite, and `pmtiles://` against this device's own archive. Attribution rides on the source
in the style document, so MapLibre's control renders it unchanged.

**These ship with the app; they are not fetched.** MapLibre used to come from jsDelivr, and that
was the single thing standing between this app and a map in a dead zone: the library was loaded on
first use, so a phone with no signal failed before it requested a tile. Downloaded tiles would not
have helped. Vendoring is therefore a prerequisite for offline maps rather than housekeeping, and
it also takes a runtime host dependency off the two online modes.

`map.maplibre.js` resolves them through `import.meta.url`, not a document-relative path — this
module is served from the shared library's static assets, and `script.src` / `link.href` resolve
against the *page*.

`MapAssetRules` in `tests/DLR.Architecture.Tests` fails the build if a module in this folder ever
references a package CDN or a font host again.

To update MapLibre: replace the two files and the licence, bump the version in this table and in
the comment at the top of `map.maplibre.js`, and check the map still draws — there is no lockfile
here to do it for you.

## Getting an archive to test with

The renderer is wired but there is nothing to point it at until a pack exists on a device — the
downloader is the next phase. To try it by hand, build a regional extract with the
[`pmtiles` CLI](https://github.com/protomaps/go-pmtiles):

```
pmtiles extract https://demo-bucket.protomaps.com/v4.pmtiles sydney.pmtiles \
    --bbox=150.5,-34.2,151.4,-33.5 --maxzoom=14
```

That source is verified: a PMTiles v3 planet archive, z0–15, `Accept-Ranges: bytes`. The extract
pulls only the ranges it needs, so it costs a fraction of the 137 GB behind it.

Drop the result on a device as
`{FileSystem.AppDataDirectory}/mappacks/{packId}/v1.pmtiles` — `FileMapPackStore` picks it up
from there, and `LoopbackMapPackServer` serves it to MapLibre.

`--maxzoom=14` is deliberate: above z14 MapLibre overzooms the vector data, which stays sharp
because it is vector. It is the single biggest lever on pack size and costs almost nothing
visually.

## Australian state packs

One pack per state and territory, ids matching `MapPackSummary.Id` in the offline-maps plan
(§4.2). Run these against the same source; each is independent, so they can be built one at a
time or all at once by `Build-AuMapPacks.ps1` (next section).

```
pmtiles extract https://demo-bucket.protomaps.com/v4.pmtiles au-nsw.pmtiles --bbox=140.99,-37.52,153.65,-28.15 --maxzoom=14
pmtiles extract https://demo-bucket.protomaps.com/v4.pmtiles au-vic.pmtiles --bbox=140.95,-39.20,150.00,-33.95 --maxzoom=14
pmtiles extract https://demo-bucket.protomaps.com/v4.pmtiles au-qld.pmtiles --bbox=137.99,-29.20,153.60,-9.10  --maxzoom=14
pmtiles extract https://demo-bucket.protomaps.com/v4.pmtiles au-sa.pmtiles  --bbox=128.95,-38.10,141.05,-25.95 --maxzoom=14
pmtiles extract https://demo-bucket.protomaps.com/v4.pmtiles au-wa.pmtiles  --bbox=112.90,-35.25,129.05,-13.50 --maxzoom=14
pmtiles extract https://demo-bucket.protomaps.com/v4.pmtiles au-nt.pmtiles  --bbox=128.95,-26.05,138.05,-10.90 --maxzoom=14
pmtiles extract https://demo-bucket.protomaps.com/v4.pmtiles au-tas.pmtiles --bbox=143.75,-43.90,148.55,-39.15 --maxzoom=14
pmtiles extract https://demo-bucket.protomaps.com/v4.pmtiles au-act.pmtiles --bbox=148.75,-35.95,149.42,-35.10 --maxzoom=14
```

`--bbox` is `minLon,minLat,maxLon,maxLat` — **longitude first**, and southern latitudes are
negative, so `minLat` is the *southern* edge. Getting the pair order wrong produces an empty
archive rather than an error, which is the failure mode to watch for.

| Pack id  | State / territory            | Extent covered                                          |
| -------- | ---------------------------- | ------------------------------------------------------- |
| `au-nsw` | New South Wales              | Cape Howe to Point Danger, SA border to Cape Byron       |
| `au-vic` | Victoria                     | Murray River to Wilsons Promontory, incl. Cape Howe      |
| `au-qld` | Queensland                   | NSW border to the Torres Strait islands                  |
| `au-sa`  | South Australia              | 129°E–141°E, 26°S to Cape Northumberland and Kangaroo Is |
| `au-wa`  | Western Australia            | Dirk Hartog Is to the NT border, Kimberley to Albany     |
| `au-nt`  | Northern Territory           | 129°E–138°E, SA border to the Tiwi and Wessel islands    |
| `au-tas` | Tasmania                     | King and Flinders islands to Pedra Branca                |
| `au-act` | Australian Capital Territory | The ACT proper; Jervis Bay falls inside `au-nsw`         |

**These boxes overlap.** A rectangle around NSW swallows the ACT outright and takes in roughly
half of Victoria and a slab of southern Queensland; `au-sa`, `au-nt` and `au-wa` share their
straight-line borders exactly. A rider who downloads two neighbouring packs pays for that overlap
twice. It is the accepted cost of `--bbox`: the alternative is `pmtiles extract --region
<state>.geojson`, which clips to the real boundary and would need state-boundary GeoJSON checked
in and maintained. Revisit that when pack size on a phone actually becomes the complaint.

External territories are deliberately absent — Lord Howe, Norfolk, Christmas, Cocos and Macquarie
Island are each far outside their state's box and want their own tiny pack if anyone ever rides
there.

`au-wa` and `au-qld` are by far the largest: each covers over 2.5× the area of NSW. If either
comes out too big to download over mobile data, split it by region (`au-wa-southwest`,
`au-wa-pilbara`, `au-wa-kimberley`) rather than dropping the max zoom, since z13 is a visible
step down on a map read through a visor.

## Building all eight

`Documentation/Build-AuMapPacks.ps1` runs the table above end to end and records what §4.2 step 3
asks for — size and SHA-256 per pack:

```powershell
./Build-AuMapPacks.ps1                              # all eight into ./mappacks
./Build-AuMapPacks.ps1 -Only au-nsw,au-act          # just these two
./Build-AuMapPacks.ps1 -OutDir D:\packs -Force      # rebuild over existing files
```

It skips packs that already exist unless `-Force`, and writes `catalogue.json` alongside them in
the shape of `MapPackSummary` — id, name, bounds, zoom range, size, hash, version and URL — ready
to serve from the VPS static directory. `-BaseUrl` sets the URL prefix written into that
catalogue.

The `pmtiles` CLI must be on `PATH`; the script fails immediately if it is not. A full run pulls
several GB of ranges from the source archive and takes a while — it prints each pack as it lands
rather than staying silent to the end.

## Credentials

**None.** MapLibre needs no key and OSM needs no account, which is why one registration line
answers for iOS, Android and the web. What OSM does require is permanent attribution — it is
declared on the tile source in `map.maplibre.js` so MapLibre's own `AttributionControl`
renders it, and removing it means removing the tiles.

## The tile source is temporary

`TILES` in `map.maplibre.js` points at `tile.openstreetmap.org`. That is a **donated service
whose usage policy does not cover a public launch** (§4.5, §13 Q26): it is right for
development and a handful of friends, and it moves to self-hosted PMTiles before the app is
publicly announced. That change is one constant and a style block in this module — and it is
also what makes an offline map pack possible, which none of the three removed providers could
have offered.
