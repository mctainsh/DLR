// Colour-emoji rasteriser for the Skia map overlay (§4.5 v0.21, §16.3).
//
// Why this exists: SkiaSharp's WebAssembly build ships one embedded Latin typeface and no
// system font manager, so `SKFontManager.MatchCharacter` finds nothing for an emoji
// codepoint and the overlay can only draw a plain pin. Every host we ship — browser, iOS
// WKWebView, Android WebView — already has a colour emoji font that the 2D canvas can use.
//
// So the browser draws each glyph once into an offscreen canvas and hands back the raw RGBA
// buffer, which the C# side wraps as an SKImage. Raw pixels rather than a PNG so that no
// image decoder runs outside the photo-ingest path. That keeps §4.5's rule intact: one Skia
// canvas still
// draws every authored map element, and there is exactly one drawing path across the three
// hosts. The alternative — DOM pins over the map — would split marker rendering back into
// per-host code, which is the drift v0.13 warned about.
//
// Cost is one rasterise per distinct icon per session (21 curated keys), not per frame.

// Deliberately ordered: the platform emoji font first, then a generic fallback so a host
// without one still produces *something* rather than an empty bitmap.
const EMOJI_STACK = '"Apple Color Emoji","Segoe UI Emoji","Noto Color Emoji","Twemoji Mozilla",sans-serif';

/**
 * Rasterise one emoji to a square RGBA buffer.
 *
 * Raw pixels rather than a PNG on purpose: the C# side would otherwise have to run an image
 * decoder, and decoding is deliberately confined to the photo-ingest path. It also skips an
 * encode/decode round trip we would immediately undo.
 *
 * @param {string} text The emoji, including any variation selector.
 * @param {number} sizePx Bitmap edge length. Rendered above display size so the overlay
 *   scales down for any devicePixelRatio rather than up.
 * @returns {Uint8Array} Unpremultiplied RGBA, sizePx × sizePx × 4 bytes. Empty if the host
 *   cannot draw, which the caller treats as "no glyph".
 */
export function renderPixels(text, sizePx) {
    if (typeof document === "undefined" || !text) return new Uint8Array(0);

    const size = Math.max(8, Math.min(256, sizePx | 0));
    const canvas = document.createElement("canvas");
    canvas.width = size;
    canvas.height = size;

    const ctx = canvas.getContext("2d", { willReadFrequently: true });
    if (!ctx) return new Uint8Array(0);

    // 0.82 leaves room for glyphs whose ink overflows the em box — several emoji do, and a
    // clipped edge is far more obvious than a slightly small icon.
    ctx.font = `${Math.round(size * 0.82)}px ${EMOJI_STACK}`;
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillText(text, size / 2, size / 2);

    // getImageData is unpremultiplied RGBA, which is what the overlay declares on its side.
    return new Uint8Array(ctx.getImageData(0, 0, size, size).data.buffer);
}
