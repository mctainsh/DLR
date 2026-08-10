// MapLibre GL JS + OpenStreetMap raster tiles — the base map on every surface (§4.5 v0.24).
//
// v0.24 consolidated three base maps into this one. There is no per-host module any more:
// iOS, Android and the web all import this file, because none of them needs anything the
// others do not. What that bought is recorded in §4.5 — no `.p8`, no server dependency for
// the map, no client-side API key, and a route to offline packs that neither vendor SDK
// could provide.
//
// Base-map role only: tiles, camera, rotation, attribution. Every rider pin, marker and
// route lives in SkiaMapOverlay on top (§4.5 v0.21).
//
// OSM's tile usage policy is a real constraint, not a formality (§4.5, §13 Q26). This module
// points at `tile.openstreetmap.org` directly, so no third-party style server sits between
// us and OSM, and `TILES` below is the single line that moves when the tile source does.

import { dispatch, createViewportReporter } from "./interop.js";

// Version pinned so a build is reproducible. `dist/maplibre-gl.js` is the UMD bundle:
// one request, one copy of the library, everything hanging off the `maplibregl` global.
//
// The same trap the OpenLayers module documented applies here and is worth keeping written
// down, because the next person to "modernise" this line will hit it: the per-module ESM
// sources under the package root import bare specifiers a browser cannot resolve without an
// import map, and per-entry CDN ESM bundles (`/+esm`) inline separate copies of the library
// so instanceof checks across entries fail.
const MAPLIBRE_BASE = "https://cdn.jsdelivr.net/npm/maplibre-gl@4.7.1";

// The tile source. §13 Q26 moves this to self-hosted PMTiles before public announcement —
// at which point this becomes a `pmtiles://` URL and a vector style, and nothing else in
// this file changes shape. Until then OSM's donated tiles carry development.
const TILES = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";

// ODbL requires this and §4.5 makes it permanent. Declared on the source, so MapLibre's
// AttributionControl renders it from the style — removing the attribution means removing
// the tiles, which is the point.
const ATTRIBUTION =
    '© <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noreferrer">OpenStreetMap</a> contributors';

let libraryLoad = null;

// Resolves to the `maplibregl` global, loading the bundle on first use. Rejects — and clears
// the cached promise so a later map can retry — if the CDN is unreachable, which is what puts
// a stated error in front of the user instead of a blank grey rectangle (§4.5).
function loadMapLibre() {
    if (typeof window !== "undefined" && window.maplibregl) return Promise.resolve(window.maplibregl);
    if (libraryLoad) return libraryLoad;

    libraryLoad = new Promise((resolve, reject) => {
        const existing = document.getElementById("maplibre-js");
        const script = existing ?? document.createElement("script");
        script.addEventListener("load", () => {
            if (window.maplibregl) {
                resolve(window.maplibregl);
            } else {
                reject(new Error("MapLibre GL JS loaded but did not define the `maplibregl` global."));
            }
        });
        script.addEventListener("error", () =>
            reject(new Error(`Could not load MapLibre GL JS from ${MAPLIBRE_BASE}/dist/maplibre-gl.js.`)));
        if (!existing) {
            script.id = "maplibre-js";
            script.src = `${MAPLIBRE_BASE}/dist/maplibre-gl.js`;
            script.async = true;
            document.head.appendChild(script);
        }
    });

    libraryLoad.catch(() => { libraryLoad = null; });
    return libraryLoad;
}

// MapLibre ships CSS separately, and without it the canvas is unpositioned and the
// attribution is unreadable. Inject once per page.
function ensureStylesheet() {
    if (typeof document === "undefined") return;
    if (document.getElementById("maplibre-css")) return;
    const link = document.createElement("link");
    link.id = "maplibre-css";
    link.rel = "stylesheet";
    link.href = `${MAPLIBRE_BASE}/dist/maplibre-gl.css`;
    document.head.appendChild(link);
}

// A raster style over OSM. Written inline rather than fetched from a style server: one less
// host to depend on, and the tile URL stays visible in this file where §13 Q26 will change it.
function osmRasterStyle() {
    return {
        version: 8,
        sources: {
            osm: {
                type: "raster",
                tiles: [TILES],
                tileSize: 256,
                // OSM's raster tiles stop at 19. Without this MapLibre requests z20+ and gets
                // 404s, which reads on screen as the map dissolving when a rider zooms in on
                // a marker — exactly the zoom level a marker is placed at (§16.1).
                maxzoom: 19,
                attribution: ATTRIBUTION,
            },
        },
        layers: [{ id: "osm", type: "raster", source: "osm" }],
    };
}

