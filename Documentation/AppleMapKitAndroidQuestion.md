# MapKit JS in an Android WebView — the policy question

**Status:** **Closed by decision, not by answer.** Rather than wait on Apple's
licensing clarification, the project has switched to a per-platform base-map choice
plus a single shared overlay for authored content. The AGPL §7 permission (§14.6.5)
already covers the proprietary linkage this introduces on Android.

Answered 2026-08-02.

## The decision

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

Both proprietary map JS SDKs need a credential. AGPL §14.2 already tells us what to do
with each:

| Credential | Where it lives | Never commit |
| --- | --- | --- |
| MapKit JS `.p8` private key (already recorded) | Server (§7.4-style secrets) | ✅ Already on §14.2 |
| **Google Maps API key (browser type, referrer-restricted)** | Config on the running instance, one value per environment; passed to the phone via `GET /api/v1/maps/google-key` (Phase 1) | ✅ Add to §14.2 |
| Apple team id, key id | Server config | ✅ Already on §14.2 |

The Google Maps key is a low-severity leak — bots harvesting it get a key restricted
to our bundle id, so it "works" for nobody but us — but committing it is still the
kind of leak that turns up on later scans and looks bad. The rule stays: user secrets
locally, environment in production (§14.3).

## The Skia overlay

One C# component in `BlazorDLR.Shared/Components/SkiaMapOverlay.razor`, backed by
`SkiaSharp.Views.Blazor`. It receives:

- the base map's current **viewport** — top-left latitude/longitude, bottom-right
  latitude/longitude, zoom, rotation — from a viewport-changed event the three base
  maps all emit,
- the current **markers** and **route** to draw.

It projects lat/lon to screen space using Web Mercator (`EPSG:3857`), which is what
every one of the three base maps uses natively, so the overlay's pixel is always over
the right tile pixel.

## Where the details land

- Design-outline revision entry: **v0.21** in `design-outline.md`.
- Front-end plan: rewritten §5 in `SharedFrontend.md`.
- Interface change: `IMapInterop` narrowed to the base map; new `IMapOverlay` for the
  shared drawing surface.
- Never-commit list: `design-outline.md` §14.2 gets a row for the Google Maps browser
  API key.

## Superseded questions

The original question at the top of this file — "does Apple permit MapKit JS in an
Android WebView" — is **no longer on the critical path**. The design does not need an
answer to it. Left here for the record; do not reopen unless a future maintainer
wants to consolidate to Apple Maps on both phones.
