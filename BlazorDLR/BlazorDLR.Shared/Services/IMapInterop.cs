using Microsoft.AspNetCore.Components;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// The base-map seam behind <c>RideMap.razor</c> (§4.5, §18.3, v0.24).
/// <para>
/// <strong>One JavaScript SDK on every surface</strong> — MapLibre GL JS over OpenStreetMap
/// tiles, implemented once in <see cref="MapLibreInterop"/> and registered identically by
/// all three hosts. The module handles pan / zoom / rotate / tiles / attribution, and emits
/// a Web-Mercator <see cref="MapViewport"/> whenever the view moves. <strong>It does not
/// draw markers or tracks.</strong> Authored content lives in <see cref="IMapOverlay"/>, a
/// single C# component running SkiaSharp on top of it.
/// </para>
/// <para>
/// <strong>The interface survives the consolidation on purpose.</strong> v0.24 removed the
/// three providers this seam was built to abstract (§4.5), which is an argument for deleting
/// it — but §13 Q26 moves the tile source to self-hosted PMTiles before public announcement,
/// and an offline-pack renderer is the case it would be needed for again. The cost of
/// keeping it is one interface and one registration line per host.
/// </para>
/// <para>
/// <strong>Failure branch is the shared component's, not the module's</strong> (§4.5). A map
/// that cannot reach its tiles or its library shows a stated error, not a grey rectangle —
/// that decision lives in <c>RideMap.razor</c>, where it renders once.
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
	/// Fired when the user taps or clicks a point on the base map, converted to lat / lon
	/// by the module so a subscriber never learns what is under it.
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

/// <summary>
/// Which base map is behind the interop, and therefore which attribution string is required
/// (§4.5, v0.24).
/// <para>
/// One member since v0.24. It is still an enum rather than a deleted concept because the
/// attribution obligation is per tile source, not per app: the PMTiles move scheduled by §13
/// Q26 changes who must be credited, and that is the change this type exists to make visible.
/// </para>
/// </summary>
public enum MapProvider
{
	/// <summary>MapLibre GL JS with OpenStreetMap tiles — every host (§4.5 v0.24).</summary>
	MapLibreOsm = 0,
}

/// <summary>Bootstrap options for <see cref="IMapInterop.InitAsync"/>.</summary>
/// <param name="Camera">Where the map opens.</param>
/// <param name="ShowUserLocation">Whether the platform's "blue dot" is drawn. Phone-only in practice.</param>
/// <param name="AllowRotation">
/// Whether the rider may turn the map off north, and therefore whether a compass is offered.
/// <para>
/// On by default, and off on the screens where a tap <em>places</em> something — the private-area
/// picker (§10.1) and the marker composer (§16.1). On those the map is a coordinate entry field:
/// a rider who has rotated it and then taps is reasoning about a north-up mental image that is no
/// longer on screen, and the point lands somewhere they did not mean.
/// </para>
/// <para>
/// There is no matching option for pitch. Tilting is refused on every map, because the Skia
/// overlay projects flat Web Mercator from a <see cref="MapViewport"/> that has no pitch term —
/// a tilted base map would leave every pin, track and circle drawn for a view nobody is looking
/// at. That is a constraint of the design, not a preference.
/// </para>
/// </param>
public sealed record MapOptions(
	MapCamera Camera,
	bool ShowUserLocation = false,
	bool AllowRotation = true);

/// <summary>A point the user tapped on the base map, in decimal degrees (§16.1).</summary>
/// <param name="Latitude">Decimal degrees.</param>
/// <param name="Longitude">Decimal degrees.</param>
public readonly record struct MapClick(double Latitude, double Longitude);

/// <summary>A camera position (§4.5).</summary>
/// <param name="Latitude">Decimal degrees.</param>
/// <param name="Longitude">Decimal degrees.</param>
/// <param name="ZoomLevel">Web-Mercator zoom, as MapLibre and OSM both number it.</param>
/// <param name="HeadingDeg">Degrees clockwise from true north.</param>
public sealed record MapCamera(double Latitude, double Longitude, double ZoomLevel, double HeadingDeg = 0);

/// <summary>
/// The base map's current view, as the shared overlay needs it to draw in register with
/// the tiles under it (§4.5 v0.21).
/// <para>
/// The base-map module emits this exact shape on pan / zoom / rotate. The overlay
/// projects lat / lon → pixels through Web Mercator, which is the projection MapLibre
/// renders natively — so a pixel here lands on the corresponding tile pixel.
/// <para>
/// The shape is still a contract rather than MapLibre's own types, because the overlay is
/// the half of the map this project owns and it must not learn what is under it (§4.5 v0.21).
/// </para>
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
/// <param name="SpeedMps">
/// How fast the fix said the rider was going (metres/second), when it said. Riders only: it is what decides
/// between the heading arrow and the stopped dot (§16.3). Null means the fix carried no speed,
/// which reads as stopped — an arrow is a claim about direction, and a fix with no speed is not
/// evidence for one.
/// </param>
/// <param name="Colour">
/// The label's background as <c>#rrggbb</c>, or null for <c>MarkerColours.Default</c>. Riders
/// only — it is the rider's own choice from their profile (§7.14, §16.3), and the text and border
/// are drawn in whichever of black or white reads on it.
/// </param>
public sealed record MapMarker(
	Guid Id,
	double Latitude,
	double Longitude,
	string IconKey,
	string Title,
	double? DirectionDeg = null,
	MarkerKind Kind = MarkerKind.Authored,
	double? SpeedMps = null,
	string? Colour = null);

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

	/// <summary>
	/// A live position: a heading arrow — or a dot when stopped — on the rounded end of a label
	/// carrying the rider's name in their own colour (§5.3, §16.3). No icon: which of twenty
	/// people this is, and which way they are going, is the whole of what a live position says.
	/// </summary>
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

/// <summary>
/// A ground circle drawn by the Skia overlay: a centre, a radius in <em>metres</em>, and a dot
/// on the middle so the centre is findable when the ring is off screen.
/// <para>
/// Metres rather than pixels because every circle this app draws is a statement about the
/// ground — today the private area on the profile screen (§10.1, §18.6), whose whole meaning is
/// "this far around here". A pixel radius would grow and shrink the protected area as the rider
/// zoomed, which is exactly the wrong thing for a control someone is trying to reason about.
/// </para>
/// </summary>
/// <param name="Latitude">Centre latitude in decimal degrees.</param>
/// <param name="Longitude">Centre longitude in decimal degrees.</param>
/// <param name="RadiusM">Radius on the ground, in metres.</param>
/// <param name="Colour">Hex colour for the ring and the wash inside it.</param>
public sealed record MapCircle(double Latitude, double Longitude, double RadiusM, string Colour = "#dc2626");
