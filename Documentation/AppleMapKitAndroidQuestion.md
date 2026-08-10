# MapKit JS in an Android WebView — the policy question

**Status:** **Moot.** MapKit JS is no longer used on any platform (v0.24, §4.5), so
the licensing question this file was opened to answer cannot arise. Retained for the
record, because the trajectory is the point.

Closed twice:

| | | |
|---|---|---|
| **v0.21** — 2026-08-02 | Closed *by decision, not by answer* | Rather than wait on Apple's licensing clarification, the project switched to a per-platform base map plus a single shared Skia overlay for authored content. |
| **v0.24** | Closed *by deletion* | Every host moved to MapLibre GL JS over OpenStreetMap. `map.mapkit.js`, `map.googlemaps.js`, `map.openlayers.js` and their interops are gone, along with the MapKit token endpoint and both map credentials. |

**Why v0.21's answer did not last.** It was correct about the hard part — the Skia
overlay is what made the base map interchangeable — and wrong about what followed from
it. Once the overlay drew every marker, pin and route, the three base maps were being
kept for the one job all three did identically: tiles and gestures. Each was still
charging separately for it, and two of the three charged in credentials. v0.24 is
v0.21's own argument carried one step further.

**What this closes for good.** MapLibre GL JS is BSD-2-Clause. There is no per-platform
licensing question, no vendor whose terms could change under the Android build, and no
AGPL §7 linkage to reason about — the §14.6.5 permission still exists for the platform
SDKs the app genuinely links (MAUI, the car heads), and no longer has to cover a map.

## The v0.21 decision, as recorded at the time

| Surface | Base map | Base map runs where | Notes |
| --- | --- | --- | --- |
| **iOS** | **Apple Maps** via MapKit JS inside the `BlazorWebView` | JavaScript, in the WebView Blazor Hybrid already hosts | Unambiguously licensed on iOS. Server-minted JWT (§4.5) as designed. |
| **Android** | **Google Maps** via the Google Maps JavaScript API inside the `BlazorWebView` | JavaScript, same WebView as iOS | Requires a Google Cloud project and a browser API key restricted to the app's referrer / bundle id (§14.2's rules apply). |
| **Web** | **OpenLayers** on OpenStreetMap tiles | JavaScript, in the browser | Same OSM caveat (§4.5, §13 Q26) as MapLibre had — self-hosted tiles before public launch. |
| **Car heads (§4.6)** | **Mapsui / SkiaSharp** into a raw `Surface` | Native, no WebView | Unchanged. |

**One shared Skia overlay** draws every rider pin, every marker, every track. The base
map handles pan / zoom / rotate; the overlay handles authored content, in one C# file
running against `SkiaSharp` — which is already in the dependency graph via Mapsui
(§14.6.3) and already the one image path (§16.4).

## Why this is better than the earlier answer

- **No dependency on an Apple licensing clarification** that could come back "no" a
  month before Play Store submission. The Android base map is a first-class Google
  product on Android — the exact place it is meant to run.
- **The failure mode of *"two providers drift"* moves from marker rendering to base
  tiles.** Base tiles are Apple's / Google's / OSM's problem; the overlay is one file
  drawing the same pixels everywhere. Since v0.9 the design has warned that two map
  code paths would drift on marker rendering (§4.5), and this splits the surface so
  the shared half is exactly the half we own.
- **The Phase 0 spike gets simpler.** The measurement that was "MapKit JS in an
  Android WebView" — a question with three unknowns wired together — becomes "does
  the base map render smoothly" (one unknown per platform, and every platform's
  vendor already measured it) plus "does the Skia overlay hold 60 fps with 20 pins"
  (one measurement that answers for all three surfaces at once).

## What is committed and what is not

*(v0.21's answer, superseded by v0.24 — see below.)*

Both proprietary map JS SDKs need a credential. §14.2 already tells us what to do with
each:

| Credential | Where it lives | Never commit |
| --- | --- | --- |
| MapKit JS `.p8` private key (already recorded) | Server (§7.4-style secrets) | ✅ Already on §14.2 |
| **Google Maps API key (browser type, referrer-restricted)** | Config on the running instance, one value per environment; passed to the phone via `GET /api/v1/maps/google-key` (Phase 1) | ✅ Add to §14.2 |
| Apple team id, key id | Server config | ✅ Already on §14.2 |

**v0.24 deleted every row in that table.** The `.p8`, the team and key ids, the Google
browser key and the `google-key` endpoint that was going to deliver it were all removed
along with the two proprietary SDKs. MapLibre needs no credential on the server or the
client, so §14.2's map rows are gone rather than tightened — and the Phase 1 work to
build a second credential-delivery endpoint was never done.

## The Skia overlay

One C# component in `BlazorDLR.Shared/Components/SkiaMapOverlay.razor`, backed by
`SkiaSharp.Views.Blazor`. It receives:

- the base map's current **viewport** — top-left latitude/longitude, bottom-right
  latitude/longitude, zoom, rotation — from a viewport-changed event the base map
  emits (three modules emitted it identically under v0.21; one does under v0.24),
- the current **markers** and **route** to draw.

It projects lat/lon to screen space using Web Mercator (`EPSG:3857`), which is what
every one of the three base maps uses natively, so the overlay's pixel is always over
the right tile pixel.

## Where the details land

**v0.21:**

- Design-outline revision entry: **v0.21** in `design-outline.md`.
- Front-end plan: rewritten §5 in `SharedFrontend.md`.
- Interface change: `IMapInterop` narrowed to the base map; new `IMapOverlay` for the
  shared drawing surface.
- Never-commit list: `design-outline.md` §14.2 gets a row for the Google Maps browser
  API key.

**v0.24:**

- Design-outline revision entries: **v0.24** in `design-outline.md`, and §4.5 rewritten.
- Front-end plan: §5 and the Phase 0 spike records in `SharedFrontend.md`.
- Interface change: none. `IMapInterop`, `IMapOverlay` and `MapBridge` are unchanged —
  which is the evidence that v0.21 drew the seam in the right place.
- Never-commit list: §14.2 **loses** both map rows.

## Superseded questions

The original question at the top of this file — "does Apple permit MapKit JS in an
Android WebView" — cannot arise: no host runs MapKit JS. Left here for the record.

**Do not reopen this to consolidate onto Apple Maps.** The reasons it was rejected are
in §4.5 and they got stronger, not weaker: a `.p8` on the server, a token endpoint, an
origin claim that the MAUI `BlazorWebView` (`app://0.0.0.0`) cannot satisfy, and no
offline mode at any price — for a base map that draws tiles and nothing else.
