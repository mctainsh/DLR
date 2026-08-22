using BlazorDLR.Shared.Services;
using DLR.Core.Tracks;
using Microsoft.AspNetCore.Components;

namespace DLR.UI.Tests.Fakes;

/// <summary>
/// A hand-rolled <see cref="IMapInterop"/> that lets bUnit render <c>RideMap</c> without
/// loading a JS SDK (§4.5 v0.24). Two levers:
/// <list type="bullet">
///   <item><c>Provider</c> — what the fake reports. One value since v0.24, kept settable
///     because the property is on the interface and a future offline renderer would add
///     another (§13 Q26).</item>
///   <item><c>InitException</c> — the exception <see cref="InitAsync"/> should throw, if any.
///     Set this to reproduce the CDN-unreachable and tile-server-unreachable branches, which
///     surface identically to <c>RideMap</c>.</item>
/// </list>
/// </summary>
public sealed class FakeMapInterop : IMapInterop
{
	public MapProvider Provider { get; set; } = MapProvider.MapLibreOsm;

	/// <summary>If set, <see cref="InitAsync"/> throws this exception instead of announcing a viewport.</summary>
	public Exception? InitException { get; set; }

	/// <summary>The initial viewport emitted after <see cref="InitAsync"/> succeeds.</summary>
	public MapViewport InitialViewport { get; set; } = new(
		TopLeftLatitude: 0.01, TopLeftLongitude: -0.01,
		BottomRightLatitude: -0.01, BottomRightLongitude: 0.01,
		ZoomLevel: 12,
		HeadingDeg: 0,
		CanvasWidthPx: 800, CanvasHeightPx: 600,
		DevicePixelRatio: 1);

	public int InitCount { get; private set; }
	public int DisposeCount { get; private set; }

	/// <summary>
	/// The options the last <see cref="InitAsync"/> was given — recorded even when
	/// <see cref="InitException"/> makes the call throw, because the camera a map <em>opens</em>
	/// on is decided before the base map has a chance to fail.
	/// </summary>
	public MapOptions? LastOptions { get; private set; }

	public event Action<MapViewport>? ViewportChanged;

	public event Action<MapClick>? Clicked;

	/// <summary>The base map complaining. Raised by <see cref="RaiseError"/>, never on its own.</summary>
	public event Action<string>? ErrorOccurred;

	/// <summary>The rider moving the map by hand. Raised by <see cref="RaiseGesture"/>.</summary>
	public event Action<MapGesture>? Gestured;

	/// <summary>
	/// Stands in for the rider dragging or turning the base map. The real module tells these apart
	/// from its own camera moves by MapLibre's <c>originalEvent</c>; a bUnit test has no MapLibre,
	/// so it says which gesture happened directly.
	/// </summary>
	/// <param name="gesture">What the rider did.</param>
	public void RaiseGesture(MapGesture gesture) => Gestured?.Invoke(gesture);

	/// <summary>
	/// Stands in for MapLibre reporting a problem it did not throw for — a tile source it cannot
	/// reach, a style it cannot parse. The real module raises this from a JS event.
	/// </summary>
	public void RaiseError(string message) => ErrorOccurred?.Invoke(message);

	/// <summary>
	/// Stands in for the user tapping the base map. The real modules raise this from a JS
	/// SDK event; a bUnit test has no SDK, so it calls this directly.
	/// </summary>
	public void RaiseClick(double latitudeDeg, double longitudeDeg) =>
		Clicked?.Invoke(new MapClick(latitudeDeg, longitudeDeg));

	/// <summary>
	/// Stands in for the base map reporting a view. Separate from <see cref="InitAsync"/> so a
	/// test can hand a page a viewport while still using <see cref="InitException"/> to keep
	/// <c>SkiaMapOverlay</c> — whose <c>SKCanvasView</c> is browser-only — unmounted.
	/// </summary>
	public void RaiseViewport(MapViewport viewport) => ViewportChanged?.Invoke(viewport);

	/// <summary>Every camera passed to <see cref="SetCameraAsync"/>, in order.</summary>
	public List<MapCamera> Cameras { get; } = new();

	/// <summary>
	/// Every box passed to <see cref="FitBoundsAsync"/>, in order — the frames a map was asked to
	/// fit itself to. Real <c>fitBounds</c> resolves a zoom against the canvas, which a bUnit test
	/// has none of, so what is asserted here is the request rather than the view it produced.
	/// </summary>
	public List<TrackBounds> Fits { get; } = new();

	/// <summary>
	/// Every source passed to <see cref="SetSourceAsync"/>, in order — the restyles a map performs
	/// while it is on screen (§4.5). The one <see cref="InitAsync"/> opened with is not in here;
	/// that is <c>LastOptions.EffectiveSource</c>.
	/// </summary>
	public List<MapSource> Sources { get; } = new();

	public ValueTask InitAsync(ElementReference host, MapOptions options, CancellationToken cancellationToken = default)
	{
		InitCount++;
		LastOptions = options;
		if (InitException is not null)
		{
			throw InitException;
		}
		ViewportChanged?.Invoke(InitialViewport);
		return ValueTask.CompletedTask;
	}

	public ValueTask SetCameraAsync(MapCamera camera, CancellationToken cancellationToken = default)
	{
		Cameras.Add(camera);
		return ValueTask.CompletedTask;
	}

	public ValueTask FitBoundsAsync(
		TrackBounds bounds,
		double paddingPx = 32,
		double maxZoomLevel = 16,
		CancellationToken cancellationToken = default)
	{
		Fits.Add(bounds);
		return ValueTask.CompletedTask;
	}

	public ValueTask SetSourceAsync(MapSource source, CancellationToken cancellationToken = default)
	{
		Sources.Add(source);
		Provider = source.Provider;
		return ValueTask.CompletedTask;
	}

	public ValueTask DisposeAsync(CancellationToken cancellationToken = default)
	{
		DisposeCount++;
		return ValueTask.CompletedTask;
	}
}
