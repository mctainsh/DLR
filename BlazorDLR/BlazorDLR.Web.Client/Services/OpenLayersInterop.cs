using BlazorDLR.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BlazorDLR.Web.Client.Services;

/// <summary>
/// The web's <see cref="IMapInterop"/>: OpenLayers 9.x backed by OpenStreetMap tiles
/// (§4.5 v0.21, §18.3). Base-map role only — every marker and track is drawn by the
/// shared <see cref="IMapOverlay"/> on top.
/// </summary>
public sealed class OpenLayersInterop : IMapInterop
{
	private const string ModulePath = "./_content/BlazorDLR.Shared/map/map.openlayers.js";

	private readonly IJSRuntime _js;
	private IJSObjectReference? _module;
	private IJSObjectReference? _map;
	private DotNetObjectReference<MapBridge>? _bridge;

	public OpenLayersInterop(IJSRuntime js)
	{
		_js = js;
	}

	/// <inheritdoc />
	public MapProvider Provider => MapProvider.OpenLayersOsm;

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
			try { await _module.DisposeAsync(); } catch { /* circuit already gone */ }
			_module = null;
		}
	}

	private async ValueTask Call(string method, CancellationToken cancellationToken, object? argument)
	{
		if (_map is null)
		{
			throw new InvalidOperationException(
				$"OpenLayers interop: {method} called before InitAsync — the host element is not attached yet.");
		}
		await _map.InvokeVoidAsync(method, cancellationToken, argument);
	}

}
