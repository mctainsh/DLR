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
	private DotNetObjectReference<ViewportBridge>? _bridge;

	public OpenLayersInterop(IJSRuntime js)
	{
		_js = js;
	}

	/// <inheritdoc />
	public MapProvider Provider => MapProvider.OpenLayersOsm;

	/// <inheritdoc />
	public event Action<MapViewport>? ViewportChanged;

	/// <inheritdoc />
	public async ValueTask InitAsync(ElementReference host, MapOptions options, CancellationToken cancellationToken = default)
	{
		_module ??= await _js.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath);
		_bridge = DotNetObjectReference.Create(new ViewportBridge(v => ViewportChanged?.Invoke(v)));

		_map = await _module.InvokeAsync<IJSObjectReference>("createMap", cancellationToken, host, new
		{
			latitude = options.Camera.Latitude,
			longitude = options.Camera.Longitude,
			zoomLevel = options.Camera.ZoomLevel,
			headingDeg = options.Camera.HeadingDeg,
			showUserLocation = options.ShowUserLocation,
		}, new { onViewportChanged = _bridge });
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

	/// <summary>
	/// The JS module cannot call an event on a C# class directly, but it can call one method
	/// on a <see cref="DotNetObjectReference"/>. This bridge is what turns that call back
	/// into the <see cref="ViewportChanged"/> event the shared component listens to.
	/// </summary>
	private sealed class ViewportBridge
	{
		private readonly Action<MapViewport> _forward;
		public ViewportBridge(Action<MapViewport> forward) => _forward = forward;

		[JSInvokable]
		public void OnViewportChanged(MapViewport viewport) => _forward(viewport);
	}
}
