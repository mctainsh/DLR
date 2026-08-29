// MapLibre GL JS — the base map on every surface (§4.5 v0.24).
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
// NOTHING IN HERE REACHES A THIRD PARTY EXCEPT THE TILES. The library is vendored beside this
// file and the style is built inline, so the only host a map talks to is whichever tile server
// the rider chose — and on an offline pack, none at all. That is the property the offline work
// exists to protect, and it is easy to lose by "just" pulling one asset off a CDN.
//
// Which tiles is the rider's choice (§4.5): C# resolves a MapSource and hands it down through
// `options.source`. OSM's tile usage policy remains a real constraint on the default (§13 Q26) —
// this module requests `tile.openstreetmap.org` directly, with no style server in between.

import { dispatch, createViewportReporter, registerTracker, unregisterTracker } from "./interop.js";

// MapLibre GL JS 4.7.1, VENDORED — `lib/maplibre/` beside this file, not a CDN.
//
// It used to come from jsDelivr, and that was the single thing standing between this app and a
// map in a dead zone: the library was fetched on first use, so a phone with no signal failed
// here, before a tile was ever requested. Downloaded tiles would not have helped. Vendoring is
// therefore a prerequisite for offline maps rather than a tidy-up, and it also takes a runtime
// host dependency off the two online modes (§4.5 listed it as an outstanding cost).
//
// Resolved through `import.meta.url` rather than a document-relative path: `script.src` and
// `link.href` resolve against the *page*, and this module is served out of the shared library's
// static assets — so a page at any route would otherwise look for the bundle in the wrong place.
//
// `maplibre-gl.js` is the UMD bundle: one request, one copy of the library, everything hanging
// off the `maplibregl` global. The trap the OpenLayers module documented still applies and is
// worth keeping written down, because the next person to "modernise" this will hit it: the
// per-module ESM sources import bare specifiers a browser cannot resolve without an import map,
// and per-entry ESM bundles inline separate copies of the library so instanceof checks across
// entries fail.
const MAPLIBRE_BASE = new URL("./lib/maplibre/", import.meta.url);
const SCRIPT_URL = new URL("maplibre-gl.js", MAPLIBRE_BASE).href;
const STYLESHEET_URL = new URL("maplibre-gl.css", MAPLIBRE_BASE).href;

// The PMTiles protocol plugin, vendored beside MapLibre for the same reason (§13 Q26). It is what
// turns a `pmtiles://` source into HTTP range requests against a single archive — no tile server,
// which is the whole property that makes an offline pack possible.
const PMTILES_URL = new URL("./lib/pmtiles/pmtiles.js", import.meta.url).href;

// The vector basemap: Protomaps' themes, their glyphs and their sprites, all local.
//
// Built as string concatenation rather than `new URL(...)`, because the glyph path carries the
// literal placeholders `{fontstack}` and `{range}` that MapLibre substitutes — and the URL
// constructor percent-encodes the braces, which turns the template into a 404 per font.
//
// Two themes, and the archive is not one of them. A PMTiles pack holds vector geometry with no
// colour in it, so light and dark are two style documents over the same tiles — the rider switches
// with no download and no second pack (§13 Q26). Which one is the rider's choice, resolved in C#
// and handed down through `options.source.theme`; see MapTheme in MapSource.cs.
//
// `light` remains the default. The Skia overlay's route styling is tuned against light ground
// (RouteStyle.Default draws a dark casing under the line and white chevrons over it) and a map is
// read through a visor in daylight, so dark is the deliberate choice rather than the ambient one —
// and choosing it moves the base map only. Nothing the overlay draws follows it.
//
// The glyphs are shared between the two: a font carries no colour, and shipping one copy is the
// difference between a second theme costing ~290 KB and costing ~1 MB.
const STYLE_BASE = new URL("./style/", import.meta.url).href;
const GLYPHS_URL = STYLE_BASE + "glyphs/{fontstack}/{range}.pbf";

const VECTOR_THEMES = {
    light: { style: STYLE_BASE + "basemap.json", sprite: STYLE_BASE + "sprite/light" },
    dark: { style: STYLE_BASE + "basemap.dark.json", sprite: STYLE_BASE + "sprite/dark" },
};

const DEFAULT_THEME = "light";

