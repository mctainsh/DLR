using Microsoft.AspNetCore.Components;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// The base-map seam behind <c>RideMap.razor</c> KD(§4.5, §18.3, v0.21).
/// <para>
/// <strong>The base map is one of three JavaScript SDKs</strong> — Apple Maps on iOS
/// (MapKit JS), Google Maps on Android (JavaScript API), OpenLayers + OSM on the web —
/// chosen at host registration. Each module handles pan / zoom / rotate / tiles /
/// attribution, and emits a Web-Mercator <see cref="MapViewport"/> whenever the view
/// moves. <strong>None of them draws markers or tracks.</strong> Authored content lives
/// in <see cref="IMapOverlay"/>, a single C# component running SkiaSharp on top of
/// whichever base map is under it.
/// </para>
/// <para>
/// <strong>Failure branch is the shared component's, not the module's</strong>
/// (§4.5). A map that cannot get an API key or a token shows a stated error, not a
/// grey rectangle — that decision lives in <c>RideMap.razor</c>, where it renders once
/// for every provider.
/// </para>
/// </summary>
public interface IMapInterop
{
	/// <summary>Which base map is bound. The attribution string follows from it (§4.5).</summary>
	MapProvider Provider { get; }

	/// <summary>
	/// Fired whenever the base map pans or zooms. The overlay listens and repaints; nothing else
	/// consumes this. Emitted at least once immediately after <see cref="InitAsync"/> resolves so
	/// the overlay has a starting frame.
	/// </summary>
	event Action<MapViewport>? ViewportChanged;

	/// <summary>
	/// Fired when the user taps or clicks a point on the base map. The three SDKs all
	/// report a tap in their own coordinate space; each module converts to lat / lon
	/// before raising this, so a subscriber never learns which provider is under it.
	/// <para>
	/// This is how the marker composer lets an author place a point by pointing at it
	/// (§16.1) instead of typing two decimal numbers. Nothing consumes it during a live
	/// ride — the overlay does not hit-test, and a stray tap on the live map is inert.
	/// </para>
	/// </summary>
	event Action<MapClick>? Clicked;

	/// <summary>Attach the map to the DOM element the shared component owns.</summary>
	ValueTask InitAsync(ElementReference host, MapOptions options, CancellationToken cancellationToken = default);

	/// <summary>Move the camera.</summary>
	ValueTask SetCameraAsync(MapCamera camera, CancellationToken cancellationToken = default);

	/// <summary>Tear the map down and release the JS resources.</summary>
	ValueTask DisposeAsync(CancellationToken cancellationToken = default);
}

/// <summary>Which base map is behind the interop, and therefore which attribution string is required (§4.5, v0.21).</summary>
public enum MapProvider
{
	/// <summary>Apple Maps via MapKit JS — iOS.</summary>
	AppleMaps = 0,

	/// <summary>Google Maps via the JavaScript API — Android.</summary>
	GoogleMaps = 1,

	/// <summary>OpenLayers with OpenStreetMap tiles — web.</summary>
	OpenLayersOsm = 2,
}

/// <summary>Bootstrap options for <see cref="IMapInterop.InitAsync"/>.</summary>
/// <param name="Camera">Where the map opens.</param>
/// <param name="ShowUserLocation">Whether the platform's "blue dot" is drawn. Phone-only in practice.</param>
public sealed record MapOptions(MapCamera Camera, bool ShowUserLocation = false);

/// <summary>A point the user tapped on the base map, in decimal degrees (§16.1).</summary>
/// <param name="Latitude">Decimal degrees.</param>
/// <param name="Longitude">Decimal degrees.</param>
public readonly record struct MapClick(double Latitude, double Longitude);

/// <summary>A camera position (§4.5).</summary>
/// <param name="Latitude">Decimal degrees.</param>
/// <param name="Longitude">Decimal degrees.</param>
/// <param name="ZoomLevel">Whatever the provider means by "zoom"; the three we support agree.</param>
/// <param name="HeadingDeg">Degrees clockwise from true north.</param>
public sealed record MapCamera(double Latitude, double Longitude, double ZoomLevel, double HeadingDeg = 0);

