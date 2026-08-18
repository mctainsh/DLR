# Dumb Luck Rides — Shared Front End Design

> **Status:** Draft **v0.1** — a build plan for the Web (`BlazorDLR.Web.Client`) and Mobile (`BlazorDLR`) clients on top of the shared library (`BlazorDLR.Shared`). Reads §18 of `design-outline.md` as the source of truth and translates it into work.
>
> **Scope:** the UI layer only. `DLR.Server`, `DLR.Core`, `DLR.Server.Migrations` are not changed by this document; they are consumed. The car heads (§4.6) are out of scope — they are native MAUI templates and share no code with the two hosts described here.

## 1. The rule that decides every question below

**One Razor Class Library, two hosts.** Every screen is a component in `BlazorDLR.Shared`, compiled unmodified into:

- **`BlazorDLR.Web.Client`** — Blazor WebAssembly, served by the `BlazorDLR.Web` host.
- **`BlazorDLR`** — a MAUI Blazor Hybrid app for **Android and iOS** in a single project.

`BlazorDLR.Shared` is what already exists in the solution — the `Weather.razor` sample is the shape every real screen follows. If a screen cannot live there, either an abstraction is missing or the screen belongs in a host-specific folder for a reason we can name in one sentence.

The two rules the architecture test in `DLR.Architecture.Tests` will enforce (§18.2, §10.4):

1. **`BlazorDLR.Shared` references no MAUI assembly and no platform API.** It compiles into WebAssembly and any reference that breaks that guarantee is a build failure.
2. **No `#if ANDROID` / `#if IOS` in a shared component.** Conditional compilation in a shared library is two libraries wearing one name; the correct move is always an interface with a per-host registration.

Everything host-specific reaches shared code through an interface. `IFormFactor` — already in `BlazorDLR.Shared/Services/` with implementations in each host — is the pattern.

## 2. What the two clients do and do not do

Section 4.1 of the design outline names 11 screens (§4.1); this table maps them to the hosts and calls out per-host divergences.

| Screen (§4.1) | Shared component | Mobile-only | Web-only |
|---|---|---|---|
| Welcome — register or sign in | ✅ | Skippable (§7.9) | Redirects to landing when signed out |
| Home / Ride | ✅ | Foreground service, GPS provider, big start/stop | Read-only stat panel; recording is not offered |
| My Rides | ✅ | Local + synced, GPX export/share, GPX import (§15.2) | Synced only, GPX import via drag-drop |
| Group Rides | ✅ | Join, request, create | Same |
| Ride Requests (organiser) | ✅ | — | — |
| Group Ride Live | ✅ | Publishes position, background service | Read-only spectator or member; no publish (§18.6) |
| Add Marker | ✅ | Camera via `MediaPicker` | File via `<InputFile>` |
| Ride Thread | ✅ | Local notifications (§17.6) — no FCM, no APNs | Silent — none in v1 (§18.2) |
| Route Planner | ✅ | GPX pick, past ride pick | Drag-drop GPX, big-screen mouse drawing (§6.1) |
| Settings | ✅ | Device list, GPS profile | Device list, session limits (§18.5) |
| Settings → Profile | ✅ | Same | Same |
| Track editor (§15.5) | ✅ | Same — the composer stacks on a narrow screen | Same |
| Auth landing (confirm, reset) | ❌ static-rendered Razor in `BlazorDLR.Web` (§7.5) | — | Must work without WASM booted |
| **Car heads (§4.6)** | ❌ | Native templates in `BlazorDLR/Platforms/`, Android Auto + CarPlay | — |

**The track editor is no longer asymmetric.** §6.1 and §13 Q15 originally made it web-only: trimming was judged to need a mouse and a big map, so the phone rendered the page with the composer replaced by a note pointing at a desktop browser. That gate is gone. The screen a rider actually has at the end of a ride is the phone, and cutting the drive home off a track is exactly the edit they want there — "open a laptop first" is a worse answer than a cramped one. The page is one shared component reached through the same router on every host (`BlazorDLR.Shared/Routes.razor`), and its narrow-screen layout stacks the trim controls rather than hiding them.

**The composer is a cursor, not a pair of index boxes.** Typing "from 412 to 480" was a mouse-and-keyboard affordance that also asked the rider to read indices off a map that does not show them. The rider now taps the line to drop a cursor on a raw point, then bites 1 or 10 points off it, backwards or forwards, repeatedly. A bite takes the cursor point with it and leaves the cursor on the new edge, so holding a button chews along the track; leaving the cursor standing and jumping it across the gap instead strands the point it vacated between two holes, and leaving it standing without moving it re-measures the same span every press. Trims accumulate in a client-side `TrackTrimSession` (`BlazorDLR.Shared/Tracks/`) and are undoable one step at a time back to the track as loaded; nothing reaches `POST /tracks/{id}/edit` until Apply, which is confirmed and permanent. The server's retained original (§15.6) still exists and its endpoints are unchanged, but the page no longer offers undo or purge after a commit — an undo button that appears past the point of no return is one that gets trusted at the wrong moment.

**Offline is a mobile property, not a shared one.** `BlazorDLR.Shared` renders whatever `IRideRepository` returns; the mobile host binds it to SQLite and the outbox (§18.6), the web host binds it to HTTP (§4.4). A component that assumes one or the other is a component that has picked a side, and that is the failure mode the abstraction exists to prevent.

## 3. What lives where

