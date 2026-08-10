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