export async function createMap(hostElement, options, callbacks) {
    if (!hostElement) {
        throw new Error("map.maplibre.js: hostElement is required.");
    }

    ensureStylesheet();

    const maplibregl = await loadMapLibre();

    hostElement.classList.remove("dlr-map-placeholder");
    hostElement.replaceChildren();

    const map = new maplibregl.Map({
        container: hostElement,
        style: osmRasterStyle(),
        center: [options.longitude ?? 0, options.latitude ?? 0],
        zoom: options.zoomLevel ?? 2,
        bearing: options.headingDeg ?? 0,
        // The overlay draws every pin, so the base map never needs to be interrogated for
        // what is under the cursor. Turning this off skips a hit-test on every mouse move.
        interactive: true,
        // Attribution is not optional (§4.5). Left at MapLibre's default control rather than
        // rendered by us, so it survives anyone editing the shared component.
        attributionControl: { compact: false },
    });

    // The platform blue dot, on the hosts that asked for it. GeolocateControl is the base
    // map's own — distinct from the rider pins the Skia overlay draws from published fixes,
    // which is what everyone else on the ride sees (§5.3).
    let geolocate = null;
    if (options.showUserLocation) {
        geolocate = new maplibregl.GeolocateControl({
            positionOptions: { enableHighAccuracy: true },
            trackUserLocation: true,
            showAccuracyCircle: true,
        });
        map.addControl(geolocate);
    }

    // Every base-map module emits this exact shape (§4.5 v0.21).
    const readViewport = () => {
        const bounds = map.getBounds();
        if (!bounds) return null;
        const canvas = map.getCanvas();
        if (!canvas) return null;
        // Axis-aligned, as OpenLayers' calculateExtent was before it: with a bearing applied
        // this encloses the rotated view rather than tracing it. The overlay is handed
        // headingDeg alongside and rotates its own drawing, so the two agree.
        const northWest = bounds.getNorthWest();
        const southEast = bounds.getSouthEast();
        const dpr = window.devicePixelRatio || 1;
        return {
            topLeftLatitude: northWest.lat,
            topLeftLongitude: northWest.lng,
            bottomRightLatitude: southEast.lat,
            bottomRightLongitude: southEast.lng,
            zoomLevel: map.getZoom() ?? 0,
            headingDeg: map.getBearing() ?? 0,
            // clientWidth, not canvas.width: MapLibre already scales its backing store by the
            // device pixel ratio, so reading canvas.width and multiplying again would double
            // the overlay's canvas on every retina screen.
            canvasWidthPx: Math.round(canvas.clientWidth * dpr),
            canvasHeightPx: Math.round(canvas.clientHeight * dpr),
            devicePixelRatio: dpr,
        };
    };

    const reporter = createViewportReporter(readViewport, () => callbacks?.onViewportChanged);

    // MapLibre has no per-frame paint hook the way OpenLayers' `postrender` does, so this is
    // the frame-pump case interop.js describes: sample every frame between move-start and
    // move-end, and let the reporter drop the frames where nothing actually moved.
    //
    // `movestart`/`moveend` cover inertial flings and `flyTo`/`easeTo` animations as well as
    // drags, which is what keeps the overlay in register during a kinetic pan rather than
    // snapping into place when it settles.
    map.on("movestart", reporter.startTracking);
    map.on("moveend", reporter.stopTracking);

    // Rotation and pitch raise their own pair; a two-finger twist is not a "move".
    map.on("rotatestart", reporter.startTracking);
    map.on("rotateend", reporter.stopTracking);
    map.on("zoomstart", reporter.startTracking);
    map.on("zoomend", reporter.stopTracking);

    // A tap on the map, in lat / lon (§16.1). MapLibre raises `click` only for a real click —
    // a drag that ends over the map does not — so a pan never places a marker by accident.
    map.on("click", (event) => {
        if (!event.lngLat) return;
        dispatch(callbacks?.onMapClicked, "OnMapClicked", {
            latitude: event.lngLat.lat,
            longitude: event.lngLat.lng,
        });
    });

    // The first viewport cannot be read until the style has loaded and the canvas has a size;
    // reporting before that hands the overlay a zero-width frame it would draw nothing into.
    if (map.loaded()) {
        setTimeout(reporter.report, 0);
    } else {
        map.once("load", () => reporter.report());
    }

    // A container that changes size — rotating the phone, the ride panel expanding — moves the
    // viewport without moving the camera, so the overlay has to be told.
    const resize = new ResizeObserver(() => {
        map.resize();
        reporter.report();
    });
    resize.observe(hostElement);

    return {
        provider: "maplibre",
        setCamera(camera) {
            // jumpTo, not easeTo: SetCameraAsync is how C# asserts where the map should be —
            // following a rider (§5.3), opening the composer on the ride's ground (§16.1) —
            // and an animation would report a stream of intermediate viewports the overlay
            // would draw the pins against, which reads as the markers sliding into place.
            map.jumpTo({
                center: [camera.longitude, camera.latitude],
                zoom: camera.zoomLevel,
                bearing: camera.headingDeg ?? 0,
            });
            reporter.report();
        },
        dispose() {
            // Drop the callbacks first. Tearing the map down emits one last moveend, and by
            // then C# has disposed the DotNetObjectReference — dispatching into it logs
            // "no tracked object with id N" for a result nobody is waiting for.
            callbacks = null;
            reporter.dispose();
            try { resize.disconnect(); } catch { /* already gone */ }
            if (geolocate) {
                try { map.removeControl(geolocate); } catch { /* already detached */ }
                geolocate = null;
            }
            try { map.remove(); } catch { /* already gone */ }
        },
    };
}