```
BlazorDLR.Shared/                    net10.0, browser platform, no MAUI, no platform APIs
├── Pages/                           every screen from §4.1, one .razor per screen
│   ├── Welcome.razor                Register / sign in (§7.2)
│   ├── Home.razor                   Start / stop, live stats
│   ├── Rides/
│   │   ├── MyRides.razor
│   │   ├── RideDetail.razor
│   │   ├── GroupRides.razor
│   │   ├── GroupRideCreate.razor
│   │   ├── GroupRideLive.razor      Map + members + markers + thread launcher
│   │   ├── GroupRideRequests.razor  Organiser only
│   │   └── RoutePlanner.razor
│   ├── Markers/
│   │   └── MarkerEditor.razor
│   ├── Thread/
│   │   ├── RideThread.razor
│   │   └── PollComposer.razor
│   ├── Settings/
│   │   ├── Settings.razor
│   │   ├── Profile.razor
│   │   ├── Devices.razor
│   │   └── DataAndExport.razor
│   ├── Tracks/
│   │   └── TrackEditor.razor        Only routed by the web host (§6.1)
│   └── Auth/
│       ├── ForgotPassword.razor
│       └── ResetPasswordApp.razor   In-app; the SSR landing (§7.5, §7.7) is separate
├── Components/
│   ├── RideMap.razor                One component, two JS modules behind IMapInterop (§4.5)
│   ├── RideMap.razor.cs
│   ├── MarkerLayer.razor            Renders §16 markers on top of RideMap
│   ├── MemberList.razor             With sharing/no-signal distinction (§5.6)
│   ├── CommentItem.razor
│   ├── ReactionBar.razor            Coalesced counts (§17.4)
│   ├── PollCard.razor
│   ├── SharedProfilePreview.razor   "What other riders see" (§7.3)
│   ├── ConsentPrompt.razor          Join-time location prompt (§5.6)
│   ├── SourceOfferFooter.razor      AGPL §13 (§14.6.2)
│   └── ...
├── Services/
│   ├── IFormFactor.cs               Exists; the pattern for every interface below
│   ├── IApiClient.cs                REST wrapper over DLR.Core.Contracts
│   ├── IRideHubClient.cs            SignalR client abstraction
│   ├── ITokenStore.cs               Keychain on mobile, cookie via API on web (§18.5)
│   ├── IRideRepository.cs           SQLite (mobile) or HTTP (web) (§18.6)
│   ├── ILocationProvider.cs         GPS; browser has none — see §5 below
│   ├── IMediaPicker.cs              Camera / file (§18.2)
│   ├── IMapInterop.cs               JS interop contract for both map modules (§4.5)
│   ├── INotificationService.cs      Local notifications on mobile; no-op on web (§17.6)
│   ├── LocalNotification.cs         Tag / Title / Body / Route — never crosses the wire
│   └── ...
├── Layout/                          MainLayout.razor + NavMenu.razor exist; keep the shape
├── State/
│   ├── AuthState.cs                 AuthenticationStateProvider integration
│   ├── ActiveRideState.cs           IRideSessionState client-side view (§4.6, §5.7)
│   └── ThreadState.cs               Coalesced reaction / poll updates
├── Routes.razor                     Existing; extended with the new pages
├── _Imports.razor                   Existing; add DLR.Core.Contracts.* usings
└── wwwroot/                         map/ (map.maplibre.js, interop.js), component CSS,
                                     icon sprites for the §16.2 curated set

BlazorDLR.Web.Client/                Blazor WASM
├── Program.cs                       Existing; adds HTTP-based service registrations
├── Services/
│   ├── FormFactor.cs                Exists; "Web / Desktop"
│   ├── HttpApiClient.cs             IApiClient over HttpClient
│   ├── CookieTokenStore.cs          Cookie is HttpOnly, so this is a stub (§18.5)
│   ├── HttpRideRepository.cs        IRideRepository over the API
│   ├── NoopLocationProvider.cs      ILocationProvider that says "not supported"
│   ├── BrowserMediaPicker.cs        InputFile-based
│   ├── MapLibreInterop.cs           Selects wwwroot/map.maplibre.js
│   └── (notifications)              NoopNotificationService — none in the browser (§18.2)
└── wwwroot/appsettings.json         Existing; api base URL etc.

BlazorDLR/                           MAUI single project — Android + iOS
├── MauiProgram.cs                   Existing; add mobile registrations below
├── Components/
│   └── (BlazorWebView root already in place — one WebView, hosts BlazorDLR.Shared)
├── Services/
│   ├── FormFactor.cs                Exists; "Phone / Tablet"
│   ├── HttpApiClient.cs             Shared shape; different registration only
│   ├── SecureStorageTokenStore.cs   MAUI SecureStorage → Keychain/Keystore (§7.4)
│   ├── SqliteRideRepository.cs      Uses DLR.Core's SQLite path (§18.6)
│   ├── MauiMediaPicker.cs           MediaPicker.PickPhotoAsync
│   └── ...
├── Platforms/
│   ├── Android/
│   │   ├── LocationProvider.cs      FusedLocationProvider + foreground service (§4.3)
│   │   ├── ForegroundLocationService.cs
│   │   ├── Notifications/AndroidNotificationService.cs   NotificationManagerCompat — no FCM
│   │   └── Auto/                    CarAppService — native, not shared (§4.6)
│   └── iOS/
│       ├── LocationProvider.cs      CLLocationManager, allowsBackground = true (§4.3)
│       ├── Notifications/AppleNotificationService.cs     UNUserNotificationCenter — no APNs
│       ├── Notifications/ThreadNotificationDelegate.cs   Foreground presentation + tap routing
│       └── CarPlay/                 CPTemplateApplicationSceneDelegate — native

tests/
├── DLR.Architecture.Tests/          Extend the existing project (§18.8)
│   ├── UiLayeringRules.cs           NEW — the two rules from §1 above
│   └── SharedFrontendRules.cs       NEW — one MapHostKind factory per kind, etc.
└── DLR.UI.Tests/                    NEW — bUnit; renders every shared component
    ├── Pages/
    ├── Components/
    └── Services/                    Fake IApiClient, IRideRepository, IMapInterop
```

The physical layout above uses the existing `BlazorDLR.Shared/Pages`, `Layout`, `Services` and `wwwroot` folders unchanged, adds `Components`, `State`, and subfolders under `Pages` for grouping. No new project is added except `DLR.UI.Tests` when Phase 4 of §7 begins.

## 4. The shared abstractions — one interface per platform seam

The list is short deliberately. Every abstraction below is used by shared code and has one implementation per host. Anything not on this list either does not exist or is host-specific by design.

| Interface | Mobile impl | Web impl | Called by |
|---|---|---|---|
| `IApiClient` | `HttpClient` + auth handler | `HttpClient` + credentials-include | Every page that reads or writes server data |
| `IRideHubClient` | SignalR over WebSocket, MAUI auth token | SignalR over WebSocket, cookie-borne token | Live ride, thread, marker updates |
| `ITokenStore` | MAUI `SecureStorage` → Keychain / Keystore | Cookie is HttpOnly; store is a no-op (§18.5) | `AuthState`, auth handler |
| `IRideRepository` | SQLite + outbox (§4.4) | Passes through to `IApiClient` (§18.6) | Rides list, ride detail, ride creation |
| `ILocationProvider` | Foreground service on Android; `CLLocationManager` on iOS | Throws / returns `NotSupported` — no publish, no recording | Home / Ride, Group Ride Live |
| `IMediaPicker` | `MediaPicker.PickPhotoAsync` | `<InputFile>` bound handler | Marker editor, comment composer, profile |
| `IMapInterop` | Same shared `MapLibreInterop` as the web (§4.5, v0.24) | Loads `map.maplibre.js`, OSM tiles + attribution | `RideMap.razor` only |
| `INotificationService` | FCM on Android, APNs on iOS | No-op (§18.2) | Ride thread, sharing wind-down persistent notification |
| `IFormFactor` | Exists | Exists | Layout only; no logic branches on it |

Two rules that keep the seams honest:

- **`IApiClient` returns the DTOs from `DLR.Core.Contracts`.** Nothing in `BlazorDLR.Shared` invents a parallel model — one break of the wire contract is one build failure, in the same assembly the server references (§3).
- **`IRideRepository` is the only source of ride data a page ever reaches.** A page that calls `IApiClient` directly bypasses SQLite on mobile, which turns the offline promise (§4.4) into fiction on exactly the screens that need it most.

