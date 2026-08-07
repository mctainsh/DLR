using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Maps;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BlazorDLR.Services;

/// <summary>
/// The iOS phone's <see cref="IMapInterop"/>: Apple Maps via MapKit JS inside the MAUI
/// <c>BlazorWebView</c> (§4.5 v0.21, §18.3). Base-map role only.
/// <para>
/// Fetches a short-lived MapKit token via <see cref="HttpApiClient.GetMapTokenAsync"/> —
/// the private <c>.p8</c> stays on the server (§4.5, §14.2) — caches it in memory with
/// a one-minute pre-expiry buffer against <see cref="TimeProvider"/>, and passes it to
/// <c>map.mapkit.js</c> through the JS module's authorization callback.
/// </para>
/// </summary>
public sealed class AppleMapsInterop : IMapInterop
{
	private const string ModulePath = "./_content/BlazorDLR.Shared/map/map.mapkit.js";

	private readonly IJSRuntime _js;
	private readonly HttpApiClient _api;
	private readonly TimeProvider _clock;

	private IJSObjectReference? _module;
	private IJSObjectReference? _map;
	private DotNetObjectReference<MapBridge>? _bridge;
	private MapToken? _cachedToken;

	public AppleMapsInterop(IJSRuntime js, HttpApiClient api, TimeProvider clock)
	{
		_js = js;
		_api = api;
		_clock = clock;
	}

	/// <inheritdoc />
	public MapProvider Provider => MapProvider.AppleMaps;

	/// <inheritdoc />
	public event Action<MapViewport>? ViewportChanged;

	/// <inheritdoc />
	public event Action<MapClick>? Clicked;

	/// <inheritdoc />
	public async ValueTask InitAsync(ElementReference host, MapOptions options, CancellationToken cancellationToken = default)
	{
		string token = await FetchOrRefreshTokenAsync(cancellationToken);

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
			token,
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
			try { await _module.DisposeAsync(); } catch { /* WebView already gone */ }
			_module = null;
		}
	}

	private async Task<string> FetchOrRefreshTokenAsync(CancellationToken cancellationToken)
	{
		if (_cachedToken is { } cached && cached.ExpiresUtc - TimeSpan.FromMinutes(1) > _clock.GetUtcNow())
		{
			return cached.Token;
		}

		_cachedToken = await _api.GetMapTokenAsync(cancellationToken);
		return _cachedToken.Token;
	}

	private async ValueTask Call(string method, CancellationToken cancellationToken, object? argument)
	{
		if (_map is null)
		{
			throw new InvalidOperationException(
				$"Apple Maps interop: {method} called before InitAsync — the host element is not attached yet.");
		}
		await _map.InvokeVoidAsync(method, cancellationToken, argument);
	}

}
