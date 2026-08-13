# Offline maps — implementation plan

**Status:** phases 0, 1 and 2 shipped. Phase 3 — the downloader that actually puts an archive on a
device — is next. **Supersedes nothing**; this is the plan §4.5 and §13 Q26 have been pointing at
since v0.24 unblocked it.

The rider this is for is at a trailhead with no signal. Since the offline ride cache landed they
get their ride, their group's last known positions, the markers and the planned routes — over a
blank grey rectangle. This closes that.

---

## 1. What exists today

| Piece | Where | Note |
| --- | --- | --- |
| Base-map seam | `BlazorDLR.Shared/Services/IMapInterop.cs` | Kept through v0.24 *for this*. One implementation, three hosts. |
| `MapProvider` enum | same file | One member. Exists because "the attribution obligation is per tile source, not per app". |
| The only map module | `BlazorDLR.Shared/wwwroot/map/map.maplibre.js` | `styleFor(source)` builds a raster or a vector style from the rider's choice. |
| MapLibre GL JS 4.7.1 | ~~jsDelivr CDN~~ → `wwwroot/map/lib/maplibre/` | Vendored in phase 0. Was the blocker: the library was fetched on first use, so a dead zone failed before a tile was requested. |
| Everything authored | `SkiaMapOverlay.razor` | Pins, routes, circles. **Unaffected by any of this.** |
| Device preferences | `IDeviceSettings` + `RouteStyleState` pattern | Versioned string encodings, read once, broadcast on change. |
| Device file blobs | `IOfflineStore` → `FileOfflineStore` | `FileSystem.AppDataDirectory/offline/`. Temp-then-move writes. |

Two facts shape everything below.

**The base map is only tiles, camera, rotation and attribution.** Every marker, rider pin and route
is Skia on top, projecting flat Web Mercator from a `MapViewport`. So changing the tile source
changes what is *under* the overlay and nothing else — no screen, no contract, no test outside the
map module has to move.

**The map was not offline-capable even with tiles on disk**, because `map.maplibre.js` fetched the
library itself from jsDelivr on first use — a phone in a dead zone failed at `loadMapLibre()`,
before a tile was ever requested. Phase 0 closed that, and `MapAssetRules` stops it coming back.

---

## 2. The choice the rider makes

One setting, three kinds, on a new **Settings → Maps** screen.

| Kind | What it is | Where it works |
| --- | --- | --- |
| `Offline` | A PMTiles archive downloaded to this device | Phone only (§18.6) |
| `Osm` | `tile.openstreetmap.org` raster — today's behaviour, and the default | Every host |
| `Custom` | A rider-supplied XYZ raster template + attribution | Every host |

Modelled exactly like `RouteStyle` (§18.6): a record with a versioned hand-rolled encoding, one
`IDeviceSettings` key, one scoped state object that reads it once and broadcasts changes.

```csharp
public enum MapSourceKind { Osm = 0, Offline = 1, Custom = 2 }

public sealed record MapSource(
	MapSourceKind Kind,
	string? PackId,          // Offline: which downloaded archive
	string? UrlTemplate,     // Custom: https://…/{z}/{x}/{y}.png
	string? Attribution,     // Custom: required, see §5
	int MaxZoom)
{
	public const string StorageKey = "dlr.map-source";
	public static MapSource Default { get; } = new(MapSourceKind.Osm, null, null, null, 19);
	public string Encode();                      // "1|osm|||19"
	public static MapSource? Decode(string? s);  // all-or-nothing, like LiveMapView
}
```

`MapSourceState` mirrors `RouteStyleState` — and does one extra thing: **it degrades `Offline` to
`Osm` when `IOfflineStore.IsSupported` is false.** A browser has no pack store, and a rider who set
offline on their phone and then opens the site must get a working map rather than a blank one.

`MapOptions` gains a `MapSource Source`; `map.maplibre.js` replaces `osmRasterStyle()` with
`styleFor(source)`. `MapProvider` gains `Pmtiles` and `CustomRaster` so the attribution obligation
stays visible in the type, which is why that enum was kept.

---

## 3. Phase 0 — make the map work with no network at all — **DONE**

Nothing else in this plan matters until this is done. All of it is vendoring, in the pattern
`wwwroot/lib/bootstrap` and `wwwroot/lib/fontawesome` already established.

