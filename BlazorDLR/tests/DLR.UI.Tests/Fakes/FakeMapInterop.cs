using BlazorDLR.Shared.Services;
using Microsoft.AspNetCore.Components;

namespace DLR.UI.Tests.Fakes;

/// <summary>
/// A hand-rolled <see cref="IMapInterop"/> that lets bUnit render <c>RideMap</c> against
/// any of the three real providers (§4.5 v0.21) without loading a JS SDK. Two levers:
/// <list type="bullet">
///   <item><c>Provider</c> — which of Apple, Google, OpenLayers the fake reports.</item>
///   <item><c>InitException</c> — the exception <see cref="InitAsync"/> should throw, if any.
///     Set this to reproduce the "MapKit token unavailable" branch and the "provider key
///     absent" branch, since both surface identically to <c>RideMap</c>.</item>
/// </list>
/// </summary>
public sealed class FakeMapInterop : IMapInterop
{
	public MapProvider Provider { get; set; } = MapProvider.OpenLayersOsm;

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

	public ValueTask DisposeAsync(CancellationToken cancellationToken = default)
	{
		DisposeCount++;
		return ValueTask.CompletedTask;
	}
}
