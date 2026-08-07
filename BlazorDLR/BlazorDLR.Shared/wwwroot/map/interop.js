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