/// <summary>
/// The base map's current view, as the shared overlay needs it to draw in register with
/// the tiles under it (§4.5 v0.21).
/// <para>
/// Every base-map module emits this exact shape on pan / zoom / rotate. The overlay
/// projects lat / lon → pixels through Web Mercator, which is the projection the three
/// base maps we support render natively — so a pixel here lands on the corresponding
/// tile pixel.
/// </para>
/// </summary>
/// <param name="TopLeftLatitude">Northmost latitude visible in the canvas.</param>
/// <param name="TopLeftLongitude">Westmost longitude visible.</param>
/// <param name="BottomRightLatitude">Southmost latitude visible.</param>
/// <param name="BottomRightLongitude">Eastmost longitude visible.</param>
/// <param name="ZoomLevel">The base map's zoom.</param>
/// <param name="HeadingDeg">Degrees clockwise from true north.</param>
/// <param name="CanvasWidthPx">Overlay canvas width in device pixels.</param>
/// <param name="CanvasHeightPx">Overlay canvas height in device pixels.</param>
/// <param name="DevicePixelRatio">Backing store pixels per CSS pixel.</param>
public readonly record struct MapViewport(
	double TopLeftLatitude,
	double TopLeftLongitude,
	double BottomRightLatitude,
	double BottomRightLongitude,
	double ZoomLevel,
	double HeadingDeg,
	int CanvasWidthPx,
	int CanvasHeightPx,
	double DevicePixelRatio)
{
	/// <summary>
	/// Longitude at the centre of the view. Web Mercator leaves longitude linear, so the
	/// plain midpoint is exact. Naive across the antimeridian, which no view we hand to a
	/// camera spans in practice.
	/// </summary>
	public double CentreLongitude => (TopLeftLongitude + BottomRightLongitude) / 2;

	/// <summary>
	/// Latitude at the centre of the view.
	/// <para>
	/// Latitude is <em>not</em> linear in Web Mercator — the midpoint of the two edge
	/// latitudes is not the middle of the screen, and the error grows with both zoom-out
	/// and distance from the equator. Averaging in projected space and inverting is what
	/// the base maps do themselves, so this agrees with the pixel the user is looking at.
	/// </para>
	/// </summary>
	public double CentreLatitude => MapGeometry.MercatorMidLatitude(TopLeftLatitude, BottomRightLatitude);
}

/// <summary>A marker as the overlay draws it (§16.2). Wire model is <c>MarkerDto</c> in <c>DLR.Core.Contracts</c>.</summary>
/// <param name="Id">Stable identifier the marker is upserted against.</param>
/// <param name="Latitude">Decimal degrees.</param>
/// <param name="Longitude">Decimal degrees.</param>
/// <param name="IconKey">A key from the curated set (§16.2). Unknown keys degrade to <c>note</c>. Ignored when <paramref name="Kind"/> is <see cref="MarkerKind.Rider"/>.</param>
/// <param name="Title">Rendered beside the icon.</param>
/// <param name="DirectionDeg">Null means "no direction", never zero (§16.2).</param>
/// <param name="Kind">What the overlay draws. Defaults to an authored marker.</param>
public sealed record MapMarker(
	Guid Id,
	double Latitude,
	double Longitude,
	string IconKey,
	string Title,
	double? DirectionDeg = null,
	MarkerKind Kind = MarkerKind.Authored);

/// <summary>
/// Which of the overlay's two renderings a <see cref="MapMarker"/> gets (§16.3).
/// <para>
/// This is a separate field rather than a reserved <see cref="MapMarker.IconKey"/> value on
/// purpose. Icon keys are forward-compatible by design — <c>MarkerIcons.ForSymbol</c> passes
/// any well-shaped GPX <c>&lt;sym&gt;</c> straight through so a newer client's key survives a
/// round trip — so a sentinel key would collide with real authored data the moment someone
/// imported a file containing it, and the server has no way to reserve one.
/// </para>
/// </summary>
public enum MarkerKind
{
	/// <summary>Something a rider placed: icon on a disc, with its title beneath (§16.2).</summary>
	Authored = 0,

	/// <summary>A live position: the bare heading dart, no icon and no disc (§5.3).</summary>
	Rider = 1,
}

/// <summary>
/// A route drawn as a polyline overlay by the Skia layer.
/// <para>
/// <see cref="Colour"/> is what the <em>caller</em> asks for — the per-route palette entry
/// (§5.4). It is not necessarily what gets drawn: the overlay resolves it against the device's
/// own route-display preferences (§18.6), which can pin a colour to <see cref="TrackId"/> or
/// paint every route the same. Resolving here rather than in each calling page keeps the answer
/// in one place; a page that omits <see cref="TrackId"/> simply cannot be individually
/// recoloured, which is the right answer for a line that is not a saved track.
/// </para>
/// </summary>
/// <param name="EncodedPolyline">Google-style encoded polyline; the overlay decodes it once.</param>
/// <param name="Colour">Hex colour string — the palette's answer, before the device's preferences are applied.</param>
/// <param name="TrackId">The track this line is, when it is one. <c>null</c> for the editor's unsaved working copy.</param>
public sealed record RouteOverlay(string EncodedPolyline, string Colour = "#2563eb", Guid? TrackId = null);
