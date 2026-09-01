// The overlay's presentation half (§4.5 v0.24, §16.3).
//
// SkiaMapOverlay draws every pin, route and rider label in C#. This file does two things with
// the result: get it onto the screen, and keep it over the right ground while the map moves.
//
// Why this file exists. The overlay used to be an <SKCanvasView> from SkiaSharp.Views.Blazor,
// which initialises through [JSImport] - WebAssembly-only interop. On a MAUI BlazorWebView the
// runtime is Mono, so that threw "System.Runtime.InteropServices.JavaScript is not supported on
// this platform" on first render, and an unhandled throw there takes down the whole Blazor
// renderer: the base map kept panning (it is pure JS) while every button in the app went dead.
// Skia itself runs fine on the phone - only the canvas *binding* was browser-only - so C# now
// rasterises off-screen and hands the encoded frame here.

// A <canvas> and createImageBitmap, NOT an <img> with a data: URL.
//
// The first attempt used `img.src = "data:image/png;base64,..."`. That decodes asynchronously,
// and assigning src again *cancels a decode already in flight*. On the web, where decoding a
// frame is far quicker than the gap between repaints, it looked fine. On a phone it did not:
// Group Ride Live repaints about once a second as rider positions arrive, each assignment
// killed the previous decode, and the overlay never displayed anything at all - while a static
// page like the track editor, which paints once and then stops, eventually got a frame through.

import { findTracker } from "./interop.js";

// Per-canvas tracking state: what the frame on screen was drawn for, and the subscription that
// keeps it over the right ground. Keyed on the canvas so two maps on one page cannot collide.
const tracked = new WeakMap();

/**
 * Decode and present a rasterised overlay frame, and follow the map with it from here.
 *
 * @param {HTMLCanvasElement} element The canvas the component owns.
 * @param {string} pngBase64 The frame, PNG-encoded. Empty clears the overlay.
 * @param {{widthPx: number, heightPx: number, centreLat: number, centreLon: number, zoom: number, bearingDeg: number}} view
 *   The base map's view when the frame was drawn - its size in device pixels, and the centre,
 *   zoom and bearing that every later frame's transform is measured against.
 */
export async function present(element, pngBase64, view) {
	if (!element) return;

	const { widthPx, heightPx } = view;

	const context = element.getContext("2d");
	if (!context) throw new Error("map/overlay.js: could not get a 2d context for the overlay canvas.");

	// Assigning width or height also clears the canvas, so only touch them when they actually
	// changed - otherwise every frame would blank the surface before drawing the new one and a
	// slow decode would show as a flicker.
	if (element.width !== widthPx || element.height !== heightPx) {
		element.width = widthPx;
		element.height = heightPx;
	}

	if (!pngBase64) {
		context.clearRect(0, 0, element.width, element.height);
		return;
	}

	const bitmap = await createImageBitmap(base64ToBlob(pngBase64));
	try {
		// Clear and draw in the same synchronous block: the overlay is transparent almost
		// everywhere, so drawing over the previous frame without clearing would leave every pin
		// the map has ever shown smeared across the canvas.
		context.clearRect(0, 0, element.width, element.height);
		context.drawImage(bitmap, 0, 0);
	} finally {
		// ImageBitmap holds a decoded buffer the GC does not account for. One live-ride frame a
		// second, undisposed, is a leak measured in megabytes per minute.
		bitmap.close();
	}

	track(element, view);
}

/** Stop following the map and drop the subscription. Called from the component's dispose. */
export function detach(element) {
	const state = element && tracked.get(element);
	if (!state) return;
	state.tracker.offMove(state.apply);
	tracked.delete(element);
	element.style.transform = "";
}

// Point the canvas at the frame just drawn, and follow the map with it from here.
//
// One subscription per canvas, held for its lifetime. A new frame changes only the reference
// view, so that is mutated in place rather than the subscription being torn down and rebuilt:
// a live ride presents a frame a second, and an off/on pair each time is churn on MapLibre's
// listener list for no change in behaviour.
function track(element, view) {
	const existing = tracked.get(element);
	if (existing) {
		existing.view = view;
		existing.apply();
		return;
	}

	const tracker = findTracker(baseMapElementFor(element));
	if (!tracker) {
		// No base map published a projection. Correct behaviour is no tracking at all rather
		// than a guess: an overlay that sits still is stale, one that moves wrongly is a lie.
		element.style.transform = "";
		return;
	}

	const state = { tracker, view };
	state.apply = () => applyTransform(element, state.view, tracker);
	tracker.onMove(state.apply);
	tracked.set(element, state);

	// Immediately, not on the next move. C# spent tens of milliseconds rasterising this frame
	// and the map has kept moving the whole time, so presenting it untransformed would snap the
	// overlay back to where the map was when the paint started - a visible twitch on every
	// frame of a pan, which is worse than the lag it is meant to cure.
	state.apply();
}

// Move the drawn pixels to where their ground currently is.
//
// The frame was rasterised in screen space for `painted`, so its centre pixel is the canvas
// centre and everything else follows from a similarity transform:
//
//   scale    2^(zoom now − zoom painted)
//   rotate   bearing painted − bearing now, because turning the map turns the world the other way
//   translate wherever the painted centre's ground point has moved to
//
// Every one of those numbers comes from the base map itself rather than from arithmetic here.
// The previous attempt re-derived the projection from the reported viewport and drifted; asking
// the library cannot, because it is the same projection that drew the tiles underneath.
function applyTransform(element, view, tracker) {
	const now = tracker.projectCss(view.centreLat, view.centreLon);
	if (!now || !Number.isFinite(now.x) || !Number.isFinite(now.y)) return;

	// CSS pixels throughout: the canvas is laid out at the container's CSS size, whatever its
	// backing store measures in device pixels. Derived from the frame rather than read off the
	// element, because reading clientWidth inside a move handler forces a layout on every frame
	// of every pan - and the two cannot disagree, because a resize changes the reported viewport
	// and therefore produces a new frame.
	const ratio = window.devicePixelRatio || 1;
	const dx = now.x - (view.widthPx / ratio / 2);
	const dy = now.y - (view.heightPx / ratio / 2);
	const scale = Math.pow(2, tracker.zoom() - view.zoom);
	const rotate = view.bearingDeg - tracker.bearing();

	// transform-origin is the centre (the CSS default, and relied on here), so rotate and scale
	// pivot on the same point the translation is measured from.
	element.style.transform =
		`translate(${dx}px, ${dy}px) rotate(${rotate}deg) scale(${scale})`;
}

// The base map's host element is the overlay's sibling inside the shared map container. Found
// by structure rather than by an id, because a page can host several maps.
function baseMapElementFor(element) {
	const container = element.closest(".dlr-map-container");
	return container ? container.querySelector(".dlr-map-base") : null;
}

function base64ToBlob(base64) {
	const bytes = Uint8Array.from(atob(base64), (character) => character.charCodeAt(0));
	return new Blob([bytes], { type: "image/png" });
}
