// OpenLayers + OpenStreetMap tiles — web base map (§4.5 v0.21, §18.3).
//
// v0.21 replaces MapLibre with OpenLayers, base-map role only. This module handles ONLY
// the base map — tiles, camera, rotation, attribution. Every rider pin, marker and route
// lives in SkiaMapOverlay on top (§4.5 v0.21).
//
// OSM's tile usage policy is a real constraint, not a formality (§4.5, §13 Q26). This
// module points at `tile.openstreetmap.org` directly, so no third-party style server sits
// between us and OSM. Permanent attribution is declared on the tile source; removing it
// removes the tile source.

import { dispatch, createViewportReporter } from "./interop.js";

// Version pinned so a build is reproducible.
//
// We load the *dist* bundle, not the per-module ESM sources under `ol@9.2.4/Map.js`.
// Those are unbundled npm sources: internally they import bare specifiers (`rbush`,
// `earcut`, `pbf`, …) which a browser cannot resolve without an import map, and the
// module graph fails with "Failed to resolve module specifier \"rbush\"". Per-entry CDN
// ESM bundles (jsDelivr `/+esm`) fix the specifiers but inline OpenLayers separately into
// every entry, so `new TileLayer()` and `new Map()` would come from different copies of
// the library and OpenLayers' internal instanceof checks would reject the layer.
//
// `dist/ol.js` is a single UMD build: one request, one copy, everything hanging off the
// `ol` global (`ol.Map`, `ol.layer.Tile`, `ol.source.OSM`, `ol.proj.*`).
const OL_BASE = "https://cdn.jsdelivr.net/npm/ol@9.2.4";

let olLoad = null;

// Resolves to the `ol` global, loading the bundle on first use. Rejects — and clears the
// cached promise so a later map can retry — if the CDN is unreachable, which is what puts
// a stated error in front of the user instead of a blank grey rectangle (§4.5).
function loadOpenLayers() {
    if (typeof window !== "undefined" && window.ol) return Promise.resolve(window.ol);
    if (olLoad) return olLoad;

    olLoad = new Promise((resolve, reject) => {
        const existing = document.getElementById("openlayers-js");
        const script = existing ?? document.createElement("script");
        script.addEventListener("load", () => {
            if (window.ol) {
                resolve(window.ol);
            } else {
                reject(new Error("OpenLayers loaded but did not define the `ol` global."));
            }
        });
        script.addEventListener("error", () => reject(new Error(`Could not load OpenLayers from ${OL_BASE}/dist/ol.js.`)));
        if (!existing) {
            script.id = "openlayers-js";
            script.src = `${OL_BASE}/dist/ol.js`;
            script.async = true;
            document.head.appendChild(script);
        }
    });

    olLoad.catch(() => { olLoad = null; });
    return olLoad;
}

// OpenLayers ships CSS separately. Inject once per page.
function ensureStylesheet() {
    if (typeof document === "undefined") return;
    if (document.getElementById("openlayers-css")) return;
    const link = document.createElement("link");
    link.id = "openlayers-css";
    link.rel = "stylesheet";
    link.href = `${OL_BASE}/ol.css`;
    document.head.appendChild(link);
}

export async function createMap(hostElement, options, callbacks) {
    if (!hostElement) {
        throw new Error("map.openlayers.js: hostElement is required.");
    }

    ensureStylesheet();

    const ol = await loadOpenLayers();
    const { Map, View } = ol;
    const TileLayer = ol.layer.Tile;
    const OSM = ol.source.OSM;
    const { fromLonLat, toLonLat } = ol.proj;

    hostElement.classList.remove("dlr-map-placeholder");
    hostElement.replaceChildren();

    const map = new Map({
        target: hostElement,
        // OSM source ships attribution baked in — OpenLayers renders it inside the
        // AttributionControl by default. Removing the source removes the attribution,
        // which is the point of §4.5's rule that OSM attribution is permanent.
        layers: [new TileLayer({ source: new OSM() })],
        view: new View({
            center: fromLonLat([options.longitude ?? 0, options.latitude ?? 0]),
            zoom: options.zoomLevel ?? 2,
            rotation: (options.headingDeg ?? 0) * Math.PI / 180,
        }),
        controls: [],
    });

    // Every base-map module emits this exact shape (§4.5 v0.21).
    const readViewport = () => {
        const view = map.getView();
        const size = map.getSize();
        if (!view || !size) return null;
        const extent = view.calculateExtent(size);
        if (!extent) return null;
        // extent is [minX, minY, maxX, maxY] in view projection (Web Mercator).
        // Corners come back in lat/lon so the overlay does not have to know about it.
        const [tlLon, tlLat] = toLonLat([extent[0], extent[3]]);
        const [brLon, brLat] = toLonLat([extent[2], extent[1]]);
        const dpr = window.devicePixelRatio || 1;
        return {
            topLeftLatitude: tlLat,
            topLeftLongitude: tlLon,
            bottomRightLatitude: brLat,
            bottomRightLongitude: brLon,
            zoomLevel: view.getZoom() ?? 0,
            headingDeg: (view.getRotation() ?? 0) * 180 / Math.PI,
            canvasWidthPx: Math.round(size[0] * dpr),
            canvasHeightPx: Math.round(size[1] * dpr),
            devicePixelRatio: dpr,
        };
    };

    const reporter = createViewportReporter(readViewport, () => callbacks?.onViewportChanged);
    const emitViewport = reporter.report;

    // OpenLayers is the one provider that hands us its own frame hook, so it needs no
    // pump: `postrender` fires after every frame the map paints, including each frame of
    // a drag, a wheel-zoom animation and a kinetic fling. Sampling the view *after* the
    // paint is also what keeps the overlay in register — the extent we read is the one
    // that was just drawn, not the one that is about to be. Frames where nothing moved
    // (tiles fading in) are dropped by the reporter and never reach C#.
    map.on("postrender", emitViewport);

    // Belt and braces: a view change that somehow paints nothing still settles here.
    map.on("moveend", emitViewport);

    // A tap on the map, in lat / lon (§16.1). OpenLayers' "click" fires only for a real
    // click — a drag that ends over the map does not raise it — so a pan never places a
    // marker by accident.
    map.on("click", (event) => {
        if (!event.coordinate) return;
        const [lon, lat] = toLonLat(event.coordinate);
        dispatch(callbacks?.onMapClicked, "OnMapClicked", { latitude: lat, longitude: lon });
    });

    // Kick one out immediately.
    setTimeout(emitViewport, 0);

    return {
        provider: "openlayers",
        setCamera(camera) {
            map.getView().animate({
                center: fromLonLat([camera.longitude, camera.latitude]),
                zoom: camera.zoomLevel,
                rotation: (camera.headingDeg ?? 0) * Math.PI / 180,
                duration: 0,
            });
            emitViewport();
        },
        dispose() {
            // Drop the callbacks first. Detaching the map can emit one last moveend, and by
            // then C# has disposed the DotNetObjectReference — dispatching into it logs
            // "no tracked object with id N" for a result nobody is waiting for.
            callbacks = null;
            reporter.dispose();
            try { map.setTarget(null); } catch { /* already gone */ }
        },
    };
}