| Item | Destination | Size | Status |
| --- | --- | --- | --- |
| `maplibre-gl.js` (UMD) v4.7.1 | `wwwroot/map/lib/maplibre/` | 784 KB | shipped |
| `maplibre-gl.css` v4.7.1 | `wwwroot/map/lib/maplibre/` | 64 KB | shipped |
| `LICENSE.txt` (3-Clause BSD) | `wwwroot/map/lib/maplibre/` | 6 KB | shipped |
| `pmtiles` v3 UMD (protocol handler) | `wwwroot/map/lib/pmtiles/` | ~35 KB | **moved to phase 2** |
| Glyph PBFs, sprite, style JSON | `wwwroot/map/style/` | ~3–5 MB | **moved to phase 2** |

`loadMapLibre()` and `ensureStylesheet()` now resolve through `import.meta.url` rather than a CDN
base — `script.src` and `link.href` resolve against the *page*, and this module is served from the
shared library's static assets. The retry-and-clear behaviour stays: a local bundle can still fail
to parse, and on the web it is served over HTTP like anything else and can 404 behind a stale
cache, so the stated-error branch in `RideMap.razor` is still the right answer.

**Why the last two rows moved.** They are inert until a *vector* style exists, and the only thing
that introduces one is reading a PMTiles archive — which is phase 2. Shipping 3–5 MB of glyph
ranges now would be dead weight in every install, and the exact set to ship (which font, which
Unicode ranges) is not knowable until the style is chosen. Both land with the code that reads
them.

**Glyphs will still be the one that surprises people.** A vector style renders no labels at all
without PBF glyph ranges, and MapLibre fetches them per range from `style.glyphs`. Latin-only keeps
this to a few MB; the full CJK set is ~100 MB and is out of scope. Roads and coastlines render fine
without labels, so a missing range degrades rather than breaks.

App package grew by ~850 KB rather than the ~5 MB estimated, because the glyph bundle moved out.
That cost is paid by every install including riders who never turn offline on — and it removes the
CDN as a runtime dependency for the *online* modes too, which §4.5 lists as an outstanding cost.
Worth it on that alone.

**`MapAssetRules` guards it.** A new architecture rule fails the build if any module under
`wwwroot/map/` references a package CDN or font host, and asserts the vendored bundle and its
licence are present. The regression it prevents is invisible on a desk, on CI and in a simulator,
and shows up only as "the map is blank" from a rider at a trailhead.

---

## 4. Offline maps

### 4.1 Format: vector PMTiles

**PMTiles**, single file, read by HTTP range request. MapLibre reads it through the `pmtiles://`
protocol plugin with no tile server involved — which is the property that makes this a tile-source
question rather than an SDK question (§4.5).

**Vector, not raster.** Size is the entire constraint and it is not close: a raster extract of NSW
to z16 is tens of GB; the equivalent vector extract is under 1 GB. The cost is Phase 0's glyph and
sprite bundle, paid once in the app package. Vector also restyles for free, which matters for a map
read through a visor.

Zoom range **z0–z14** for a regional pack. Above z14 vector tiles are "overzoomed" by the renderer —
MapLibre keeps drawing z14 data at z15–18, which stays sharp because it is vector. This is the single
biggest size lever available and it costs almost nothing visually.

### 4.2 Where packs come from

The DLR server, over the infrastructure §9.1 already describes.

**Build** (offline, on a workstation, not on the VPS):

1. Take a Protomaps daily planet build, or self-build with `planetiler` from a Geofabrik extract.
2. `pmtiles extract <source> <out>.pmtiles --bbox=<region> --maxzoom=14`.
3. Record size and SHA-256.

**Serve:** the archive goes in the VPS static directory and **Caddy's `file_server` serves it
directly**. Caddy handles HTTP range requests natively, which is exactly why §9.1 chose PMTiles over
a setup needing object storage and a Worker. Kestrel never sees the bytes, so a multi-gigabyte
download does not occupy an app thread or count against its rate limits.

**Catalogue:** a small authed API so the client can discover what exists without hardcoding it.

```
GET /api/v1/map-packs        →  IReadOnlyList<MapPackSummary>
```

```csharp
// DLR.Core/Contracts/Maps/MapPackSummary.cs
public sealed record MapPackSummary(
	string Id,              // "au-nsw"
	string Name,            // "New South Wales"
	TrackBounds Bounds,
	int MinZoom, int MaxZoom,
	long SizeBytes,
	string Sha256,
	int Version,            // bumped when the extract is rebuilt
	string Url);            // absolute, Caddy-served
```

### 4.3 How a pack is downloaded