// The tile source is no longer a constant in this file: the rider chooses it (§4.5), and C#
// hands the choice down through `options.source` as { kind, tileUrl, packId, archiveUrl,
// attribution, maxZoom, theme }. See MapSource.cs, which is the half of that contract with the
// documentation. `theme` is read by the offline branch alone — the raster kinds arrive as
// finished images and have no cartography to choose.
//
// What is still true is that a source and its attribution travel together — ODbL's credit is a
// condition of using OSM's tiles and §4.5 makes it permanent, so it is declared ON the source
// below and MapLibre's own AttributionControl renders it from the style. Removing the credit
// means removing the tiles, which is the point.
//
// The fallback here is for a caller that supplies no source at all; the C# side resolves the
// same default, so these agree by construction rather than by coincidence.
const DEFAULT_SOURCE = {
    kind: "osm",
    tileUrl: "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
    packId: null,
    attribution:
        '© <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noreferrer">OpenStreetMap</a> contributors',
    maxZoom: 19,
};

let libraryLoad = null;

// Resolves to the `maplibregl` global, loading the bundle on first use. Rejects — and clears the
// cached promise so a later map can retry — if it cannot be loaded, which is what puts a stated
// error in front of the user instead of a blank grey rectangle (§4.5).
//
// That branch is not dead now the file ships with the app: a bundle can still fail to parse, and
// on the web it is served over HTTP like everything else and can 404 behind a stale cache.
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
            reject(new Error(`Could not load MapLibre GL JS from ${SCRIPT_URL}.`)));
        if (!existing) {
            script.id = "maplibre-js";
            script.src = SCRIPT_URL;
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
    link.href = STYLESHEET_URL;
    document.head.appendChild(link);
}

let pmtilesLoad = null;
let pmtilesProtocol = null;

// Loads the PMTiles plugin and registers the `pmtiles://` protocol with MapLibre, once per page.
//
// The Protocol instance is kept because it holds the archive's directory cache — a PMTiles read
// is two range requests (find the tile, fetch it) and the first is answered from that cache after
// the first tile. Dropping it would double every request for the life of the map.
async function ensurePmtilesProtocol(maplibregl) {
    if (pmtilesProtocol) return;

    pmtilesLoad ??= new Promise((resolve, reject) => {
        const existing = document.getElementById("pmtiles-js");
        const script = existing ?? document.createElement("script");
        script.addEventListener("load", () =>
            window.pmtiles
                ? resolve(window.pmtiles)
                : reject(new Error("pmtiles loaded but did not define the `pmtiles` global.")));
        script.addEventListener("error", () =>
            reject(new Error(`Could not load the PMTiles plugin from ${PMTILES_URL}.`)));
        if (!existing) {
            script.id = "pmtiles-js";
            script.src = PMTILES_URL;
            script.async = true;
            document.head.appendChild(script);
        }
    });
    pmtilesLoad.catch(() => { pmtilesLoad = null; });

    const pmtiles = await pmtilesLoad;
    const protocol = new pmtiles.Protocol();

    // Wrapped rather than passed directly, so the handler cannot lose its `this` if the plugin
    // ever stops defining `tile` as a bound property — and, since v0.27, so that a read which
    // fails says which archive it was reading and what the archive says for itself. See
    // `describePmtilesFailure`: what reaches the rider without it is "Load failed", which is the
    // browser's whole vocabulary for a fetch that never got a response.
    maplibregl.addProtocol("pmtiles", async (params, abortController) => {
        try {
            return await protocol.tile(params, abortController);
        } catch (error) {
            throw await describePmtilesFailure(params, error, abortController);
        }
    });
    pmtilesProtocol = protocol;
}

