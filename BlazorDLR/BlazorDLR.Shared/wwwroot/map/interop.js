// The JS half of the base-map callback contract (§4.5 v0.21).
//
// Every provider module (map.openlayers.js, map.mapkit.js, map.googlemaps.js) reports back
// to C# through this one function, so the method names below pair with exactly one place on
// the other side: MapBridge's [JSInvokable] methods in BlazorDLR.Shared/Services.
//
// That pairing has already been got wrong once. Blazor marshals a DotNetObjectReference
// across as an object carrying `invokeMethodAsync` — NOT as a callable function — so
// `callbacks.onViewportChanged(payload)` throws "is not a function" inside an event handler
// and the event vanishes with no visible error. The map drew, and every viewport and click
// it reported was silently discarded. One copy of this rule is the point of the file.

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
// Every provider stops its own pump on its settle event, so this is the backstop for the
// one that never arrives — an `idle` lost because the tab was hidden mid-drag, a
// `region-change-end` swallowed by a cancelled gesture. Without it that run samples the
// view forever.
//
// Ten seconds at 60 Hz, not half a second, and the difference is a bug: a finger resting
// mid-drag is still a drag, and on MapKit nothing would restart the pump until the gesture
// ended — the overlay would freeze again for the rest of a pan the user paused. The cost of
// erring long is one extent calculation per frame, dispatching nothing.
const StillFramesBeforeStop = 600;

/**
 * The viewport half of the base-map contract: builds a payload, drops it when nothing
 * moved, and can sample once per animation frame while the map is in motion.
 *
 * The overlay has to repaint *during* a pan, not after it. A provider that only reports on
 * its settle event leaves every marker and track frozen while the tiles slide underneath —
 * so each module reports continuously, either from a per-frame hook of its own
 * (OpenLayers' `postrender`) or from the frame pump below (MapKit, Google Maps), which
 * runs between the provider's move-start and move-end events.
 *
 * Sampling per frame is why the dedupe matters: a map that is being rendered but not moved
 * — tiles fading in, a finger held still mid-drag — would otherwise cross the interop
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

        /** Begin sampling once per frame. Idempotent — a second gesture does not stack pumps. */
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
