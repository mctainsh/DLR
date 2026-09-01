using BlazorDLR.Shared.Diagnostics;

using Microsoft.JSInterop;

namespace BlazorDLR.Shared.Markers;

/// <summary>
/// Marker-icon pixels for the Skia map overlay, rasterised once by the host and kept for the
/// life of the app (§16.3).
/// <para>
/// The icons ship as PNGs (<c>wwwroot/markers/</c>) and SkiaSharp cannot open one here:
/// decoding is confined to the photo-ingest path (§16.4, <c>ImageRules</c>). <c>map/markers.js</c>
/// decodes each icon through the host's own 2D canvas and the overlay draws the result. §4.5's
/// "one Skia canvas draws every authored map element" holds: the overlay still does all the
/// drawing, it just sources its bitmaps from the platform decoder instead of one Skia is not
/// allowed to call.
/// </para>
/// <para>
/// <strong>Raw RGBA, never an encoded image.</strong> The JS side hands back the canvas' pixel
/// buffer, so nothing on this side runs an image decoder - that stays the sole business of
/// <c>BlazorDLR.Web/Photos/</c>, which <c>ImageRules</c> enforces.
/// </para>
/// <para>
/// Painting is synchronous, so nothing here blocks: <see cref="TryGetPixels"/> answers from
/// the cache only, and <see cref="PrimeAsync"/> is what fills it.
/// </para>
/// </summary>
public static class MarkerIconCache
{
	/// <summary>
	/// Bitmap edge in pixels, matching the artwork's native 48×48. Rasterising larger would
	/// resample twice - once here and again as the overlay scales to the display - for detail
	/// the source PNG does not contain.
	/// </summary>
	public const int RasterSize = 48;

	private const string ModulePath = "./_content/BlazorDLR.Shared/map/markers.js";

	/// <summary>
	/// Icon key to its RGBA pixels, or null for "this host cannot draw it" - a negative entry
	/// so a hopeless icon is asked for once rather than once per repaint.
	/// </summary>
	private static readonly Dictionary<string, byte[]?> Pixels = new(StringComparer.Ordinal);

	private static readonly SemaphoreSlim Gate = new(1, 1);

	private static IJSObjectReference? _module;

	/// <summary>Set once the host has proved it cannot rasterise, so we stop asking.</summary>
	private static bool _unavailable;

	/// <summary>The cached RGBA buffer for an icon key, or null when it is not available.</summary>
	/// <param name="iconKey">The icon key (§16.2).</param>
	/// <returns>An unpremultiplied RGBA buffer of <see cref="RasterSize"/> squared, or null.</returns>
	public static byte[]? TryGetPixels(string iconKey) =>
		Pixels.TryGetValue(iconKey, out byte[]? pixels) ? pixels : null;

	/// <summary>Rasterise any of the supplied icon keys not cached yet.</summary>
	/// <param name="iconKeys">The keys the caller is about to draw.</param>
	/// <param name="js">The host's JS runtime.</param>
	/// <returns>True when at least one new icon landed, so the caller should repaint.</returns>
	public static async ValueTask<bool> PrimeAsync(IEnumerable<string> iconKeys, IJSRuntime js)
	{
		if (_unavailable)
		{
			return false;
		}

		List<string> wanted = iconKeys
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
			if (await ModuleAsync(js) is not { } module)
			{
				return false;
			}

			bool added = false;

			foreach (string key in wanted)
			{
				// Re-check under the gate: a concurrent prime may have filled it already.
				if (Pixels.ContainsKey(key))
				{
					continue;
				}

				// AssetPath, not string concatenation: an unrecognised key resolves to the note
				// icon here rather than fetching a URL that does not exist.
				byte[]? rgba;

				try
				{
					rgba = await module.InvokeAsync<byte[]?>(
						"renderPixels", MarkerIconGlyphs.AssetPath(key), RasterSize);
				}
				catch (Exception exception) when (IsTeardown(exception))
				{
					// The rider left the map mid-rasterise. Nothing is cached - the next map asks
					// again - and nothing is reported: this is a normal way for a page to end.
					return added;
				}
				catch (Exception exception)
				{
					// One icon that will not draw is one plain pin, not a map without markers.
					// Cached negatively so it is asked for once, logged once for the same reason.
					DiagnosticLog.WriteError($"rasterising the marker icon '{key}'", exception);
					Pixels[key] = null;
					continue;
				}

				// Pattern-matched rather than `rgba.Length`, which is what this was and what threw.
				// The module answers an empty buffer for an icon it cannot draw - but a JS function
				// that returns undefined, or one a stale cached module never had, deserialises as
				// null, and the null dereference here left the whole overlay unmounted behind "Map
				// markers unavailable" over a missing pin.
				Pixels[key] = rgba is { Length: RasterSize * RasterSize * 4 } ? rgba : null;
				added = true;
			}

			return added;
		}
		finally
		{
			Gate.Release();
		}
	}

	/// <summary>
	/// The rasteriser module, or null when this host is not going to produce one.
	/// <para>
	/// Separated from the per-icon loop because the two failures are not the same size: no module
	/// means no icons at all and is worth latching, while one icon that will not draw is one plain
	/// pin. Latching on the second was how a single bad key could have turned every marker on the
	/// device into a dot for the rest of the session.
	/// </para>
	/// </summary>
	/// <param name="js">The host's JS runtime.</param>
	/// <returns>The module, or null.</returns>
	private static async ValueTask<IJSObjectReference?> ModuleAsync(IJSRuntime js)
	{
		if (_module is not null)
		{
			return _module;
		}

		try
		{
			return _module = await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
		}
		catch (InvalidOperationException)
		{
			// Prerender: JS interop is unavailable during SSR. The interactive pass primes.
			return null;
		}
		catch (Exception exception) when (IsTeardown(exception))
		{
			// The WebView went away while the module was importing - the rider left the page.
			// Not latched: the next map is a new WebView and can import perfectly well.
			return null;
		}
		catch (Exception exception)
		{
			// No rasteriser module, no icons. The overlay's plain-pin fallback covers it - a map
			// that draws pins beats a map that throws. Latched: without this the import is retried
			// on every render, which on a live ride is a thrown-and-caught interop exception per
			// second for the rest of the session.
			//
			// Logged, because "every marker is a plain dot" is a thing a rider notices and cannot
			// otherwise explain, and the reason is only ever in this exception.
			DiagnosticLog.WriteError("loading the marker rasteriser", exception);
			_unavailable = true;
			return null;
		}
	}

	/// <summary>
	/// Whether an exception is the page going away rather than something being wrong.
	/// <para>
	/// A prime runs from the overlay's <c>OnAfterRenderAsync</c>, so anything escaping it unmounts
	/// the overlay through <c>RideMap</c>'s error boundary - and the commonest exception here is
	/// simply a rider leaving the map while an icon is in flight. Both of these were guarded in
	/// <c>SkiaMapOverlay.RepaintAsync</c> from the start and in neither of the paths that reach
	/// this file.
	/// </para>
	/// </summary>
	private static bool IsTeardown(Exception exception) =>
		exception is JSDisconnectedException or ObjectDisposedException or OperationCanceledException;
}
