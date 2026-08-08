// Google Maps via the JavaScript API — Android base map (§4.5 v0.21, §18.3).
//
// New in v0.21. This module handles ONLY the base map — tiles, camera, rotation. Every
// rider pin, marker and route lives in SkiaMapOverlay on top (§4.5 v0.21).
//
// The C# side (`GoogleMapsInterop.cs`) supplies the referrer-restricted browser API key
// via `options.apiKey`. The key is not compiled into the app; it is a config value on
// the host, and §14.2 has the rules about where it lives (never `appsettings.json`).

import { dispatch, createViewportReporter } from "./interop.js";

const SCRIPT_ID = "google-maps-js";
let loadPromise = null;

// The Google Maps JS API is a single global that has to be loaded exactly once per page.
// If a caller creates a second map before the first finishes loading, the second one
// waits on the same promise — no double-load, no race.
async function ensureGoogleMaps(apiKey) {
    if (typeof google !== "undefined" && google.maps) return google;
    if (loadPromise) return loadPromise;
    if (typeof document === "undefined") {
        throw new Error("map.googlemaps.js: no document — running outside a browser?");
    }
    if (!apiKey) {
        // §4.5 rule: a map that cannot get a key shows a stated error, not a grey rectangle.
        // The C# side catches this and RideMap.razor renders the message.
        throw new Error("Google Maps: no API key was supplied.");
    }

    loadPromise = new Promise((resolve, reject) => {
        // Callback the Maps loader jumps to when ready — Google's own bootstrap pattern.
        const callbackName = "__dlrGoogleMapsReady__";
        window[callbackName] = () => {
            delete window[callbackName];
            resolve(window.google);
        };

        const script = document.createElement("script");
        script.id = SCRIPT_ID;
        script.async = true;
        script.defer = true;
        script.src =
            "https://maps.googleapis.com/maps/api/js" +
            "?key=" + encodeURIComponent(apiKey) +
            "&callback=" + callbackName +
            "&v=weekly" +
            "&libraries=";
        script.onerror = () => reject(new Error("Google Maps JS failed to load."));
        document.head.appendChild(script);
    });

    return loadPromise;
}

export async function createMap(hostElement, options, callbacks) {
    if (!hostElement) {
        throw new Error("map.googlemaps.js: hostElement is required.");
    }

    hostElement.classList.remove("dlr-map-placeholder");
    hostElement.replaceChildren();

    const g = await ensureGoogleMaps(options.apiKey);

    const map = new g.maps.Map(hostElement, {
        center: { lat: options.latitude ?? 0, lng: options.longitude ?? 0 },
        zoom: options.zoomLevel ?? 12,
        heading: options.headingDeg ?? 0,
        disableDefaultUI: true,
        // Google Maps renders its own attribution; the terms require it to stay visible,
        // and there is no supported way to hide it.
    });

    // Every base-map module emits this shape (§4.5 v0.21).
    const readViewport = () => {
        const bounds = map.getBounds();
        if (!bounds) return null;
        const ne = bounds.getNorthEast();
        const sw = bounds.getSouthWest();
        const rect = hostElement.getBoundingClientRect();
        const dpr = window.devicePixelRatio || 1;
        return {
            topLeftLatitude: ne.lat(),
            topLeftLongitude: sw.lng(),
            bottomRightLatitude: sw.lat(),
            bottomRightLongitude: ne.lng(),
            zoomLevel: map.getZoom() ?? 0,
            headingDeg: map.getHeading() ?? 0,
            canvasWidthPx: Math.round(rect.width * dpr),
            canvasHeightPx: Math.round(rect.height * dpr),
            devicePixelRatio: dpr,
        };
    };

    const reporter = createViewportReporter(readViewport, () => callbacks?.onViewportChanged);
    const emitViewport = reporter.report;

    // "idle" is the settle event — on its own it leaves the overlay frozen for the whole
    // of a pan. `bounds_changed` fires as the view moves, but Google raises it per changed
    // value rather than per frame, so it is used as the "the map is moving" signal and the
    // pump samples getBounds() every frame until "idle" says it stopped. Starting the pump
    // is idempotent, so the run of bounds_changed events a single drag produces is one run.
    map.addListener("bounds_changed", reporter.startTracking);
    map.addListener("heading_changed", reporter.startTracking);
    map.addListener("idle", reporter.stopTracking);

    // A tap on the map, in lat / lon (§16.1). Google raises "click" only for a tap, not
    // for the mouse-up that ends a pan, so dragging the map never places a marker.
    map.addListener("click", (event) => {
        if (!event?.latLng) return;
        dispatch(callbacks?.onMapClicked, "OnMapClicked", {
            latitude: event.latLng.lat(),
            longitude: event.latLng.lng(),
        });
    });

    // Kick one out immediately.
    setTimeout(emitViewport, 0);

    return {
        provider: "googlemaps",
        setCamera(camera) {
            map.setCenter({ lat: camera.latitude, lng: camera.longitude });
            map.setZoom(camera.zoomLevel);
            map.setHeading(camera.headingDeg ?? 0);
            emitViewport();
        },
        dispose() {
            // Drop the callbacks first — teardown can emit one last idle event, and by then
            // C# has disposed the DotNetObjectReference it would dispatch into.
            callbacks = null;
            reporter.dispose();
            // Google Maps has no explicit destroy, but detaching from the DOM and letting
            // GC run is the documented pattern.
            hostElement.replaceChildren();
        },
    };
}
