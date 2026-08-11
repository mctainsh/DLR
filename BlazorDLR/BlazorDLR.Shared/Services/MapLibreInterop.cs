using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// The base map, on every host: MapLibre GL JS over OpenStreetMap tiles (§4.5 v0.24, §18.3).
/// <para>
/// <strong>This class lives in the shared project, not in a host</strong>, and that is the
/// whole of what v0.24 bought. The three predecessors — <c>AppleMapsInterop</c> (MapKit JS),
/// <c>GoogleMapsInterop</c> and <c>OpenLayersInterop</c> — each carried a credential and a
/// per-host registration; between them they cost a <c>.p8</c> on the server, a browser API key
/// in the app bundle, and a token endpoint that made the map a server dependency. MapLibre
/// needs none of the three, so one registration answers for iOS, Android and the web.
/// </para>
/// <para>
/// Base-map role only. Every marker, rider pin and route is drawn by <c>SkiaMapOverlay</c> on
/// top (§4.5 v0.21), which is why nothing provider-shaped reaches the rest of the UI.
/// </para>
/// </summary>
public sealed class MapLibreInterop : IMapInterop
{
	private const string ModulePath = "./_content/BlazorDLR.Shared/map/map.maplibre.js";

	private readonly IJSRuntime _js;

	private IJSObjectReference? _module;
	private IJSObjectReference? _map;
	private DotNetObjectReference<MapBridge>? _bridge;

	public MapLibreInterop(IJSRuntime js)
	{
		_js = js;
	}

	/// <inheritdoc />
	public MapProvider Provider => MapProvider.MapLibreOsm;

	/// <inheritdoc />
	public event Action<MapViewport>? ViewportChanged;

	/// <inheritdoc />
	public event Action<MapClick>? Clicked;

	/// <inheritdoc />
	public async ValueTask InitAsync(ElementReference host, MapOptions options, CancellationToken cancellationToken = default)
	{
		_module ??= await _js.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath);
		_bridge = DotNetObjectReference.Create(new MapBridge(
			v => ViewportChanged?.Invoke(v),
			c => Clicked?.Invoke(c)));

		_map = await _module.InvokeAsync<IJSObjectReference>("createMap", cancellationToken, host, new
		{
			latitude = options.Camera.Latitude,
			longitude = options.Camera.Longitude,
			zoomLevel = options.Camera.ZoomLevel,
			headingDeg = options.Camera.HeadingDeg,
			showUserLocation = options.ShowUserLocation,
			allowRotation = options.AllowRotation,
		}, new { onViewportChanged = _bridge, onMapClicked = _bridge });
	}

	/// <inheritdoc />
	public ValueTask SetCameraAsync(MapCamera camera, CancellationToken cancellationToken = default) =>
		Call("setCamera", cancellationToken, new
		{
			latitude = camera.Latitude,
			longitude = camera.Longitude,
			zoomLevel = camera.ZoomLevel,
			headingDeg = camera.HeadingDeg,
		});

	/// <inheritdoc />
	public async ValueTask DisposeAsync(CancellationToken cancellationToken = default)
	{
		if (_map is not null)
		{
			try { await _map.InvokeVoidAsync("dispose", cancellationToken); }
			catch (JSDisconnectedException) { /* page navigating away */ }
			try { await _map.DisposeAsync(); } catch { /* already gone */ }
			_map = null;
		}
		_bridge?.Dispose();
		_bridge = null;

		// One interop instance per <RideMap>, so an undisposed module reference would leak
		// a tracked JS object on every navigation into a ride page.
		if (_module is not null)
		{
			try { await _module.DisposeAsync(); } catch { /* WebView or circuit already gone */ }
			_module = null;
		}
	}

	private async ValueTask Call(string method, CancellationToken cancellationToken, object? argument)
	{
		if (_map is null)
		{
			throw new InvalidOperationException(
				$"MapLibre interop: {method} called before InitAsync — the host element is not attached yet.");
		}
		await _map.InvokeVoidAsync(method, cancellationToken, argument);
	}
}
