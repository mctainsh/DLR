using Microsoft.JSInterop;

namespace BlazorDLR.Shared.Markers;

/// <summary>
/// Colour-emoji pixels for the Skia map overlay, rasterised once by the host and kept for
/// the life of the app (§16.3).
/// <para>
/// SkiaSharp's WebAssembly build has no emoji font, so <c>DrawText</c> on an emoji draws
/// nothing. <c>map/emoji.js</c> rasterises each glyph through the host's own 2D canvas —
/// every host we ship is a browser or a WebView and already has colour emoji — and the
/// overlay draws the result. §4.5's "one Skia canvas draws every authored map element"
/// holds: the overlay still does all the drawing, it just sources its glyphs from the
/// platform instead of from a font Skia does not have.
/// </para>
/// <para>
/// <strong>Raw RGBA, never an encoded image.</strong> The JS side hands back the canvas'
/// pixel buffer rather than a PNG, so nothing here runs an image decoder — that stays the
/// sole business of <c>BlazorDLR.Web/Photos/</c>, which <c>ImageRules</c> enforces. It is
/// also simply cheaper: no encode on one side and decode on the other.
/// </para>
/// <para>
/// Painting is synchronous, so nothing here blocks: <see cref="TryGetPixels"/> answers from
/// the cache only, and <see cref="PrimeAsync"/> is what fills it.
/// </para>
/// </summary>
public static class MarkerGlyphCache
{
	/// <summary>
	/// Bitmap edge in pixels. Above the ~20 px the overlay draws at even on a 3× display, so
	/// glyphs are always scaled down rather than up.
	/// </summary>
	public const int RasterSize = 64;

	private const string ModulePath = "./_content/BlazorDLR.Shared/map/emoji.js";

	/// <summary>
	/// Emoji to its RGBA pixels, or null for "this host cannot draw it" — a negative entry so
	/// a hopeless glyph is asked for once rather than once per repaint.
	/// </summary>
	private static readonly Dictionary<string, byte[]?> Pixels = new(StringComparer.Ordinal);

	private static readonly SemaphoreSlim Gate = new(1, 1);

	private static IJSObjectReference? _module;

	/// <summary>Set once the host has proved it cannot rasterise, so we stop asking.</summary>
	private static bool _unavailable;

	/// <summary>The cached RGBA buffer for an emoji, or null when it is not available.</summary>
	/// <param name="emoji">The emoji string.</param>
	/// <returns>An unpremultiplied RGBA buffer of <see cref="RasterSize"/> squared, or null.</returns>
	public static byte[]? TryGetPixels(string emoji) =>
		Pixels.TryGetValue(emoji, out byte[]? pixels) ? pixels : null;

	/// <summary>Rasterise any of the supplied emoji not cached yet.</summary>
	/// <param name="emoji">The glyphs the caller is about to draw.</param>
	/// <param name="js">The host's JS runtime.</param>
	/// <returns>True when at least one new glyph landed, so the caller should repaint.</returns>
	public static async ValueTask<bool> PrimeAsync(IEnumerable<string> emoji, IJSRuntime js)
	{
		if (_unavailable)
		{
			return false;
		}

		List<string> wanted = emoji
			.Where(candidate => !string.IsNullOrEmpty(candidate) && !Pixels.ContainsKey(candidate))
			.Distinct(StringComparer.Ordinal)
			.ToList();

		if (wanted.Count == 0)
		{
			return false;
		}

		await Gate.WaitAsync();
		try
		{
			bool added = false;
			_module ??= await js.InvokeAsync<IJSObjectReference>("import", ModulePath);

			foreach (string glyph in wanted)
			{
				// Re-check under the gate: a concurrent prime may have filled it already.
				if (Pixels.ContainsKey(glyph))
				{
					continue;
				}

				byte[] rgba = await _module.InvokeAsync<byte[]>("renderPixels", glyph, RasterSize);
				Pixels[glyph] = rgba.Length == RasterSize * RasterSize * 4 ? rgba : null;
				added = true;
			}

			return added;
		}
		catch (JSException)
		{
			// No emoji module, no emoji. The overlay's plain-pin fallback covers it — a map
			// that draws pins beats a map that throws. Latched: without this the module
			// import is retried on every render, which on a live ride is a thrown-and-caught
			// interop exception per second for the rest of the session.
			_unavailable = true;
			return false;
		}
		catch (InvalidOperationException)
		{
			// Prerender: JS interop is unavailable during SSR. The interactive pass primes.
			return false;
		}
		finally
		{
			Gate.Release();
		}
	}
}
