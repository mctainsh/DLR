# Map JS modules

Two ES modules, one per provider (§4.5, §18.3). Both implement the same interop
contract that lives on the C# side as `IMapInterop` in
`BlazorDLR.Shared/Services/IMapInterop.cs`:

| File                | Provider   | Bound in                                | Tiles                 |
| ------------------- | ---------- | --------------------------------------- | --------------------- |
| `map.mapkit.js`     | Apple Maps | `BlazorDLR/Services/MapKitInterop.cs`   | Apple's — MapKit JS   |
| `map.maplibre.js`   | MapLibre   | `BlazorDLR.Web.Client/Services/…`       | OpenStreetMap         |

## Contract

Each module exports a single factory:

```js
export async function createMap(hostElement, options, callbacks) { ... }
```

Returning an object with:

- `setCamera(camera)`
- `setRoute(route | null)`
- `upsertMarker(marker)`
- `removeMarker(id)`
- `dispose()`

The C# side (`IMapInterop`) mirrors this shape one-to-one. Phase 0 leaves both
modules as skeletons that render a placeholder — the real implementations arrive
in Phase 1 alongside `RideMap.razor`.

**MapKit JS** needs a short-lived JWT minted by `GET /api/v1/maps/token`; the
interop implementation on the C# side fetches it via `IApiClient` and passes it
through `options.token`. The private `.p8` key never reaches this file (§4.5).

**MapLibre** needs no key. It needs a permanent OSM attribution string and an
identifying `User-Agent` on the tile requests, both of which are that module's
responsibility.
