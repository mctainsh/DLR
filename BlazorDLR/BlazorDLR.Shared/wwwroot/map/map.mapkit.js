// Apple Maps via MapKit JS — iOS base map (§4.5 v0.21, §18.3).
//
// v0.21 narrowed this module: it now handles ONLY the base map — tiles, camera, rotation,
// attribution. Every rider pin, marker and route lives in SkiaMapOverlay (§4.5 v0.21).
// The change matters because it is what stops "two map code paths drift on marker
// rendering" (v0.13's finding), by removing the duplicated half.
//
// The C# side (`AppleMapsInterop.cs`) mints the JWT server-side (GET /api/v1/maps/token,
// §4.5) and passes it in as `options.token`; the .p8 private key never touches this file.

import { dispatch, createViewportReporter } from "./interop.js";

const MAPKIT_SCRIPT_ID = "mapkit-js";
const MAPKIT_URL = "https://cdn.apple-mapkit.com/mk/5.x.x/mapkit.js";

// Ensure MapKit JS is loaded exactly once per page.
async function ensureMapKit() {
    if (typeof mapkit !== "undefined") return mapkit;
    if (typeof document === "undefined") {
        throw new Error("map.mapkit.js: no document — running outside a browser?");
    }
    if (!document.getElementById(MAPKIT_SCRIPT_ID)) {
        await new Promise((resolve, reject) => {
            const script = document.createElement("script");
            script.id = MAPKIT_SCRIPT_ID;
            script.async = true;
            script.crossOrigin = "";
            script.src = MAPKIT_URL;
            script.onload = () => resolve();
            script.onerror = () => reject(new Error("MapKit JS failed to load."));
            document.head.appendChild(script);
        });
    }
    while (typeof mapkit === "undefined") {
        await new Promise((r) => setTimeout(r, 30));
    }
    return mapkit;
}

function initialise(mapkit, tokenSource) {
    return new Promise((resolve) => {
        mapkit.init({
            authorizationCallback: (done) => {
                Promise.resolve(tokenSource()).then((token) => done(token));
            },
            language: navigator.language ?? "en",
        });
        resolve();
    });
}

export async function createMap(hostElement, options, callbacks) {
    if (!hostElement) {
        throw new Error("map.mapkit.js: hostElement is required.");
    }

    hostElement.classList.remove("dlr-map-placeholder");
    hostElement.replaceChildren();

    const mapkitLib = await ensureMapKit();
    await initialise(mapkitLib, () => options.token);

    const map = new mapkitLib.Map(hostElement, {
        showsUserLocation: options.showUserLocation ?? false,
        showsMapTypeControl: false,
        showsZoomControl: false,
        showsCompass: mapkitLib.FeatureVisibility.Adaptive,
        region: new mapkitLib.CoordinateRegion(
            new mapkitLib.Coordinate(options.latitude ?? 0, options.longitude ?? 0),
            new mapkitLib.CoordinateSpan(spanForZoom(options.zoomLevel ?? 12))),
    });

    // Viewport event — every base-map module emits this exact shape so the shared
    // SkiaMapOverlay is portable across all three (§4.5 v0.21).
    const readViewport = () => {
        const region = map.region;
        if (!region) return null;
        const rect = hostElement.getBoundingClientRect();
        const dpr = window.devicePixelRatio || 1;
        return {
            topLeftLatitude: region.center.latitude + region.span.latitudeDelta / 2,
            topLeftLongitude: region.center.longitude - region.span.longitudeDelta / 2,
            bottomRightLatitude: region.center.latitude - region.span.latitudeDelta / 2,
            bottomRightLongitude: region.center.longitude + region.span.longitudeDelta / 2,
            zoomLevel: zoomForSpan(region.span.latitudeDelta),
            headingDeg: map.rotation ?? 0,
            canvasWidthPx: Math.round(rect.width * dpr),
            canvasHeightPx: Math.round(rect.height * dpr),
            devicePixelRatio: dpr,
        };
    };

    const reporter = createViewportReporter(readViewport, () => callbacks?.onViewportChanged);
    const emitViewport = reporter.report;

    // MapKit reports the region only when it settles, so the overlay would sit still for
    // the whole of a drag and then jump. `map.region` is live throughout, though — the
    // pump samples it once a frame between start and end, which is what makes the pins
    // travel with the tiles instead of after them.
    map.addEventListener("region-change-start", reporter.startTracking);
    map.addEventListener("region-change-end", reporter.stopTracking);
    map.addEventListener("rotate-start", reporter.startTracking);
    map.addEventListener("rotate-end", reporter.stopTracking);

    // A tap on the map, in lat / lon (§16.1). MapKit reports the tap in page coordinates;
    // `convertPointOnPageToCoordinate` is the documented way back to a coordinate, and it
    // accounts for the map's own rotation, which a manual pixel projection would not.
    map.addEventListener("single-tap", (event) => {
        const point = event?.pointOnPage;
        if (!point) return;
        const coordinate = map.convertPointOnPageToCoordinate(point);
        if (!coordinate) return;
        dispatch(callbacks?.onMapClicked, "OnMapClicked", {
            latitude: coordinate.latitude,
            longitude: coordinate.longitude,
        });
    });

    // Kick one out immediately so the overlay has a starting frame.
    setTimeout(emitViewport, 0);

    return {
        provider: "mapkit",
        setCamera(camera) {
            map.setRegionAnimated(new mapkitLib.CoordinateRegion(
                new mapkitLib.Coordinate(camera.latitude, camera.longitude),
                new mapkitLib.CoordinateSpan(spanForZoom(camera.zoomLevel))),
                false);
            map.rotation = camera.headingDeg ?? 0;
            emitViewport();
        },
        dispose() {
            // Drop the callbacks first — destroy can emit one last region-change-end, and by
            // then C# has disposed the DotNetObjectReference it would dispatch into.
            callbacks = null;
            reporter.dispose();
            try { map.destroy(); } catch { /* already gone */ }
        },
    };
}

// Rough zoom ↔ span conversions. Close enough for the shared overlay — MapKit
// does its own tile selection, so this only matters for camera calls.
function spanForZoom(zoom) {
    const clamped = Math.max(1, Math.min(20, zoom ?? 12));
    return 180 / Math.pow(2, clamped);
}
function zoomForSpan(span) {
    if (span <= 0) return 12;
    return Math.log2(180 / span);
}
