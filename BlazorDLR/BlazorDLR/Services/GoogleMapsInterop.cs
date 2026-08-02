using BlazorDLR.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BlazorDLR.Services;

/// <summary>
/// The Android phone's <see cref="IMapInterop"/>: Google Maps via the JavaScript API
/// inside the MAUI <c>BlazorWebView</c> (§4.5 v0.21, §18.3). Base-map role only.
/// <para>
/// The API key is a browser-type key restricted to the app's bundle id at the provider,
/// supplied at construction time. Phase 1 fetches it from the server (rather than baking
/// it into the app); Phase 0 accepts it as a plain string so a real device can render.
/// A missing key surfaces as an exception the shared <c>RideMap.razor</c> renders as a
/// stated error — <em>a map that cannot get a key shows a stated error, not a grey
/// rectangle</em> (§4.5).
/// </para>
/// </summary>
public sealed class GoogleMapsInterop : IMapInterop
{
	private const string ModulePath = "./_content/BlazorDLR.Shared/map/map.googlemaps.js";

	private readonly IJSRuntime _js;
	private readonly string? _apiKey;

	private IJSObjectReference? _module;
	private IJSObjectReference? _map;
	private DotNetObjectReference<ViewportBridge>? _bridge;

	public GoogleMapsInterop(IJSRuntime js, GoogleMapsApiKey key)
	{
		_js = js;
		_apiKey = key.Value;
	}

	/// <inheritdoc />
	public MapProvider Provider => MapProvider.GoogleMaps;

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
			apiKey = _apiKey,
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
				$"Google Maps interop: {method} called before InitAsync — the host element is not attached yet.");
		}
		await _map.InvokeVoidAsync(method, cancellationToken, argument);
	}

	private sealed class ViewportBridge
	{
		private readonly Action<MapViewport> _forward;
		public ViewportBridge(Action<MapViewport> forward) => _forward = forward;

		[JSInvokable]
		public void OnViewportChanged(MapViewport viewport) => _forward(viewport);
	}
}

/// <summary>
/// The Google Maps browser API key, wrapped so it can be registered in DI as a distinct
/// type. <see cref="Value"/> is null when the key is not configured — the interop's own
/// error branch reports that, not an ambient null-reference exception.
/// </summary>
public sealed record GoogleMapsApiKey(string? Value);
