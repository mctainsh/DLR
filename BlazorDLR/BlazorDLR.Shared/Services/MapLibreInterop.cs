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
	private readonly IMapPackServer _packs;

	private IJSObjectReference? _module;
	private IJSObjectReference? _map;
	private DotNetObjectReference<MapBridge>? _bridge;
	private MapSource _source = MapSource.Default;

	/// <param name="js">The runtime the module is imported into.</param>
	/// <param name="packs">
	/// Turns an offline pack id into a URL the WebView can range-read (§13 Q26). On the browser
	/// hosts it answers nothing, which is what makes an offline source fall back to OSM there.
	/// </param>
	public MapLibreInterop(IJSRuntime js, IMapPackServer packs)
	{
		_js = js;
		_packs = packs;
	}

	/// <summary>
	/// Which source is currently under the map, and therefore whose credit is required. Follows
	/// <see cref="SetSourceAsync"/> rather than being fixed at construction — the renderer is
	/// MapLibre either way, and the attribution is the part that moves (§4.5).
	/// </summary>
	public MapProvider Provider => _source.Provider;

	/// <inheritdoc />
	public event Action<MapViewport>? ViewportChanged;

	/// <inheritdoc />
	public event Action<MapClick>? Clicked;

	/// <inheritdoc />
	public event Action<string>? ErrorOccurred;

	/// <inheritdoc />
	public async ValueTask InitAsync(ElementReference host, MapOptions options, CancellationToken cancellationToken = default)
	{
		_module ??= await _js.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath);
		_bridge = DotNetObjectReference.Create(new MapBridge(
			v => ViewportChanged?.Invoke(v),
			c => Clicked?.Invoke(c),
			m => ErrorOccurred?.Invoke(m)));

		_source = options.EffectiveSource;

		_map = await _module.InvokeAsync<IJSObjectReference>("createMap", cancellationToken, host, new
		{
			latitude = options.Camera.Latitude,
			longitude = options.Camera.Longitude,
			zoomLevel = options.Camera.ZoomLevel,
			headingDeg = options.Camera.HeadingDeg,
			showUserLocation = options.ShowUserLocation,
			allowRotation = options.AllowRotation,
			source = await DescribeAsync(_source, cancellationToken),
		}, new { onViewportChanged = _bridge, onMapClicked = _bridge, onMapError = _bridge });
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
	public async ValueTask SetSourceAsync(MapSource source, CancellationToken cancellationToken = default)
	{
		_source = source;

		if (_map is null)
		{
			// Not attached yet — InitAsync will carry it. A caller changing a device setting has no
			// business knowing whether a map happens to be on screen.
			return;
		}

		await Call("setSource", cancellationToken, await DescribeAsync(source, cancellationToken));
	}

	/// <summary>
	/// The source as the JS module needs it: a tile template or an archive URL, a credit, and how
	/// deep it goes. The module never sees <see cref="MapSource"/> itself, for the same reason the
	/// viewport is a contract rather than MapLibre's own type — the two halves are versioned
	/// separately.
	/// <para>
	/// <strong>Asynchronous because of one field.</strong> An offline pack has no URL until the
	/// loopback server has been asked for one: the port is assigned by the OS and the path carries
	/// a per-run secret, so it cannot be stored in the setting and has to be resolved each time
	/// (see <see cref="IMapPackServer"/>).
	/// </para>
	/// <para>
	/// A pack this device cannot serve — deleted since it was chosen, or a host that holds none —
	/// falls back to the default here rather than being sent on as an offline source with nothing
	/// behind it. The module performs the same fallback; both exist because the failure mode is a
	/// blank map, and a rider in a dead zone reading a blank map cannot tell it from a broken app.
	/// </para>
	/// </summary>
	private async ValueTask<object> DescribeAsync(MapSource source, CancellationToken cancellationToken)
	{
		Uri? archive = null;

		if (source.Kind == MapSourceKind.Offline && source.PackId is { } packId)
		{
			archive = await _packs.ResolveAsync(packId, cancellationToken);
		}

		MapSource usable = source.Kind == MapSourceKind.Offline && archive is null
			? MapSource.Default
			: source;

		return new
		{
			kind = usable.Kind.ToString().ToLowerInvariant(),
			tileUrl = usable.TileUrl,
			packId = usable.PackId,
			archiveUrl = archive?.ToString(),
			attribution = usable.AttributionText,
			maxZoom = usable.MaxZoom,
		};
	}

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
