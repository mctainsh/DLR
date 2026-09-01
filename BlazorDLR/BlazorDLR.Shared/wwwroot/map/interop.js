// The JS half of the base-map callback contract (§4.5 v0.24).
//
// The base-map module (map.maplibre.js) reports back to C# through this one function, so the
// method names below pair with exactly one place on the other side: MapBridge's [JSInvokable]
// methods in BlazorDLR.Shared/Services.
//
// Kept as its own file after v0.24 collapsed three modules into one, because the rule below
// is about Blazor's marshalling and not about any provider - a second module (an offline
// PMTiles renderer, §13 Q26) would need it stated exactly once, here.
//
// That pairing has already been got wrong once. Blazor marshals a DotNetObjectReference
// across as an object carrying `invokeMethodAsync` - NOT as a callable function - so
// `callbacks.onViewportChanged(payload)` throws "is not a function" inside an event handler
// and the event vanishes with no visible error. The map drew, and every viewport and click
// it reported was silently discarded. One copy of this rule is the point of the file.

// -- Base-map trackers -----------------------------------------------------------------------
//
// How the overlay follows the map between repaints, and the reason a pan no longer drags it.
//
// A repaint is a round trip: MapLibre moves, JS tells C#, C# re-renders, rasterises, encodes and
// marshals a frame back. That is on the order of a hundred milliseconds, so during a continuous
// pan the overlay is always showing where the map *was*. Redrawing faster cannot fix it - the
// lag is the round trip, not the drawing.
//
// So the pixels already on screen are moved to follow the map, in JS, on the map's own frame.
// The base-map module registers how to ask it where a ground point currently sits; the overlay
// looks that up and transforms itself. C# is not involved and cannot be the bottleneck.
//
// Keyed on the host element the base map attached to, because that is the one object both sides
// can name without either importing the other - the overlay finds it by walking up to the
// shared container. A WeakMap so a disposed map does not pin its element.
const trackers = new WeakMap();

/**
 * Publish a base map's live projection. Called by the base-map module on create.
 * @param {Element} hostElement The element the base map attached to.
 * @param {{projectCss: Function, zoom: Function, bearing: Function, onMove: Function, offMove: Function}} tracker
 */
export function registerTracker(hostElement, tracker) {
	if (hostElement) trackers.set(hostElement, tracker);
}

/** Withdraw it again on dispose, so a stale map cannot be asked where anything is. */
export function unregisterTracker(hostElement) {
	if (hostElement) trackers.delete(hostElement);
}

/** The tracker for a base map, or null when none has registered - tracking is optional. */
export function findTracker(hostElement) {
	return (hostElement && trackers.get(hostElement)) || null;
}

/**
 * Forward a payload to a .NET callback.
 * @param {object|Function|null|undefined} target A DotNetObjectReference, or a plain
 *   function for a test harness or a future non-Blazor host.
 * @param {string} method The [JSInvokable] method name on MapBridge.
 * @param {object} payload The argument, serialised by Blazor.
 */
export function dispatch(target, method, payload) {
    if (!target) return;
    if (typeof target === "function") { target(payload); return; }
    target.invokeMethodAsync(method, payload);
}

// How many consecutive still frames end a tracking run on their own.
//
// The module stops its own pump on its settle event, so this is the backstop for the one
// that never arrives - a `moveend` lost because the tab was hidden mid-drag, a `rotateend`
// swallowed by a cancelled gesture. Without it that run samples the view forever.
//
// Ten seconds at 60 Hz, not half a second, and the difference is a bug: a finger resting
// mid-drag is still a drag, and nothing would restart the pump until the gesture ended -
// the overlay would freeze for the rest of a pan the user paused. The cost of erring long
// is one bounds calculation per frame, dispatching nothing.
const StillFramesBeforeStop = 600;

/**
 * The viewport half of the base-map contract: builds a payload, drops it when nothing
 * moved, and can sample once per animation frame while the map is in motion.
 *
 * The overlay has to repaint *during* a pan, not after it. Reporting only on the settle
 * event leaves every marker and track frozen while the tiles slide underneath - so the
 * module reports continuously, from the frame pump below, which runs between the base
 * map's move-start and move-end events.
 *
 * Sampling per frame is why the dedupe matters: a map that is being rendered but not moved
 * - tiles fading in, a finger held still mid-drag - would otherwise cross the interop
 * boundary sixty times a second to say nothing changed.
 *
 * @param {Function} readViewport Builds the payload, or returns null when the map cannot
 *   answer yet. Must return the same keys every time.
 * @param {Function} readTarget Returns the current .NET callback. Read per report rather
 *   than captured, because a module drops its callbacks on dispose and a frame already
 *   scheduled must not dispatch into a disposed DotNetObjectReference.
 * @returns {{report: Function, startTracking: Function, stopTracking: Function, dispose: Function}}
 */
export function createViewportReporter(readViewport, readTarget) {
    let last = null;
    let frame = 0;
    let stillFrames = 0;

    const report = () => {
        const viewport = readViewport();
        if (!viewport) return false;
        if (last && unchanged(last, viewport)) return false;
        last = viewport;
        dispatch(readTarget(), "OnViewportChanged", viewport);
        return true;
    };

    const pump = () => {
        frame = requestAnimationFrame(pump);
        stillFrames = report() ? 0 : stillFrames + 1;
        if (stillFrames >= StillFramesBeforeStop) {
            stopTracking();
        }
    };

    const stopTracking = () => {
        if (frame) {
            cancelAnimationFrame(frame);
            frame = 0;
        }
        stillFrames = 0;
        report();
    };

    return {
        /** Sample and dispatch now, unless the view is exactly where it was. */
        report,

        /** Begin sampling once per frame. Idempotent - a second gesture does not stack pumps. */
        startTracking() {
            if (frame) return;
            stillFrames = 0;
            frame = requestAnimationFrame(pump);
        },

        /** Stop sampling and report the settled view. */
        stopTracking,

        /** Stop sampling without reporting. For teardown, once the callbacks are gone. */
        dispose() {
            if (frame) {
                cancelAnimationFrame(frame);
                frame = 0;
            }
        },
    };
}

// Both payloads come from the same builder, so they carry the same keys and a one-sided
// walk is a complete comparison.
function unchanged(a, b) {
    for (const key in a) {
        if (a[key] !== b[key]) return false;
    }
    return true;
}
