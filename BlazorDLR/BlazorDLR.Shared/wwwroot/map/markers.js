// Marker-icon rasteriser for the Skia map overlay (§4.5 v0.21, §16.3).
//
// The overlay draws onto an SKCanvas, and SkiaSharp cannot open a PNG here: decoding is
// confined to the photo-ingest path (§16.4, ImageRules), so nothing in BlazorDLR.Shared may
// touch SKCodec. The browser already has an image decoder, so it decodes each icon once into
// an offscreen canvas and hands back the raw RGBA buffer, which the C# side wraps as an
// SKImage with SKImage.FromPixelCopy — a pixel copy, not a decode.
//
// That keeps §4.5's rule intact: one Skia canvas still draws every authored map element, and
// there is exactly one drawing path across the three hosts. The alternative — DOM pins over
// the map — would split marker rendering back into per-host code, which is the drift v0.13
// warned about.
//
// The icons are our own artwork, so the same marker is the same picture on iOS, Android and
// the web rather than three platforms' interpretations of it.
//
// Cost is one fetch and decode per distinct icon per session, not per frame.

/**
 * Rasterise one marker icon to a square RGBA buffer.
 *
 * @param {string} url Host-relative URL of the icon PNG.
 * @param {number} sizePx Bitmap edge length. Pass the artwork's native size — upsampling here
 *   only costs memory, since the overlay scales to the display anyway.
 * @returns {Promise<Uint8Array>} Unpremultiplied RGBA, sizePx × sizePx × 4 bytes. Empty if the
 *   host cannot draw or the icon will not load, which the caller treats as "no glyph".
 */
export async function renderPixels(url, sizePx) {
    if (typeof document === "undefined" || !url) return new Uint8Array(0);

    const size = Math.max(8, Math.min(256, sizePx | 0));

    let image;
    try {
        image = await load(url);
    } catch {
        // A missing icon is a caller bug, not a reason to take the map down. The overlay's
        // plain-pin fallback covers it and the negative cache stops us retrying every frame.
        return new Uint8Array(0);
    }

    const canvas = document.createElement("canvas");
    canvas.width = size;
    canvas.height = size;

    const ctx = canvas.getContext("2d", { willReadFrequently: true });
    if (!ctx) return new Uint8Array(0);

    ctx.drawImage(image, 0, 0, size, size);

    // getImageData is unpremultiplied RGBA, which is what the overlay declares on its side.
    // Same-origin throughout — the icons ship inside the app — so the canvas is never tainted
    // and this never throws a SecurityError.
    return new Uint8Array(ctx.getImageData(0, 0, size, size).data.buffer);
}

/**
 * Load an image element, resolved when it is safe to draw.
 *
 * An onload/onerror pair rather than img.decode(): decode() is the tidier API but has been
 * flaky in older WKWebView builds, and this runs inside the MAUI WebView on whatever iOS the
 * rider is on.
 *
 * @param {string} url Image URL.
 * @returns {Promise<HTMLImageElement>} The loaded image.
 */
function load(url) {
    return new Promise((resolve, reject) => {
        const image = new Image();
        image.onload = () => resolve(image);
        image.onerror = () => reject(new Error(`marker icon failed to load: ${url}`));
        image.src = url;
    });
}