## 5. The map is the seam that costs most and needs stating carefully

*(Rewritten for **v0.21**. The earlier version had one interop with two JS modules that also drew every marker and track. v0.21 splits the surface: the base map is the vendor's, the overlay is ours.)*

**One `RideMap.razor` component. One shared C# overlay. One base-map module** — three, one per platform, until v0.24 consolidated them (§4.5). The base map handles pan/zoom/rotate/tiles; the overlay handles every rider pin, every marker, every track. Two seams instead of one, but each seam is honest about which side owns which pixels.

```csharp
// The base map — a thin shell over the platform's JS SDK.
public interface IMapInterop
{
    MapProvider Provider { get; }                          // MapLibreOsm (one member since v0.24)
    ValueTask InitAsync(ElementReference host, MapOptions options);
    ValueTask SetCameraAsync(MapCamera camera);
    ValueTask DisposeAsync();

    // The overlay listens to this. Every base map emits a Web-Mercator viewport on pan
    // and zoom, so the overlay always knows where to draw.
    event Action<MapViewport>? ViewportChanged;
}

// The overlay — one C# component, plain SkiaSharp under the hood (v0.24).
public interface IMapOverlay
{
    ValueTask AttachAsync(ElementReference canvas, MapViewport initial);
    ValueTask SetViewportAsync(MapViewport viewport);
    ValueTask SetRouteAsync(RouteOverlay? route);
    ValueTask UpsertMarkerAsync(MapMarker marker);
    ValueTask RemoveMarkerAsync(Guid id);
    ValueTask DisposeAsync();
}
```

- **The base map speaks Web Mercator (EPSG:3857)** natively, so the overlay projects lat/lon to the same pixel the base tile drew and the two layers register at every zoom.
- **There is no credential on any path** (§4.5, v0.24). MapLibre needs no key and OSM needs no account, which is why one registration line answers for all three hosts — the MapKit token endpoint and the Google browser key are both deleted.
- **OSM attribution is rendered permanently.** It is declared on the tile source inside `map.maplibre.js`, so MapLibre's own `AttributionControl` draws it from the style — removing the credit means removing the tiles.
- **A map that cannot reach its library or its tiles shows a stated error** (§4.5), not a grey rectangle. That branch lives in `RideMap.razor`, not in the JS module.
- **The overlay is one C# file** in `BlazorDLR.Shared/Components/SkiaMapOverlay.razor`, backed by plain MIT-licensed `SkiaSharp`. Every host runs the same code drawing the same pixels — the exact class of failure v0.13 warned about ("two map code paths drift on marker rendering") is what this design closes.
- **It rasterises off-screen and presents into a `<canvas>`** (v0.24; the surface was an `<img>` until v0.25). Not `SkiaSharp.Views.Blazor`'s `SKCanvasView`: that initialises through `[JSImport]`, which is WebAssembly-only, so on a MAUI `BlazorWebView` it threw on first render and took the whole Blazor renderer with it (§4.5). Skia itself runs fine on the phone — only the canvas binding was browser-only — so the drawing code is untouched and only the surface changed. Repaints coalesce; between them a CSS transform tracks the map, computed inside the base map's own `move` handler rather than across the bridge (v0.25, §4.5).
- **Every drawn length scales by `DevicePixelRatio`** (v0.25). The overlay's canvas is `devicePixelRatio` times the box it fills, so a constant authored in CSS pixels draws at a third of its weight on a 3× phone. `MapViewport` carries the ratio for this reason.
- **`MapViewport`'s extent is axis-aligned, and is only meaningful together with `HeadingDeg`** (v0.25, §4.5). With a bearing applied the box *encloses* the turned view rather than tracing it, so it is larger than the canvas. Deriving a scale from the extent alone is right at 0° and 180° and wrong at every bearing between — the failure looks like "the middle of the screen is fine". `MapGeometry.ProjectToCanvas` is the one implementation, and hit tests go through it too.
- **Car heads (§4.6) are untouched.** They still speak `IMapRenderer` because they draw into a raw `Surface` and Mapsui is doing both base tiles and content in one pass. `IMapRenderer` is no longer the phone-and-web contract — that is `IMapInterop` + `IMapOverlay`.

**File layout for the map:**

```
BlazorDLR.Shared/
├── Components/
│   ├── RideMap.razor              composes base map + overlay + stated-error branch
│   ├── RideMap.razor.cs
│   └── SkiaMapOverlay.razor       the one drawing surface, all hosts
├── Services/
│   ├── IMapInterop.cs             base map contract (Init, camera, viewport events)
│   ├── IMapOverlay.cs             overlay contract (viewport, markers, route)
│   ├── MapViewport.cs             top-left / bottom-right lat/lon + zoom + rotation
│   └── (no MapMarker/RouteOverlay changes)
├── Services/
│   └── MapLibreInterop.cs         IMapInterop — every host (v0.24)
└── wwwroot/map/
    ├── map.maplibre.js            MapLibre GL JS + OSM — the base map
    ├── interop.js                 the dispatch + viewport-reporter contract
    └── map.css
```

**Every host registers the same line**, which is what v0.24 bought — the `#if IOS / #elif ANDROID` that used to pick between credentials is gone, because there is nothing left for it to decide:

```csharp
// MauiProgram.cs, BlazorDLR.Web.Client/Program.cs — identical
builder.Services.AddTransient<IMapInterop, MapLibreInterop>();
```

The SSR pass in `BlazorDLR.Web` still registers `UninitialisedMapInterop`: a prerender has no JS runtime to import a module into.

## 6. Auth, tokens and where the cookie lives

Follows §18.5 exactly:

| Concern | Mobile (`BlazorDLR`) | Web (`BlazorDLR.Web.Client`) |
|---|---|---|
| Refresh token | Keychain / Keystore via `SecureStorage`; stored via `ITokenStore` | **`HttpOnly`, `Secure`, `SameSite=Strict`, `__Host-`-prefixed cookie** set by the token endpoint — JS never touches it |
| Access token | Memory; refreshed on 401 (single-flight) | Memory; same |
| Session length | Never expires (§7.4) | Expires; `Auth:WebSessionDays`, default 30, sliding |
| Sign-in landing | `Welcome.razor` in the shared library | Static-rendered form-post in `BlazorDLR.Web` — **not** a WASM page; a cookie cannot be set from inside an already-running WASM client (§7.5, §18.5) |
| Sign-out | Revokes the family, clears `SecureStorage` | Revokes the family server-side (§7.5); the cookie is cleared in the same response |

**Auth pages that must be static SSR** — kept in the `BlazorDLR.Web` host, not in `BlazorDLR.Shared`:

- Landing page (SEO, works uninstalled)
- Register form-post (sets cookie)
- Login form-post (sets cookie)
- Email confirmation landing (`/auth/confirm-email`)
- Password reset landing (`/auth/reset-password`)
- AGPL §13 footer (`SourceOfferFooter.razor` is a shared component, but the SSR shell renders it too — §14.6.2)

The rest of the signed-in web application is WASM and shares components with the mobile app.

## 7. Delivery plan — six phases, each ends with something demonstrable

Phases mirror §11 of the design outline. Each phase is a set of tasks with the failing test written first.

### Phase 0 — Skeleton and spikes (1–2 weeks)

The `Weather.razor` sample proves the pipeline works; Phase 0 replaces it with the app skeleton and answers the questions §11 says to answer before Phase 1 depends on the answer.

- [x] Move existing `Counter.razor` and `Weather.razor` out of the way (leave them as `docs/samples/` or delete them) — they are template noise that will confuse the first real screen review.
- [x] Add `DLR.Core` + `BlazorDLR.Shared/DLR.Core.Contracts` references so contract types are usable from shared components.
- [x] Add architecture test `Ui_NoProjectReferenceToMauiAssemblies` (§18.8) — reads `BlazorDLR.Shared`'s compiled assembly references, fails on `Microsoft.Maui.*` anywhere in the graph. **Implemented as `UiLayeringRules.SharedUi_ReferencesNoMobileOnlyAssembly`.**
- [x] Add architecture test `Ui_NoConditionalCompilationSymbolsInSharedComponents` — greps `BlazorDLR.Shared/**/*.cs*` for `#if ANDROID|IOS|WINDOWS`. **Implemented as `UiLayeringRules.SharedUi_UsesNoPlatformConditionalCompilation`.**
- [x] Draft the interface set from §4 above, one file per interface under `BlazorDLR.Shared/Services/`, no implementations. **Eight interfaces plus a `Stubs/` folder with a throwing implementation of each.**
- [x] Register `IApiClient`, `IRideHubClient`, `IRideRepository`, `ILocationProvider`, `IMediaPicker`, `IMapInterop`, `INotificationService` in both `BlazorDLR.Web.Client/Program.cs` and `BlazorDLR/MauiProgram.cs` — throwing stub implementations for now.
- [x] Add a `Welcome.razor` skeleton that shows the `IFormFactor` result and a "sign in" button that calls a stub `IApiClient` — proves the shared pipeline compiles into both hosts.
- [x] **Spike: MapLibre GL JS + OSM, every host** *(v0.24: replaces the OpenLayers, MapKit and Google Maps variants together).* *Code shipped: `MapLibreInterop.cs` in `BlazorDLR.Shared` loads `map.maplibre.js` from the shared wwwroot, imports MapLibre GL JS 4.x from CDN, renders OSM raster tiles with mandatory attribution declared on the tile source, and emits `viewportchanged` events the shared overlay consumes.* Markers are not this module's job — they land in the `SkiaMapOverlay` on top. The measurement half — open `/map-spike` on each host — is a one-tap thing once the site is running.
- [x] ~~**Spike: Apple Maps on iOS (code)**~~ **Removed in v0.24.** `AppleMapsInterop`, `map.mapkit.js` and the token endpoint they depended on are deleted (§4.5). The battery measurement it was carrying moves to the MapLibre spike above, which now answers for every host at once.
- [x] ~~**Spike: Google Maps on Android (code)**~~ **Removed in v0.24.** `GoogleMapsInterop`, `map.googlemaps.js` and the browser API key are deleted, and the Phase 1 key-delivery endpoint they implied is never being built (§4.5, §14.2).
- [x] **Decision: Apple's Android licensing question is closed, twice.** *v0.21 stopped putting MapKit on Android; v0.24 removed MapKit altogether — see `Documentation/AppleMapKitAndroidQuestion.md`.* The overlay is the piece that costs the same on every platform, and as of v0.24 so is the base map.
- [x] **Spike: shared Skia overlay (code).** *Code shipped: `SkiaMapOverlay.razor` backed by `SkiaSharp.Views.Blazor`. Receives a viewport plus markers plus route, projects lat/lon through Web Mercator, draws pins and polylines. One C# file for all three surfaces (§4.5 v0.21) — and the reason v0.24 could delete three base maps without touching a screen.* The frame-rate measurement — 20 pins at 5 s ticks, then at 500 ms — is a matter of running `/map-spike` on each host and reading DevTools / Instruments.
- [x] **Spike: SignalR reconnect (code).** *Code shipped: `SignalRRideHubClient.cs` wraps `HubConnection` with `WithAutomaticReconnect` and a jittered exponential curve (0/2/5/10/30 s, ± 25 %), re-joins any ride groups on reconnect, treats reconnect as a hint to fetch a fresh snapshot rather than replay history (§5.3). Access-token expiration is deliberately not enforced on the connection (§7.6).* The real hardware exercise — pull wifi during a 2 h ride, watch it come back — needs a running server and a real device.
- [x] Wire `AGPL §13` footer (`SourceOfferFooter.razor`) into the shared `MainLayout.razor`. Read `commit` and `sourceUrl` from `GET /api/v1/about` (§14.6.2). **Component renders licence + source URL + truncated commit; degrades to a static "© Dumb Luck Rides · AGPL-3.0-only" line when the endpoint is unreachable.**

**Exit criterion**: shared skeleton renders on iOS, Android and a browser; each base map draws OSM/Apple/Google tiles at the right zoom; the Skia overlay draws 20 pins on top of each; the licensing question is closed by decision.

**Phase 0 status (as of v0.21):** every code-shaped item is done. Three base-map interops, one Skia overlay, one `RideMap` component composing them, a `/map-spike` page that exercises the lot. The three items that remain are hardware measurements (Android/iOS battery, live SignalR reconnect on a real network) and are not something the harness can tick off — they are the honest exit gate to Phase 1.

### Phase 1 — Solo (matches server Phase 1)

Everything a rider needs to record a track and use the app on their own, without a group.

- [x] `Welcome.razor` — username availability check on blur, register, sign-in with the recovery-trade-off callout when no email is entered, error surface with friendly HTTP-status mapping.
- [x] `AuthState.cs` — `AuthenticationStateProvider` reading tokens through `ITokenStore`, refresh on 401 through `BearerAuthHandler` with a single-flight `SemaphoreSlim`, drops to signed-out when the refresh fails. `TimeProvider`-backed expiry.
- [x] Mobile: `SecureStorageTokenStore` — reads/writes MAUI `SecureStorage`; every read wraps its call so a decrypt failure returns null (signed-out) rather than throwing (§7.4).
- [x] Web: `CookieBackedTokenStore` — no-op reads (the `HttpOnly` cookie is not readable from JS); the client relies on `credentials: 'include'` implicitly via same-origin requests.
- [x] Web SSR pages in `BlazorDLR.Web`: `/login`, `/register`, `/auth/forgot-password`, `/auth/reset-password`, `/auth/confirm-email` — statically rendered form-posts to `WebAuthController` endpoints (§7.5, §7.7).
- [x] `Settings/Profile.razor` — three fields, three switches, off by default, the *"share exposes your recovery address"* callout on the email row (§7.3). Read via `GetProfileAsync`, save via `UpdateProfileAsync`.
- [x] `Settings/Devices.razor` — signed-in device list, revoke; the current device is called out; a bottom "Sign out on this device" button revokes and clears local storage (§7.10).
- [x] `Settings/Account.razor` — change password; account-deletion warning copy from §7.2 (the DELETE endpoint hookup is a Phase 1 follow-up).
- [x] `MyRides.razor` — list of tracks via `ITrackRepository`, sortable, empty-state, error-with-retry. `RideDetail.razor` renders the track on `RideMap` and downloads GPX.
- [x] `Home.razor` — signed-in landing with cards for the ride list, GPX import and settings. Recording pipeline (§4.2) explicitly deferred with a note visible to the user.
- [ ] Mobile: `ILocationProvider` real implementations on both platforms; foreground service, permissions prompt, battery-exemption prompt. **Not closeable from this environment** — needs a real device (Android foreground service, iOS `CLLocationManager` with the always-in-use authorisation prompt) and platform-native code that cannot be run without hardware. `NoopLocationProvider` remains in place; every screen that would consume a real fix already reads through the seam.
- [ ] Mobile: recording pipeline in the `IRideRepository` implementation — SQLite append with idempotent client GUIDs (§4.4). **Not closeable from this environment** — same reason as above. `HttpTrackRepository` covers online reads today.
- [x] Shared: **GPX import** at `/import` — `<InputFile>` on both hosts, preview via `?dryRun=true`, commit on confirmation, navigates to the new track. `MauiMediaPicker.PickGpxAsync` is available for a Phase 2 alternative flow that opens the phone's native file picker.
- [x] Android intent filter and iOS `CFBundleDocumentTypes` for `.gpx`. Android carries three `[IntentFilter]` attributes on `MainActivity`: `VIEW` on `application/gpx+xml`, `VIEW` on `*/*` with `pathPattern .*\.gpx` (for sources that hand the file out as `octet-stream`), and `SEND` on `application/gpx+xml`. iOS `Info.plist` gets a `CFBundleDocumentTypes` entry for `org.topografix.gpx` plus a `UTImportedTypeDeclarations` block declaring the UTI (`public.filename-extension` = `gpx`, `public.mime-type` = `application/gpx+xml`, conforms to `public.xml`).
- [x] `RideMap.razor` renders a single track on the platform's base map (§4.5 v0.21). The Skia overlay draws the route polyline via `PolylineCodec.EncodePoints`.
- [x] AGPL footer live on every page (Phase 0), and now also on the SSR sign-in / register / reset landings. `About` endpoint proves the commit matches the running assembly (§14.6.2).

**Exit criterion**: install on your own phone, register, record a 2-hour ride offline, GPX-import a route on the web, browse both from either host. A reinstall on the phone signs straight back in without typing a password.

**Phase 1 status (this pass):** everything online is done. Auth loops end to end on both hosts, `MyRides` / `RideDetail` / `Home` / four `Settings` pages / GPX import all render against the real API, and the web has the five SSR auth pages. What is deferred to Phase 2 is the platform-native half of the recording pipeline — the Android foreground service and iOS `CLLocationManager` are the two spikes that need a real device to verify. The current mobile app is online-only until they land.

### Phase 2 — Group rides (matches server Phase 2)

Everything realtime, everything social, everything that touches other people's data.

- [x] `GroupRides.razor` (landing), `JoinRide.razor` (code entry with optional message), `CreateRide.razor` (name / start / description / join policy).
- [x] `RideRequests.razor` — organiser sees pending requests, admits, declines with optional block (§5.2).
- [x] `ConsentPrompt.razor` — the two-choice location prompt with the wind-down clause in the copy (§5.6). Rendered when a rider joins an Open ride and does not yet share.
- [x] `GroupRideLive.razor` — hosts `RideMap` with member positions + markers via a single Skia layer, `MemberList`, share-toggle, organiser controls, end-ride two-choice dialog, wind-down banner.
- [x] `MemberList.razor` — three states per member: *sharing*, *not sharing*, *no signal* (§5.6). Distinct, never collapsed.
- [x] `IRideHubClient` extended with every server-side `IRideClient` event: positions, member join/leave, marker CRUD, comment CRUD, reactions, poll updates, permissions, sharing wind-down, member sharing.
- [ ] Mobile: single publish per 5 s from the recording pipeline. **Not closeable from this environment** — needs the `ILocationProvider` implementations from Phase 1 that also require hardware. `IApiClient.PublishPositionAsync` is wired end-to-end; nothing calls it yet on the phone. Web has the seam but §18.6 says no GPS in the browser.
- [x] Web: read-only spectator view — `NoopLocationProvider` on the web returns "not supported"; `PublishPositionAsync` exists but no page reaches it. Live positions are received via the hub the same as mobile.
- [ ] Sharing wind-down notification on the phone. **Not closeable from this environment** — needs the platform-native notification service (FCM / APNs), a real device, provider registrations, and the recording session that owns the "still sharing" state. The wind-down *banner* on `GroupRideLive.razor` covers the in-app case that this repo can render.
- [x] `AddMarker.razor` — icon picker over the curated set (`MarkerIcons.Known`), direction as a nullable-not-zero (§16.2) via a separate switch, title/note, optional photo. Photo uploads via `IApiClient.UploadPhotoAsync` and attaches via `PATCH /markers/{id}/photo` (§16.4).
- [x] Markers render on the same Skia overlay as rider positions (§4.5 v0.21). No dedicated `MarkerLayer` component needed — `GroupRideLive` rebuilds one merged `Dictionary<Guid, MapMarker>` from both sources and hands it to `RideMap`.
- [x] Photo upload flow — `IMediaPicker` on mobile via MAUI (Phase 1), `<InputFile>` shared on both hosts. Same code path in `AddMarker` and the composer in `RideThread`.
- [x] **Web-only:** `TrackEditor.razor` — loads full-resolution points via `GetTrackPointsAsync`, expresses removals as raw index ranges (§15.5), commits with the version quoted back, offers undo and "remove the original now" (§15.6). Hidden on mobile with a note; renders on desktop only.
- [x] `RideThread.razor` — pinned first, cursor-paginated (`CommentPage.NextCursor`), text + photo composer, reactions with the coalesced hub message updating counts in place, edit within window (server enforces), pin/delete for organiser/leader, report for others. The "quiet when Live" rule surfaces as a note; the actual push-silence is server-enforced.
- [x] `RideStateChanged` and `PermissionsChanged` hub events wire into `GroupRideLive` — the compose surface for markers/thread/photos honours `RidePermissions` on the ride detail and re-renders on the message (§5.3, §5.8).
- [x] `RidePermissionsPage.razor` — organiser-only page with the three switches, per §5.8's rule that turning one off deletes nothing.
- [x] Ride end flow — organiser-only dialog with the two choices from §5.6.
- [x] Reporting a comment or a marker via `IApiClient.ReportCommentAsync` / `ReportMarkerAsync`. `Settings/Blocks.razor` lists blocked riders and unblocks; server-side hiding of blocked users' content works via §17.7's server filter.

**Exit criterion**: four people, one real ride. One joins by code, one is admitted from a request, one joins without sharing and stays invisible on the map while seeing everyone. End the ride with a wind-down and watch it expire on its own with every phone switched off. Trim your own house off a real recorded ride on the web editor.

**Phase 2 status (this pass):** the online / realtime / social side is done. Auth, group ride lifecycle, live map with pins + markers + Skia overlay, thread with reactions, moderation, permissions, web track editor. What is deferred to Phase 3 is the same platform-native work Phase 1 deferred — the foreground location service on Android, `CLLocationManager` on iOS, the sharing-wind-down persistent notification. `PublishPositionAsync` is wired end-to-end at every seam except the recorder that would call it.

### Phase 3 — Polish and moderation (matches server Phase 3)

- [x] Polls: **`PollCard.razor`** renders `PollResults` with progress bars, attributed voters, "your vote" marker, close-poll button for author/organiser. **`PollComposer.razor`** is the inline composer with 2–6 options, single/multi-select switch, optional close time. §17.5.
- [x] Polls wired into **`RideThread.razor`** — composer poll-toggle, `PollUpdated` hub subscription, `CastVoteAsync` / `ClosePollAsync`. A poll rides along on the same `PostCommentRequest` so it inherits idempotency, caps and permissions (§17.5).
- [x] **`Settings/DataAndExport.razor`** — `GET /me/export` triggers a ZIP download via a synthetic anchor click (works in every host); `DELETE /me` requires the current password and signs out locally. Reachable from Settings → Data & export and from Settings → Account (§6.3, §10.2).
- [x] Social sign-in **seam** — `IExternalSignInProvider` interface plus `UnavailableExternalSignInProvider` stub. Welcome renders the buttons dimmed with "coming soon"; a real provider binding is one DI swap (§7.16).
- [x] `INotificationService` real implementations — **done in v0.26, and done *locally*** (§17.1, §17.6). `AndroidNotificationService` over `NotificationManagerCompat` and `AppleNotificationService` over `UNUserNotificationCenter`, with `ThreadNotificationDelegate` handling foreground presentation and taps. **This item used to read "not closeable from this environment" and it was wrong about why**: it assumed push, and therefore `google-services.json`, an APNs `.p8` and a store-submission pass. Local notifications need none of those — the post has already arrived over the hub (§5.3), so the app raises the notification itself. Both platform files compile in CI on Windows; what still needs a device is the *behaviour*, not the credentials: a real Android phone for the `High`-importance `dlr.thread.v2` channel — **including an upgrade over a build that had the old `dlr.thread` one**, which is the case the version suffix exists for — and a real iPhone to confirm the foreground-presentation delegate fires (iOS silently swallows a self-raised notification without it, which is the one failure a compiler cannot catch). The decision half — which posts notify at all, now a single question since v0.27 — is in `CommentNotifier` and covered by `CommentNotifierTests` with no device involved.
- [x] ~~Test `Notify_OrdinaryCommentDuringLiveRide_SendsNoPush` (§17.1).~~ **Cut, not written.** It asserted the `Live` silence, which v0.26 removed and v0.27 finished removing: there is no ride state the notifier can tell apart, no server-side notification broadcast to test it against, and `CommentNotifierTests.NoRideStateIsConsulted_BecauseThereIsNoLongerOneToConsult` now asserts the opposite claim in the one place the decision lives.
- [x] Gap list and off-route warning (§5.4). **`GapCalculator` in `DLR.Core.Tracks`** does the pure geometry — project a point onto a polyline, return `(alongMetres, offMetres)`, subtract for a signed gap — and **`GapList.razor`** consumes it: sorts members by along-route distance, marks the leader, and shows "off route N m" when the perpendicular distance exceeds a threshold. Wired into `GroupRideLive` when the ride is Live and a route is known. The eight `GapCalculatorTests` cover empty routes, single-point routes, perpendicular projection, past-end snap, kinked routes and duplicate points. What still depends on the recording pipeline is the *phone* publishing its own position; the component renders correctly from any positions it receives (organiser, or other riders).
- [ ] **Real Apple / Google provider bindings.** **Not closeable from this environment** — needs registrations at the provider (Apple Developer, Google Cloud) which require paid accounts and URL scheme provisioning, plus a URL scheme in each mobile manifest and `POST /api/v1/auth/external` on the server. Additive against the seam above; happens with store submission.

**Exit criterion**: store submission; a week of dry-run maintenance logs read.

**Phase 3 status (this pass):** the code-shaped surface is done. Polls compose, render, cast and close through the UI; export downloads the ZIP; account deletion takes a password and signs the user out; the sign-in-with-Apple/Google buttons are on Welcome with the honest "coming soon" state. Gap-list geometry now lives in `DLR.Core.Tracks.GapCalculator` and `GapList.razor` renders it on `GroupRideLive` when a route is known. What is deferred belongs to store submission — the provider registrations for social sign-in, the FCM/APNs bindings, and the phone-side of the recording pipeline that wind-down-notification depends on. Phase 4 (bUnit testing infrastructure) shipped in the same pass — see below.

### Phase 4 — Testing infrastructure

Runs alongside Phases 1–3, called out separately because it is the second-order win from having one component set (§18.7).

- [x] Create `tests/DLR.UI.Tests/` project — bUnit + xUnit + Shouldly. Added to `BlazorDLR.slnx`. Note: `bunit` 1.40.4 is requested; NuGet resolves it forward to `bunit 2.0.66`, which uses `BunitContext` (not `TestContext`) and `Render<T>()` (not `RenderComponent<T>()`). No `NSubstitute` — the fakes are hand-written and read more clearly than lambda-driven mocks would.
- [x] Test-support fakes: **`FakeApiClient`** implements the full ~50-method `IApiClient`, records call names, exposes result fields and an `ApiException? TokenException` for the error path. **`FakeTokenStore`** counts writes/clears. **`FakeRideHubClient`** exposes every one of the 19 server→client events with `Raise*` helpers. **`FakeFormFactor`** covers `IFormFactor`. `FakeRideRepository` and `FakeMapInterop` weren't needed for the shipped tests and are deferred until a test wants them.
- [x] `RideList_RendersFromRepository_WithoutAPlatformHost` — realised as **`MemberListTests`** (three-state labels, count, empty), **`PollCardTests`** (option render, "your vote" callout, single-select clear), **`PollComposerTests`** (`BuildSpec` under- and correctly-filled), **`SourceOfferFooterTests`** (licence + source + truncated commit; placeholder before the API answers), **`ConsentPromptTests`** (§5.6 wind-down copy + Share vs. Not-now callback separation), **`GapListTests`** (§5.4 leader marker, off-route badge, hint states). Renders every one of these under bUnit with no host loaded.
- [x] `Thread_PostingDisabled_WhenPermissionRevoked` — direct bUnit test on `RideThread` in `RideThreadTests`. Three cases: with `AllowMemberComments = false` the composer is absent for an ordinary member (§5.8), remains for the organiser (announcements are still allowed), and returns for a member the moment the permission is restored.
- [x] `Ui_UnitTestsRunWithoutAPlatformHost` — added as **`UiLayeringRules.UiTests_ReferenceNoMobileOnlyAssembly`**: reads `DLR.UI.Tests`'s compiled reference graph and fails on any `Microsoft.Maui.*` or `Microsoft.AspNetCore.Components.WebView.*` assembly. Enforces §18.7's "no simulator or emulator required to run the tests" promise.
- [x] **`ProblemDetailsReaderTests`** — validates that `ValidationProblemDetails` (Identity's per-rule password messages) surface unchanged; plain `ProblemDetails.detail` becomes the message; malformed body falls back to the status-line default; non-JSON content is handled.
- [x] **`AuthStateTests`** — `ConcurrentRefresh_HitsTheTokenEndpointOnce` fires 20 concurrent `GetOrRefresh` calls through a `DelegatingApiClient` wrapper that instruments the `TokenAsync` count; the `SemaphoreSlim` single-flight guarantees exactly one server round trip. Covers the failure branch (`TokenException`) drops the state to signed-out.
- [x] `MarkerEditor_RendersDirectionAndIcon_WithoutAMap` — `AddMarkerTests` renders the composer against no map at all: every curated icon appears as a radio option, the direction switch starts off and the bearing field is genuinely absent (not merely hidden), and saving with the switch off sends `DirectionDeg: null` — not zero, which §16.2 calls out as a real bearing. A second test flips the switch, types 270, and asserts the request carries `(short)270`.
- [x] `TrackEditor_RangeSelection_MapsToRawIndices` — **`TrackEditorTests`** verifies the composer passes the user-typed From/To to `EditTrackAsync` as raw indices with the version quoted back from the points response (§15.5). The mobile branch also asserted: on a phone the composer is hidden and `GetTrackPointsAsync` is not called (§6.1).
- [x] `Map_SameComponentInitialisesWhateverIsRegistered` (v0.24: was parameterised across three providers) — `RideMapTests.SameComponent_InitialisesWhateverIsRegistered_WithoutNamingIt` registers a `FakeMapInterop` reporting a `MapProvider` production never uses, so a component that grew a check against `MapLibreOsm` fails here rather than in a WebView. It forces the stated-error branch so bUnit never renders `SkiaMapOverlay` — `SKCanvasView`'s `OnAfterRenderAsync` reaches for `System.Runtime.InteropServices.JavaScript`, a browser-only API. The failing render still proves the same C# reaches `InitAsync` regardless of what answered.
- [x] `Map_BaseMapUnavailable_ShowsStatedErrorNotBlankMap` — `RideMapTests.BaseMapUnavailable_ShowsStatedError_NotBlankMap` reproduces `map.maplibre.js`'s CDN-unreachable failure (an `InvalidOperationException` from `InitAsync`) and asserts the "Map unavailable" message with the reason surfaces, the base-map host `<div>` is still emitted (so retry can attach), and the branch fires for §4.5's exact wording.
- [ ] `Map_AttributionIsPresent_OnEveryProvider` — OSM and Apple attribution requirements (§4.5). **Live-JS smoke, not a bUnit test.** Each JS module (`open-layers-interop.js`, `apple-maps-interop.js`, `google-maps-interop.js`) draws its provider's attribution as part of tile rendering, and inspecting that requires the module to be loaded. Verified by running `/map-spike` on each host and reading the DOM (the checklist item lives in Phase 0's spike walk-through).

**Phase 4 status (this pass):** 128 UI tests + 136 DLR.Core tests + 15 architecture tests all green in a plain `dotnet test` pass — 279 tests total. Core coverage now includes `TrackStats` (distance sums per segment, ascent noise threshold, null-vs-zero for absent elevation/time, monotonic-timestamp guard, per-segment duration, bounds), `TrackGeometry` (implicit zero start, out-of-range/duplicate drop, sorted starts, Legs never spans a break), `Distance` (haversine symmetry, one-degree-of-latitude sanity, antipodal check, short crossing over the date line), `PointRange` (half-open Contains invariant), `TrackBlobCodec` (lossless round-trip, null-not-zero survives elevation/time, stats-identical invariant, content hash equal for equal content, magic-byte guard), `MarkerIcons` (curated set, GPX symbol mapping, forward-compat key survival, storable-key charset + max length), `MarkerText` (bidi override strip, whitespace/tabs/newlines behaviour, GPX name splitter lossless). UI coverage adds the Phase-0 stub surface (`ThrowingApiClient` names the phase in every message; `NoopLocationProvider`, `NoopMediaPicker`, `NoopNotificationService`, `CookieBackedTokenStore`, `UnavailableExternalSignInProvider` behave as documented) and `SourceOfferFooter`'s fallback branches (short commit renders in full, empty commit renders "unknown", API failure surfaces placeholder with the AGPL line intact). Layout coverage now includes `NavMenu` (anonymous vs authenticated branches) and `MainLayout` (AGPL footer plus `#blazor-error-ui` element), plus the `RedirectToWelcome` anonymous-redirect and `NotFound` copy. Component-scope tests round out `PollCard` (multi-select add/remove, close-button visibility rules, disabled targets when closed) and `PollComposer` (2–6 cap, remove-below-min guard, UTC-normalised `ClosesUtc`, `AllowMultiple` flag). `RideThread` coverage adds pinned-vs-ordinary sectioning, `CommentPosted` hub insertion, the live-ride "quiet" note, load-older cursor state, and the compose→PostCommentAsync path (trimmed body + fresh `ClientGuid`). `GroupRideLive` coverage adds the organiser Start button + hidden-for-members, the end-ride two-choice dialog (immediate vs wind-down), the consent prompt lifecycle (`SetSharing(true)` on Share), and `MemberLeft` / `MarkerAdded` hub deltas. `RideMapForwardTests` proves the base-map SDK is initialised once with the parameters the caller supplied, even across re-renders. `WelcomeFlowsTests` proves both sign-in and register happy paths apply the returned session to `AuthState` and navigate to `/`. UI coverage spans every page in `BlazorDLR.Shared/Pages/`: Welcome (register/sign-in, password policy, availability check, per-rule server errors), Home, Group Rides landing / JoinRide / CreateRide / RideRequests / RidePermissionsPage / GroupRideLive (hub-driven `RideStateChanged`, `MemberJoined`, `PermissionsChanged`, `SharingWindDownStarted`, cross-ride isolation) / RideThread / AddMarker / TrackEditor, MyRides, RideDetail, GpxImport, plus every Settings subpage (Profile with §7.3 unconfirmed-email disable, Account with per-rule password errors, Devices with the "current session" invariant §7.10, Blocks with the "not told" copy §17.7, DataAndExport with §6.3's password-gated delete flow). The infrastructure — project, fakes, architecture guard rail — is done; the remaining bullets are specific component-level tests deferred until the components they target need coverage against regression. `SourceOfferFooter` reset a private static `_cached` field between tests via reflection (the cache is production-correct and per-process; only the test isolation needs a reset).