// The archive part of a `pmtiles://…/{z}/{x}/{y}` URL, and the tile coordinates after it.
function splitPmtilesUrl(url) {
    const text = String(url ?? "").replace(/^pmtiles:\/\//, "");
    const tile = text.match(/^(.*)\/(\d+)\/(\d+)\/(\d+)$/);

    return tile
        ? { archive: tile[1], tile: `z${tile[2]}/x${tile[3]}/y${tile[4]}` }
        : { archive: text, tile: null };
}

// A pack URL with its secret taken out.
//
// The loopback server puts a per-run token in the first path segment, and it is a real secret: it
// is what stops another app on the same phone walking the port range and reading whatever this one
// has downloaded. These strings go to C#, into the diagnostics log, and onto the screen under
// "Map tiles unavailable" — all three of which a rider may mail to somebody. The port and the pack
// are the diagnostic content; the token is not.
function redactPackUrl(url) {
    try {
        const parsed = new URL(url);

        if (parsed.hostname !== "127.0.0.1" && parsed.hostname !== "localhost") return url;

        const segments = parsed.pathname.split("/").filter(Boolean);

        if (segments.length > 1) segments[0] = "…";

        return `${parsed.origin}/${segments.join("/")}`;
    } catch {
        return String(url ?? "");
    }
}

// One range request against the archive itself, so a failed tile can say whether the thing serving
// it is answering at all. Cached briefly: a source that cannot be read fails once per tile and
// twenty of those arrive together, which should cost one probe rather than twenty.
//
// The distinction it buys is the one that matters and the one no other line in this file can make:
// a status code means the server answered and the archive or the range is wrong, while a network
// error means nothing is listening on that port any more — which is what a dead loopback listener
// looks like from in here.
const archiveProbes = new Map();

function probeArchive(archiveUrl) {
    if (!archiveProbes.has(archiveUrl)) {
        const probe = fetch(archiveUrl, { method: "GET", headers: { Range: "bytes=0-0" }, cache: "no-store" })
            .then(
                (response) => `the archive answered HTTP ${response.status}`,
                (error) => `the archive could not be reached at all (${error?.name ?? "Error"}: ${error?.message ?? error})`);

        archiveProbes.set(archiveUrl, probe);

        // Not kept: the loopback server binds a new port when its listener dies, and a stale
        // verdict about the old one would outlive the failure it described.
        probe.finally(() => setTimeout(() => archiveProbes.delete(archiveUrl), 10_000));
    }

    return archiveProbes.get(archiveUrl);
}

// What to throw in place of whatever the PMTiles reader threw.
//
// MapLibre reports the error it is given and nothing else, and what the reader gives it for an
// unreachable archive is the fetch layer's bare "Load failed" / "Failed to fetch" — no URL, no
// status, no source. That is indistinguishable from a corrupt archive, a wrong range, a pack the
// device deleted, and a listener that has stopped answering, which are four different faults with
// four different fixes.
async function describePmtilesFailure(params, error, abortController) {
    // A cancelled tile is not a failure. Every pan and every style swap aborts the fetches in
    // flight, and dressing those up as errors would fill the log with the map working correctly.
    if (abortController?.signal?.aborted || error?.name === "AbortError") return error;

    const { archive, tile } = splitPmtilesUrl(params?.url);

    let verdict = "";

    try {
        verdict = ` — ${await probeArchive(archive)}`;
    } catch {
        // The probe is a courtesy. Its own failure must not replace the error it was explaining.
    }

    const described = new Error(
        `PMTiles read failed for ${redactPackUrl(archive)}` +
        `${tile ? ` at ${tile}` : ""} (${params?.type ?? "tile"}): ` +
        `${error?.name ?? "Error"}: ${error?.message ?? error}${verdict}`);

    described.name = "PmtilesError";

    // The original too, whole: on a phone this is what a remote debugger shows, and the stack is
    // in it.
    console.error("[dlr-map] pmtiles", { url: params?.url, type: params?.type }, error);

    return described;
}

// One in-flight fetch per theme, keyed by name — a rider comparing light against dark on the
// settings screen should pay for each document once, not once per switch.
const vectorStyleLoads = new Map();

// A vendored Protomaps style document, fetched once and handed out as copies.
//
// A copy per caller because the caller patches it — the source URL differs per archive, and a
// shared object would leave the second map pointing at the first one's pack.
async function vectorStyleTemplate(theme) {
    // Resolved to a name we ship before it is used as a cache key, so a theme from a newer build
    // cannot fill the map with entries that all hold the same document.
    const name = Object.hasOwn(VECTOR_THEMES, theme) ? theme : DEFAULT_THEME;
    const chosen = VECTOR_THEMES[name];

    if (!vectorStyleLoads.has(name)) {
        const load = fetch(chosen.style).then((response) => {
            if (!response.ok) {
                throw new Error(`Could not load the offline map style from ${chosen.style}.`);
            }
            return response.json();
        });
        load.catch(() => { vectorStyleLoads.delete(name); });
        vectorStyleLoads.set(name, load);
    }

    return { style: structuredClone(await vectorStyleLoads.get(name)), sprite: chosen.sprite };
}

// A raster style over an XYZ source — OpenStreetMap, or whatever tile server the rider named.
//
// One layer id, "basemap", whichever source is under it. That is deliberate: `setStyle` swaps the
// whole style, and keeping the ids stable means anything that ever needs to insert a layer
// relative to the base map has one name to refer to rather than several.
function rasterStyle(source) {
    return {
        version: 8,
        sources: {
            basemap: {
                type: "raster",
                tiles: [source.tileUrl],
                tileSize: 256,
                // Requesting past what a server holds returns 404s, which reads on screen as the
                // map dissolving when a rider zooms in on a marker — exactly the zoom a marker is
                // placed at (§16.1). OSM's raster stops at 19; a custom source says its own.
                maxzoom: source.maxZoom ?? 19,
                attribution: source.attribution ?? "",
            },
        },
        layers: [{ id: "basemap", type: "raster", source: "basemap" }],
    };
}

// The vendored style, pointed at one archive on this device. Everything it references — the tiles,
// the glyphs, the sprite — is local, so this draws with the radio off.
//
// The sprite travels with the style rather than being chosen separately: the icons are painted for
// their theme, and a light sheet over the dark document puts dark glyphs on dark ground.
async function offlineStyle(source) {
    const { style, sprite } = await vectorStyleTemplate(source.theme);

    style.glyphs = GLYPHS_URL;
    style.sprite = sprite;

    // The template ships one vector source under a name of its own choosing, pointed at a
    // placeholder. Read the name back rather than hard-coding it: it belongs to the upstream
    // style, and every one of the 68 layers refers to it.
    const sourceName = Object.keys(style.sources)[0];

    style.sources[sourceName] = {
        ...style.sources[sourceName],
        url: `pmtiles://${source.archiveUrl}`,
    };

    addWorldUnderlay(style, sourceName, source.archiveUrl);

    return style;
}

// The source name for the coarse ground beneath a pack. Not a name any vendored style uses, and
// checked below rather than assumed.
const WORLD_SOURCE = "dlr-pack-world";

// The ground layers, in the order the vendored styles declare them. Ids rather than a rule about
// source-layers, because these three are the ones that paint the whole surface — land, what grows
// on it, and water — and everything after them is detail that only exists inside the region.
const WORLD_GROUND_LAYERS = ["earth", "landcover", "water"];

// A coarse world under the pack, drawn from the pack's own zoom-0 tile (§4.5, §13 Q26).
//
// WHAT THIS FIXES. A regional pack holds exactly the tiles its region's box touches — for
// Queensland that is ONE tile at z0, one at z1, one at z2, one at z3, two at z4, six at z5. The
// archive publishes that box in its header, MapLibre reads it through the PMTiles TileJSON, and it
// then refuses to ask for anything outside it. So the map only ever paints inside a rectangle, and
// the rest of the screen is the style's `background` colour — flat grey, no coastline, nothing.
//
// The effect reads as a dead zoom range rather than as a dead area, which is what makes it so
// confusing to hit. Right out at world zoom the pack's single z0 tile happens to cover the whole
// screen, so the map looks fine; right in, the rider is inside the rectangle, so the map looks
// fine; in between — roughly z2 to z7 — the rectangle is a strip in a grey void, and a pan of any
// distance leaves nothing on screen at all.
//
// THE FIX. A second source over the same archive, capped at `maxzoom: 0`, so MapLibre only ever
// requests the one tile every pack contains — z0, which is the whole world — and stretches it over
// whatever is on screen. The three ground layers are cloned onto it underneath the real ones. It
// costs one tile (about 90 KB, already inside the pack), reaches no network, and cannot fail
// differently from the pack itself.
//
// It does not invent detail. Outside the region a rider gets land, water and a coarse coastline
// and no roads, which is the truth about what they downloaded — and inside it nothing changes,
// because the detailed layers paint over the top.
function addWorldUnderlay(style, sourceName, archiveUrl) {
    const ground = style.layers.filter(layer => WORLD_GROUND_LAYERS.includes(layer.id));

    // A vendored style that no longer declares them. Better a map with the old grey than a map
    // that throws on a style this build does not recognise.
    if (ground.length === 0 || Object.hasOwn(style.sources, WORLD_SOURCE)) return;

    style.sources[WORLD_SOURCE] = {
        ...style.sources[sourceName],
        url: `pmtiles://${archiveUrl}`,
        // The whole point. MapLibre asks for tiles at min(zoom, maxzoom), so this pins every
        // request to 0/0/0 — the one tile a regional extract is guaranteed to hold, because the
        // z0 tile's box touches every region there is.
        maxzoom: 0,
    };

    const clones = ground.map(layer => {
        const clone = { ...layer, id: `${layer.id}-world`, source: WORLD_SOURCE };

        // Cleared so the clone draws at every zoom: the layer's own range is about detail
        // arriving, and this one is only ever the same tile stretched further.
        delete clone.minzoom;
        delete clone.maxzoom;

        return clone;
    });

    // After the background and before everything else, so the pack's real layers — and the Skia
    // overlay above them — are unchanged wherever the pack has data.
    style.layers = [style.layers[0], ...clones, ...style.layers.slice(1)];
}

// -- Offline coverage ------------------------------------------------------------------------
//
// A pack covers one region, and MapLibre knows its box — so panning off the edge raises no
// error, it just stops asking, and the world underlay leaves ground with no roads on it. That is
// a failure that looks like success, so it is computed here and reported to C# (§13 Q26).

// The pack's source in a style this module built — read off the document rather than out of
// `map.getStyle()`, which serialises and deep-clones all 68 layers every time it is asked.
// Never the world underlay: that is this module's own clone over the same archive.
function packSourceNameOf(style) {
    const sources = style?.sources ?? {};

    return Object.keys(sources).find(name =>
        name !== WORLD_SOURCE && String(sources[name]?.url ?? "").startsWith("pmtiles://")) ?? null;
}

// Coverage for what is on screen, or null while it cannot be known — the archive's header
// arrives a round trip after the style, and answering "no tiles" in that gap would flash the
// warning on every map that opens on a pack.
function readCoverage(map, source, packSourceName) {
    const zoomLevel = map.getZoom() ?? 0;

    // Only a pack is box-limited. An online source is asked for the world, and a tile it refuses
    // arrives as an error instead.
    if (source?.kind !== "offline" || !packSourceName) return { hasTiles: true, zoomLevel };

    const pack = map.getSource(packSourceName);
    const box = pack?.bounds;
    const view = map.getBounds();

    if (!Array.isArray(box) || box.length !== 4 || !view) return null;

    // Intersection, not "is the centre inside it": a rider whose ride runs off the edge is
    // looking at the half that is covered, and a banner over a map with roads on it is worse
    // than saying nothing.
    const overlaps = view.getWest() <= box[2] && view.getEast() >= box[0]
        && view.getSouth() <= box[3] && view.getNorth() >= box[1];

    // The floor only. Past the archive's deepest zoom MapLibre stretches the last tile it has;
    // below its shallowest there is no tile to stretch, the world underlay's z0 included.
    const deepEnough = Math.floor(zoomLevel) >= (pack.minzoom ?? 0);

    return { hasTiles: overlaps && deepEnough, zoomLevel };
}

// A source in one line, for a log somebody reads days later. Every field that decides whether the
// map can draw, and no field that does not — the token inside an archive URL is taken out by
// `redactPackUrl` rather than being trusted to a log file a rider may send on.
function describeSource(source) {
    const chosen = source ?? DEFAULT_SOURCE;

    if (chosen.kind === "offline") {
        return `offline pack '${chosen.packId ?? "(none chosen)"}', ${chosen.theme ?? DEFAULT_THEME} theme, ` +
            `${chosen.archiveUrl ? redactPackUrl(chosen.archiveUrl) : "NO ARCHIVE URL"}`;
    }

    return `${chosen.kind ?? "osm"} tiles, ${chosen.tileUrl ?? "(no tile URL)"}, to z${chosen.maxZoom ?? 19}`;
}

// The style for a chosen source (§4.5). Async because the offline one is a document on disk.
async function styleFor(source) {
    const chosen = source ?? DEFAULT_SOURCE;

    if (chosen.kind === "offline") {
        // No archive URL means the device could not serve the pack — deleted, or a host that
        // holds none. C# performs the same fallback before it gets here; this is belt and braces
        // for a source that arrives some other way, because the alternative is a blank map.
        if (chosen.archiveUrl) {
            return await offlineStyle(chosen);
        }
        return rasterStyle(DEFAULT_SOURCE);
    }

    return rasterStyle(chosen.tileUrl ? chosen : DEFAULT_SOURCE);
}

// A north indicator, top left, shown only once the map is actually off north.
//
// MapLibre's own NavigationControl rather than a drawn arrow: it points north, and tapping it
// turns the map back — which is the thing a rider who has rotated by accident actually wants,
// and the hardest thing to rediscover once the tiles no longer read as a map.
//
// Hidden at bearing zero because a compass that always says "north is up" on a map that is
// always north-up is furniture. The class it toggles is what the stylesheet keys off; the
// threshold is half a degree so a fractional bearing left by a gesture does not flicker it.
function addCompass(maplibregl, map, hostElement) {
    map.addControl(
        new maplibregl.NavigationControl({ showCompass: true, showZoom: false, visualizePitch: false }),
        "top-left");

    const sync = () => hostElement.classList.toggle("dlr-map-rotated", Math.abs(map.getBearing() ?? 0) > 0.5);

    map.on("rotate", sync);
    map.on("moveend", sync);
    sync();
}

export async function createMap(hostElement, options, callbacks) {
    if (!hostElement) {
        throw new Error("map.maplibre.js: hostElement is required.");
    }

    ensureStylesheet();

    const maplibregl = await loadMapLibre();

    // Both before the map is constructed. An offline source resolves to a `pmtiles://` URL, and
    // MapLibre refuses a protocol nothing has registered; and the offline style is a document
    // that has to be fetched and patched before there is a style to open with.
    if (options.source?.kind === "offline") {
        await ensurePmtilesProtocol(maplibregl);
    }

    const initialStyle = await styleFor(options.source);

    // Which source in that style is the archive, resolved once per style rather than per report.
    let packSourceName = packSourceNameOf(initialStyle);

    // What the map is currently drawing with, kept because the error handler below is the one
    // place that needs it and the style document no longer says: MapLibre reports a failed source
    // by the id inside the style ("protomaps"), which is the same word for every pack ever
    // downloaded and names neither the archive nor the region.
    let currentSource = options.source ?? DEFAULT_SOURCE;

    hostElement.classList.remove("dlr-map-placeholder");
    hostElement.replaceChildren();

    // Rotation is per screen; pitch is refused everywhere. See below.
    const allowRotation = options.allowRotation !== false;

    const map = new maplibregl.Map({
        container: hostElement,
        style: initialStyle,
        center: [options.longitude ?? 0, options.latitude ?? 0],
        zoom: options.zoomLevel ?? 2,
        bearing: options.headingDeg ?? 0,
        // The overlay draws every pin, so the base map never needs to be interrogated for
        // what is under the cursor. Turning this off skips a hit-test on every mouse move.
        interactive: true,

        // No 3D, on any map, and this is a correctness constraint rather than a style choice.
        // SkiaMapOverlay projects flat Web Mercator out of a MapViewport that carries no pitch
        // term, so a tilted base map would leave every pin, track and circle drawn for a view
        // nobody is looking at — the tiles would lean away and the markers would stay put.
        //
        // maxPitch alone is not enough: the gesture handlers have to be refused as well, or a
        // two-finger drag still fights the camera before being clamped back to zero.
        maxPitch: 0,
        pitchWithRotate: false,
        touchPitch: false,

        // Rotation is fine for the overlay — MapViewport carries a heading and the tracking in
        // overlay.js counter-rotates by the bearing delta — so this is a per-screen choice.
        dragRotate: allowRotation,

        // Attribution is not optional (§4.5). Left at MapLibre's default control rather than
        // rendered by us, so it survives anyone editing the shared component.
        attributionControl: { compact: false },
    });

    if (!allowRotation) {
        // The constructor flag covers mouse drag. Touch is a separate handler, and on a phone
        // it is the one that matters: a two-finger pinch on a picking map would otherwise turn
        // it a few degrees on the way in, which is exactly the disorientation this prevents.
        map.touchZoomRotate.disableRotation();
    } else {
        addCompass(maplibregl, map, hostElement);
    }

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
        // this encloses the rotated view rather than tracing it, so the box is *bigger* than the
        // canvas — by W*|cos| + H*|sin| across and W*|sin| + H*|cos| down. headingDeg goes with
        // it, and MapGeometry.ProjectToCanvas divides that inflation back out before it rotates.
        // Rotating without dividing it out is right at 0 and 180 and wrong at every other
        // bearing, which is how it went unnoticed until someone turned a phone sideways.
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

    // Publish the live projection so SkiaMapOverlay can follow the map between repaints
    // (see interop.js). This is MapLibre's own projection, asked at the moment of the frame —
    // an earlier attempt re-derived it in C# from the reported viewport and put the pins in the
    // wrong place, which is exactly the class of drift this hands back to the library.
    registerTracker(hostElement, {
        projectCss: (latitude, longitude) => map.project([longitude, latitude]),
        zoom: () => map.getZoom() ?? 0,
        bearing: () => map.getBearing() ?? 0,
        onMove: (handler) => map.on("move", handler),
        offMove: (handler) => map.off("move", handler),
    });

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

    // Whether the ground on screen has tiles behind it, told to C# when the answer changes.
    // Only an offline pack ever answers no; RideMap.razor is where a rider is told.
    //
    // Beside the viewport reporter rather than part of it: the overlay needs every frame of a
    // pan, and this needs the settled view only.
    let lastCoverageKey = null;

    const reportCoverage = () => {
        const coverage = readCoverage(map, currentSource, packSourceName);

        // Null is "cannot say yet", not "no tiles".
        if (!coverage) return;

        // The zoom is in the sentence a rider reads, so a gap at a new zoom is a new answer —
        // while a covered map says nothing, and its zoom is not worth an interop call.
        const key = coverage.hasTiles ? "covered" : `gap@${Math.floor(coverage.zoomLevel)}`;

        if (key === lastCoverageKey) return;

        lastCoverageKey = key;
        dispatch(callbacks?.onMapCoverage, "OnMapCoverage", coverage);
    };

    // `idle` is the settle event for every cause at once — a drag, a fling, an easeTo, tiles
    // finishing, a style swap. Answering mid-gesture would flicker the banner through a pan.
    map.on("idle", reportCoverage);

    // Everything the base map could not do, said out loud (§4.5).
    //
    // MapLibre does not throw for these — a tile source it cannot reach leaves a map object that
    // renders happily and draws nothing, which is a blank rectangle with no explanation anywhere.
    // Offline packs made this urgent: a pack served over the wrong scheme, an archive the WebView
    // will not fetch, a style whose glyphs 404, all look identical from the outside.
    //
    // Deliberately not filtered here. What is worth showing a rider is a decision for the shared
    // component, which can see whether the map ever drew anything; this end just reports.
    map.on("error", (event) => {
        const error = event?.error;

        // The URL and status matter more than the message. MapLibre parses tiles AND glyphs with
        // the same protobuf decoder, so a failed glyph fetch and a failed tile fetch produce the
        // identical "Unimplemented type: N" — and which of the two it was is the entire diagnosis.
        // AJAXError carries both; a decode failure carries neither, which is itself informative.
        const parts = [error?.message ?? String(error ?? "The map reported an error.")];

        // The type of the error is the cheapest half of that diagnosis and used to be dropped.
        // "TypeError" is a fetch that never reached anything; "AJAXError" reached a server and was
        // refused by it; "PmtilesError" is this module's own, and already carries the archive.
        if (error?.name && error.name !== "Error") parts.push(error.name);

        if (error?.status) parts.push(`HTTP ${error.status}`);
        if (error?.url) parts.push(redactPackUrl(error.url));
        if (event?.sourceId) parts.push(`source: ${event.sourceId}`);

        // Which source, always. "source: protomaps" is the style's internal id for the vector
        // layer group and is identical for every pack a rider has ever downloaded — so on its own
        // it cannot tell Queensland from New South Wales, an archive from a missing archive, or a
        // pack from the OSM fallback.
        parts.push(`using: ${describeSource(currentSource)}`);

        // Console too: on a phone this is what a remote debugger shows, and it carries the whole
        // object rather than the one line that fits on screen.
        console.error("[dlr-map]", error ?? event, event);
        dispatch(callbacks?.onMapError, "OnMapError", parts.join(" — "));
    });

    // The rider taking the map back off an automatic mode (§5.3).
    //
    // The live map has two modes that move the camera on their own — "follow me" re-centres on
    // every fix, and "travel direction up" turns the map as the rider turns — and both have to
    // yield the moment the rider moves the map themselves. Fighting a hand for control of a
    // camera is the one thing an automatic mode must never do.
    //
    // The whole difficulty is telling the rider's gesture apart from the mode's own move, and
    // the viewport event cannot: it reports where the map is, not who put it there. MapLibre
    // can. Every camera method takes an `eventData` bag that rides along on the events it
    // fires, and its own gesture handlers put the DOM event in there as `originalEvent` —
    // so a bearing change carrying one came from a finger and one without came from `setCamera`
    // below, which passes no bag at all.
    //
    // `dragstart` is the pan half and needs no such test: it is raised by the drag handler and
    // by nothing else — `jumpTo` does not fire it — so every one of them is a hand on the map.
    // Zoom is deliberately not in here. Scrolling in on a rider being followed is a request to
    // look closer at that rider, not a request to stop following them; the mode keeps the centre
    // and the rider keeps the zoom, which is what makes the two compose.
    const gesture = (kind) => dispatch(callbacks?.onMapGesture, "OnMapGesture", kind);

    map.on("dragstart", () => gesture("pan"));

    // Covers the compass as well as a two-finger twist, and deliberately so: MapLibre's
    // NavigationControl calls `resetNorth({}, {originalEvent})` on a click, so the button that
    // turns the map back to north arrives here as exactly what it is — the rider stating which
    // way up they want the map.
    map.on("rotatestart", (event) => {
        if (event?.originalEvent) gesture("rotate");
    });

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
            const target = {
                center: [camera.longitude, camera.latitude],
                zoom: camera.zoomLevel,
                bearing: camera.headingDeg ?? 0,
            };

            // Two behaviours behind one call, chosen by the caller — see IMapInterop.SetCameraAsync.
            //
            // No duration is C# *asserting* a view: opening on a stored camera, framing a ride,
            // putting the composer on the ride's ground (§16.1). Those are one-off statements about
            // where the map should be, and a flight to them reads as the map sliding into place.
            //
            // A duration is a camera something is *driving* — follow-me and heading-up, which
            // re-aim on every fix (§5.3). Jumping those is what made the live map lurch: a fix
            // arrives about once a second, so the ground sat still and then teleported a bike-length,
            // and a corner arrived as the world snapping round in three or four steps.
            const duration = camera.durationMs ?? 0;

            if (duration <= 0) {
                map.jumpTo(target);
                reporter.report();
                return;
            }

            // Linear, and that is the whole of why this is smooth rather than merely animated.
            // MapLibre's default easing is ease-in-out, which is right for one flight and wrong for
            // a chain of them: each fix would restart the curve, so the map would accelerate away
            // and brake to a stop once a second — the same lurch as the jump, just with the edges
            // sanded off. A constant velocity across the gap to the next fix reads as travel.
            //
            // Nothing here says `essential: true`, deliberately. MapLibre drops the duration to zero
            // on a device set to reduce motion, and somebody who has asked their phone to stop
            // animating things has not made an exception for the map they are riding behind.
            //
            // Bearing takes the short way round on its own — easeTo normalises the target against
            // the current bearing — so a rider drifting across north turns a degree, not 359.
            map.easeTo({ ...target, duration, easing: (t) => t });

            // No report here: easeTo raises movestart, which starts the frame pump, and the pump is
            // what keeps the overlay in register for every frame of the move. Reporting the target
            // view now would hand the overlay a frame the tiles have not reached yet.
        },
        fitBounds(box) {
            // The zoom that fits a box is a function of the canvas it has to fit inside, and the
            // canvas only exists here — see IMapInterop.FitBoundsAsync. This is the whole reason
            // the call crosses the bridge instead of a page computing a zoom level.
            //
            // Padding is clamped rather than trusted. MapLibre throws on padding that leaves no
            // room, and the map is a responsive element: the same 32 px that is breathing room on
            // a laptop is more than half the height of a map squeezed into a landscape phone.
            const width = map.getContainer().clientWidth || 0;
            const height = map.getContainer().clientHeight || 0;
            const room = Math.floor(Math.min(width, height) / 2) - 1;
            const padding = Math.max(0, Math.min(box.paddingPx ?? 32, room));

            map.fitBounds(
                [[box.west, box.south], [box.east, box.north]],
                {
                    padding,
                    // maxZoom binds on a box smaller than the screen — a track round a car park,
                    // or one whose points all landed on a single fix, which is a box with no
                    // extent at all and would otherwise fit at the deepest zoom the tiles have.
                    maxZoom: box.maxZoomLevel ?? 16,
                    // Not animated, for the reason setCamera does not animate either: the overlay
                    // draws the route against every viewport reported on the way, so a flight
                    // reads as the line sliding into place rather than a map opening on it.
                    animate: false,
                });
            reporter.report();
        },
        async setSource(source) {
            // Swaps what is under the map without tearing it down (§4.5). `setStyle` keeps the
            // camera, the bearing and the canvas — which is the whole point on the settings
            // screen, where this fires as the rider edits a tile URL and a map that jumped back
            // to a default view on every keystroke would be unusable.
            //
            // `diff: false` because the styles being swapped between are not variations of one
            // another: a one-layer raster style and the 68-layer vector one share nothing but
            // their intent, and where ids do coincide the contents behind them differ. MapLibre's
            // differ would try to reconcile those, which is both slower and wrong.
            //
            // Light → dark *is* a pair the differ could handle, and it is still swapped whole. The
            // tiles it re-requests are a file on this device, so the saving would be invisible, and
            // one path through here is worth more than a fast case that only one transition takes.
            if (source?.kind === "offline") {
                await ensurePmtilesProtocol(maplibregl);
            }

            currentSource = source ?? DEFAULT_SOURCE;

            const restyled = await styleFor(source);

            // A new source is a new question: the old pack's last answer must not dedupe the
            // first one about this pack. C# clears its side on the same event.
            packSourceName = packSourceNameOf(restyled);
            lastCoverageKey = null;

            map.setStyle(restyled, { diff: false });

            // The style carries the attribution, so the control re-reads it on load. Report once
            // the new tiles are in so the overlay is not left registered against a frame from a
            // style that no longer exists.
            map.once("styledata", () => reporter.report());
        },
        dispose() {
            // Drop the callbacks first. Tearing the map down emits one last moveend, and by
            // then C# has disposed the DotNetObjectReference — dispatching into it logs
            // "no tracked object with id N" for a result nobody is waiting for.
            callbacks = null;
            unregisterTracker(hostElement);
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
