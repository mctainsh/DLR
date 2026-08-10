// The overlay's presentation half (§4.5 v0.24, §16.3).
//
// SkiaMapOverlay draws every pin, route and rider label in C#. This file does one thing with
// the result: get it onto the screen.
//
// Why this file exists. The overlay used to be an <SKCanvasView> from SkiaSharp.Views.Blazor,
// which initialises through [JSImport] — WebAssembly-only interop. On a MAUI BlazorWebView the
// runtime is Mono, so that threw "System.Runtime.InteropServices.JavaScript is not supported on
// this platform" on first render, and an unhandled throw there takes down the whole Blazor
// renderer: the base map kept panning (it is pure JS) while every button in the app went dead.
// Skia itself runs fine on the phone — only the canvas *binding* was browser-only — so C# now
// rasterises off-screen and hands the encoded frame here.

// A <canvas> and createImageBitmap, NOT an <img> with a data: URL.
//
// The first attempt used `img.src = "data:image/png;base64,..."`. That decodes asynchronously,
// and assigning src again *cancels a decode already in flight*. On the web, where decoding a
// frame is far quicker than the gap between repaints, it looked fine. On a phone it did not:
// Group Ride Live repaints about once a second as rider positions arrive, each assignment
// killed the previous decode, and the overlay never displayed anything at all — while a static
// page like the track editor, which paints once and then stops, eventually got a frame through.
// "No pins on the live map, but the track sometimes shows up in the editor" was that bug.
//
// createImageBitmap decodes off the main thread and hands back a finished bitmap. Drawing it is
// synchronous, so a frame either replaces the previous one completely or does not appear at
// all — no partial states, and no cancellation to lose.

/**
 * Decode and present a rasterised overlay frame.
 *
 * @param {HTMLCanvasElement} element The canvas the component owns.
 * @param {string} pngBase64 The frame, PNG-encoded. Empty clears the overlay.
 * @param {number} widthPx Frame width in device pixels.
 * @param {number} heightPx Frame height in device pixels.
 */
export async function present(element, pngBase64, widthPx, heightPx) {
	if (!element) return;

	const context = element.getContext("2d");
	if (!context) throw new Error("map/overlay.js: could not get a 2d context for the overlay canvas.");

	// Assigning width or height also clears the canvas, so only touch them when they actually
	// changed — otherwise every frame would blank the surface before drawing the new one and a
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
}

function base64ToBlob(base64) {
	const binary = atob(base64);
	const bytes = new Uint8Array(binary.length);
	for (let i = 0; i < binary.length; i++) {
		bytes[i] = binary.charCodeAt(i);
	}
	return new Blob([bytes], { type: "image/png" });
}