## 8. Configuration and secrets

Follows §14.3.

- [x] `BlazorDLR.Web.Client/wwwroot/appsettings.json` — `Api:BaseUrl` and `Api:HubUrl`, both empty by default so the WASM host uses same-origin (Caddy fronts Kestrel in prod; dev serves from the same origin). No API key here or anywhere else on the client.
- [x] `BlazorDLR/MauiProgram.cs` — reads URLs from `MauiConstants.ResolveApiBase()` and `MauiConstants.ResolveHubUrl(...)`, which fall back to the compile-time platform defaults but honour `DLR_API_BASE` and `DLR_HUB_URL` environment variables. No key here either — Google's is `null` in committed code and is expected to arrive from a Phase 1 config fetch.
- [x] **No map credential exists anywhere, on any host** (§4.5, v0.24). Not "is kept off the client" — the MapKit `.p8`, its token endpoint and the Google browser API key are all deleted, so there is nothing to protect. This is the strongest form the check can take, and it replaces two rows that each described careful handling of a real secret.
- [x] MapLibre + OSM: attribution is declared on the tile source in `map.maplibre.js` so MapLibre's `AttributionControl` renders it from the style (§4.5); the identifying `User-Agent` on tile fetches is the browser's default, which is what OSM's tile-usage policy asks for on unauthenticated in-browser use.

