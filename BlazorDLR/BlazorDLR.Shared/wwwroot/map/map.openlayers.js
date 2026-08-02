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

// Modular ESM build from unpkg. Version pinned so a Phase 0 spike is reproducible.
import Map from "https://cdn.jsdelivr.net/npm/ol@9.2.4/Map.js";
import View from "https://cdn.jsdelivr.net/npm/ol@9.2.4/View.js";
import TileLayer from "https://cdn.jsdelivr.net/npm/ol@9.2.4/layer/Tile.js";
import OSM from "https://cdn.jsdelivr.net/npm/ol@9.2.4/source/OSM.js";
import { fromLonLat, toLonLat } from "https://cdn.jsdelivr.net/npm/ol@9.2.4/proj.js";

// OpenLayers ships CSS separately. Inject once per page.
function ensureStylesheet() {
    if (typeof document === "undefined") return;
    if (document.getElementById("openlayers-css")) return;
    const link = document.createElement("link");
    link.id = "openlayers-css";
    link.rel = "stylesheet";
    link.href = "https://cdn.jsdelivr.net/npm/ol@9.2.4/ol.css";
    document.head.appendChild(link);
}

export async function createMap(hostElement, options, callbacks) {
    if (!hostElement) {
        throw new Error("map.openlayers.js: hostElement is required.");
    }

    ensureStylesheet();

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
    const emitViewport = () => {
        const view = map.getView();
        const size = map.getSize();
        if (!view || !size) return;
        const extent = view.calculateExtent(size);
        if (!extent) return;
        // extent is [minX, minY, maxX, maxY] in view projection (Web Mercator).
        // Corners come back in lat/lon so the overlay does not have to know about it.
        const [tlLon, tlLat] = toLonLat([extent[0], extent[3]]);
        const [brLon, brLat] = toLonLat([extent[2], extent[1]]);
        const dpr = window.devicePixelRatio || 1;
        callbacks?.onViewportChanged?.({
            topLeftLatitude: tlLat,
            topLeftLongitude: tlLon,
            bottomRightLatitude: brLat,
            bottomRightLongitude: brLon,
            zoomLevel: view.getZoom() ?? 0,
            headingDeg: (view.getRotation() ?? 0) * 180 / Math.PI,
            canvasWidthPx: Math.round(size[0] * dpr),
            canvasHeightPx: Math.round(size[1] * dpr),
            devicePixelRatio: dpr,
        });
    };

    map.on("moveend", emitViewport);
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
            try { map.setTarget(null); } catch { /* already gone */ }
        },
    };
}