New seam `IMapPackStore` (MAUI: files under `FileSystem.AppDataDirectory/mappacks/`; both browser
hosts: unsupported, same shape as `UnavailableOfflineStore`). **App data, not `CacheDirectory`** —
the OS reclaims that one, and the whole point is the rider who cannot refetch.

`MapPackDownloader` (shared, driven by a scoped `MapPackState` the settings screen renders):

1. `HttpClient` streamed read → `{id}.v{n}.pmtiles.part`, never buffering the body in memory.
2. **Resumable.** On restart, `Range: bytes={existing length}-`; a `206` continues, a `200` means
   the server ignored it and the part file is truncated first.
3. SHA-256 computed incrementally as bytes land. Mismatch on completion ⇒ the part file is deleted
   and the download reports failure. A corrupt archive must never reach the map.
4. `File.Move(part, final, overwrite: true)` last — the same temp-then-move discipline
   `FileOfflineStore` already uses, for the same reason.
5. Old versions of the same pack id are deleted only after the new one lands.

**Wi-Fi only by default**, via `Connectivity.Current.ConnectionProfiles`, with an explicit "download
on mobile data" override. A rider who starts a 700 MB download on a phone plan by accident has a
real complaint.

**Surviving the background** is the platform-specific part and the honest risk:

- **Android** — a foreground service, or `WorkManager` with a foreground worker. The app already
  ships a foreground service for location, so the notification channel and the permission are
  precedent rather than new ground.
- **iOS** — only `NSURLSession` background download tasks survive suspension, and MAUI does not
  expose them. Either accept **foreground-only downloads on iOS** for v1 (screen on, app in front,
  with the wake lock we already have), or write a platform implementation. Recommend accepting the
  limitation first and stating it on the screen.

**Management UI** on Settings → Maps: available packs with size, downloaded packs with version and
disk used, progress with cancel, delete, and total disk consumed.

### 4.4 How MapLibre reads a local file

**This is the main architectural decision in the plan.** `pmtiles://` wants a URL; the archive is a
file on disk that the WebView cannot address.

| | Approach | Verdict |
| --- | --- | --- |
| **A** | **Loopback HTTP server.** Kestrel or `HttpListener` bound to `127.0.0.1:0`, serving `/{token}/{id}.pmtiles` with `Accept-Ranges` and `206`. MapLibre fetches it like any URL. | **Recommended.** One implementation for both platforms, real range support for free, no WebView internals. |
| **B** | **Custom WebView scheme.** `WKURLSchemeHandler` on iOS, `shouldInterceptRequest` on Android, serving `dlr-pack://`. | Fallback. No port, but two platform implementations, hand-rolled range parsing, and it hooks `BlazorWebView` internals that move between MAUI releases. |
| **C** | **Interop tile pump.** C# reads ranges and returns `byte[]` to a MapLibre `addProtocol` handler. | Rejected. Every tile crosses the JS boundary base64-encoded — roughly 20 tiles per view change, on every pan. |

Approach A costs two pieces of configuration: Android needs a network-security-config exception for
`127.0.0.1` (cleartext to loopback only, not a blanket `usesCleartextTraffic`), and iOS needs
`NSAllowsLocalNetworking` under ATS. The random path token stops another app on the device reading
the archive through the port.

---

## 5. Online sources

### 5.1 OpenStreetMap — unchanged, still the default

Today's behaviour, moved behind the setting. **None of this displaces §13 Q26.** OSM's tile policy
still forbids heavy use, the tile source still has to move before public announcement, and offline
packs do not change that — the web host and any rider who leaves the default on still hit
`tile.openstreetmap.org`. Attribution stays declared on the source so MapLibre's own control renders
it.

### 5.2 Custom raster — the mechanism, with Google as a documented case

A rider-supplied XYZ template, validated for `{x}` `{y}` `{z}` (and optional `{s}` subdomain), plus
a **required** attribution string that goes onto the MapLibre source exactly as OSM's does.

You asked specifically for `http://mt1.google.com/vt/lyrs=m&x={x}&y={y}&z={z}`. Four things about it
are worth having in writing before the work starts:

- **`mt1.google.com/vt` is an undocumented internal endpoint.** Google Maps Platform's terms require
  tiles to come through the official APIs with a key and billing attached, and using this path is
  outside them. It can also change or start refusing requests without notice.
- **`http://` will not load.** The app is served over a secure scheme on both phones and over HTTPS
  on the web, so this is mixed content and the WebView blocks it — before iOS ATS and Android's
  cleartext policy get a say. It must be `https://mt1.google.com/…`, which does work.