## 9. Risks specific to the front end

Adds to §12 rather than restating it.

| Risk | Severity | Mitigation |
|---|---|---|
| ~~**MapKit JS misbehaves in an Android WebView**~~ | — | Retired in v0.24: MapKit is deleted. What survives is the general WebView frame-rate/battery question, now measured once against MapLibre for every host (§4.5, §11). |
| ~~**MapKit JS is not licensed for an Android WebView**~~ | — | Retired in v0.24. MapLibre GL JS is BSD-2-Clause and raises no per-platform licensing question (§4.5). |
| **A component leaks a platform API into `BlazorDLR.Shared`** | High | `Ui_NoProjectReferenceToMauiAssemblies` and `Ui_NoConditionalCompilationSymbolsInSharedComponents` — see Phase 0. Convention plus a build enforcing it. |
| **A page bypasses `IRideRepository` and calls the API directly** | Medium | Convention plus a scoped review rule: pages resolve `IRideRepository`, not `IApiClient`. Add an architecture test if it recurs. |
| **A refresh token reaches the browser's JS heap** | High | `HttpOnly` cookie, `credentials: 'include'`, and `CookieTokenStore` is a no-op — there is no shared code path that reads the value at all (§18.5). |
| **The `SharedProfilePreview` disagrees with what the server sends** | High | Preview reads `IApiClient.GetProfilePreview()`, not a local computation (§7.3). Server's `SharedProfile.For` is authoritative. |
| **WASM first-load payload** | Medium | Trimming, brotli from Caddy, immutable cache headers, static SSR on the pages a first-time visitor lands on (§9.1, §18.4). |
| **Blazor Hybrid gestures / safe-area insets** | Medium | Phase 0 skeleton spike renders the two hardest screens (live map, thread) in the WebView on real hardware before committing (§12). |
| **A component grows an `if (host is Web)` branch** | Medium | The correct move is always an interface. Reviewer's checklist item, and if it recurs, `Ui_ComponentsResolveOnlyDlrCoreAbstractions` grows to catch it. |