- **Attribution is not satisfied** by the ODbL string, and Google's terms have their own
  requirements.
- **Store review** is a plausible rejection path on both platforms.

So the recommendation is: **ship the mechanism, not the preset.** The custom field is genuinely
useful — a self-hosted tile server, Thunderforest, Stadia or MapTiler with the rider's own key — and
a rider who types the Google URL into it has made their own decision on their own device. Shipping
it as a built-in option makes it ours. If you want it as a preset anyway, say so and I will add it;
the code path is identical either way.

---

## 6. Work breakdown

| Phase | Work | Rough size |
| --- | --- | --- |
| **0** ✅ | Vendor MapLibre + CSS + licence; resolve through `import.meta.url`; `MapAssetRules` guard. | done |
| **1** ✅ | `MapSource`, `MapSourceState`, `IDeviceSettings` key, `MapOptions.Source`, `SetSourceAsync` + `styleFor()`/`setSource` in the JS module, `MapProvider` members, Settings → Maps page with the three options, custom fields and a live preview. **Options 2 and 3 ship.** | done |
| **2** ✅ | Vendored `pmtiles` + Protomaps `light` style, glyphs and sprite. `IMapPackStore` + host bindings, `LoopbackMapPackServer`, Android/iOS network config, `pmtiles://` wiring. **Renderer complete; needs an archive to draw.** | done |
| **3** | Server: extract build script, Caddy static path, `MapPackSummary`, `GET /api/v1/map-packs`. Client: `MapPackDownloader`, `MapPackState`, pack management UI, Android foreground download. **This is what puts an archive on a device.** | 4–5 days |
| **4** *(optional)* | **Ride-scoped packs** — see below. | 3–4 days |

Phases 0–1 are independently shippable and deliver two of your three options. Phase 4 is where this
gets genuinely good for the product.

### Phase 4 — ride-scoped packs

A regional pack is 0.5–3 GB. A pack covering **one ride's route corridor** — the union of the ride's
`TrackBounds` plus a 10 km buffer, z0–14 — is 20–80 MB. That is a download a rider will actually
accept the night before, and it is precisely the ground they need.

`POST /api/v1/group-rides/{id}/map-pack` runs `pmtiles extract --bbox` against the regional archive,
caches the result by bbox hash so a ride's members share one build, and returns a `MapPackSummary`.
The ride's info page offers "Download map for this ride" beside the routes it already lists.

This is the version of the feature that matches what the app is for. The regional packs in Phase 3
are the fallback for a rider who wants a whole state on the phone regardless of any one ride.

---

## 7. Testing

| Suite | What |
| --- | --- |
| `DLR.UI.Tests` | `MapSource` encode/decode round trip; unknown kind and malformed values fall back to `Default`; `MapSourceState` degrades `Offline` → `Osm` when `IOfflineStore.IsSupported` is false. |
| `DLR.UI.Tests` | bUnit on Settings → Maps: three options render, Offline is absent on a host with no pack store, Custom refuses a template missing `{z}` and refuses an empty attribution. |
| `DLR.UI.Tests` | `MapPackDownloader` over a fake HTTP handler: resumes from a partial file with the right `Range`, truncates when the server answers `200`, discards on SHA-256 mismatch, leaves no `.part` behind on failure. |
| `DLR.Architecture.Tests` | New rule, in the shape of `SqlRules`/`ImageRules`: **tile URL templates appear only in `MapSource` and the map JS module.** Stops the next person hardcoding a tile host into a page. |
| `DLR.Server.Tests` | Catalogue endpoint shape and auth; a `Range` request against the pack path answers `206` with the right slice. |

No test needs a real archive: the loopback server and the downloader are both tested against
fixtures of a few kilobytes.

---

## 8. Decisions needed before Phase 3

1. **Which regions ship first?** Australia by state is the obvious cut. Each is a disk cost on the
   40 GB VPS (§9.1) and a build to maintain.
2. **Vector or raster?** The plan assumes vector. Raster skips the glyph bundle entirely and is far
   simpler, at roughly 20× the size — defensible only if packs are always ride-scoped.
3. **iOS background downloads** — accept foreground-only for v1, or build the `NSURLSession`
   platform implementation?
4. **Google as a shipped preset** — mechanism only (recommended), or preset included?
5. **Does §13 Q26 land first?** Building extracts is most of the work of moving the *online* tile
   source to self-hosted PMTiles. Doing them together is meaningfully cheaper than doing them apart;
   §4.5 already says "the same work, not two projects".