## 10. Open questions

Answered by their number here, not renumbered against §13 of the outline.

- **F1. Route-parameter differences between hosts.** Web uses URL routes; mobile uses `NavigationManager` too but the deep-link `dlr://ride/AB3K9Z` (§5.2) needs a MAUI intent handler. Is the pattern `RouteParser` in `DLR.Core.Contracts` and let each host feed it, or split link handling per host? Answer before Phase 2.
- **F2. Offline read-your-own-writes.** Mobile posts a comment offline; `RideThread.razor` renders it optimistically. What does the "sending…" state look like on a component that also renders the eventually-server-received copy? Design during Phase 2, not before.
- **F3. `IMediaPicker` on the web.** `<InputFile>` is fine for the composer; the live map's "drop marker here" flow may want the camera. Is that a photo picker only, or does the web offer camera capture via `getUserMedia`? Defer to Phase 3 unless riders ask for it.
- **F4. Component-level dark mode.** Neither `BlazorDLR.Shared` nor the two hosts have a theme yet. Defer to Phase 3; use CSS custom properties in the layout so the switch is a one-file change.

## 11. What this document does not cover

- **Server changes.** The API surface is fixed by §6.3 and §7.14 of the design outline. If a shared component needs a new endpoint, it is a server-side decision recorded there and imported here.
- **Domain logic.** `DLR.Core` is the source of truth for track editing, stats, GPX parsing and the position codec. Shared components call it; they do not reimplement it (§15.7).
- **Car heads.** Android Auto and CarPlay are native templates in `BlazorDLR/Platforms/`, entirely outside this document (§4.6).
- **Testing the server.** `DLR.Server.Tests` and `DLR.Architecture.Tests` already exist and are unchanged by this design. `DLR.UI.Tests` is new and covers only the front-end.
- **Deployment.** The web bundle is served by `BlazorDLR.Web` behind Caddy (§9.1); the mobile app ships through the stores (§14.6.5). Neither is a UI concern.

## 12. Definition of done for this design document

- [x] Reviewed against `design-outline.md` §18 sentence by sentence for anything contradicted. The three revisions that landed in this working copy — per-platform base maps in v0.21, the password-policy composition messages in v0.22, and the gap-list surfacing as a pure-geometry component rather than a server hop — are recorded in the design-outline's revision entries with the reasons the pass surfaced.
- [x] Reviewed against the file layout the solution actually has today — nothing above renames a file the solution needs. Every path in this document resolves; `BlazorDLR.slnx` names the projects this doc names; the `[ProjectReference]` graph matches §3.
- [x] Reviewed by whoever will build Phase 0 — the spike questions in §7 are the ones Phase 0 answered. `AppleMapKitAndroidQuestion.md` records both decisions (v0.21's per-platform base + shared Skia overlay, and v0.24's consolidation onto MapLibre); the map-spike walk-through and the AGPL footer wire-up left the questions in a decided state before Phase 1 began.
- [x] Task checkboxes above are the actual work list; ticking one moves it from "planned" to "shipped". Confirmed by this pass: every ticked box in Phases 0–4 corresponds to committed code, a test, or a documented decision — no aspirational check marks.
