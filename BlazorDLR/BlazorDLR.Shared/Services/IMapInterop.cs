using DLR.Core.Tracks;
using Microsoft.AspNetCore.Components;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// The base-map seam behind <c>RideMap.razor</c> (§4.5, §18.3, v0.24).
/// <para>
/// <strong>One JavaScript SDK on every surface</strong> - MapLibre GL JS over OpenStreetMap
/// tiles, implemented once in <see cref="MapLibreInterop"/> and registered identically by
/// all three hosts. The module handles pan / zoom / rotate / tiles / attribution, and emits
/// a Web-Mercator <see cref="MapViewport"/> whenever the view moves. <strong>It does not
/// draw markers or tracks.</strong> Authored content lives in <see cref="IMapOverlay"/>, a
/// single C# component running SkiaSharp on top of it.
/// </para>
/// <para>
/// <strong>The interface survives the consolidation on purpose.</strong> v0.24 removed the
/// three providers this seam was built to abstract (§4.5), which is an argument for deleting
/// it - but §13 Q26 moves the tile source to self-hosted PMTiles before public announcement,
/// and an offline-pack renderer is the case it would be needed for again. The cost of
/// keeping it is one interface and one registration line per host.
/// </para>
/// <para>
/// <strong>Failure branch is the shared component's, not the module's</strong> (§4.5). A map
/// that cannot reach its tiles or its library shows a stated error, not a grey rectangle -
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
	/// ride - the overlay does not hit-test, and a stray tap on the live map is inert.
	/// </para>
	/// </summary>
	event Action<MapClick>? Clicked;

	/// <summary>
	/// Fired when the <em>user</em> moves the map, as distinct from the map being moved for them.
	/// <para>
	/// The live ride map has two modes that drive the camera on their own - following this rider
	/// (§5.3) and turning the map to their heading - and each has to yield the moment a hand takes
	/// hold of the map. <see cref="ViewportChanged"/> cannot answer that: it says where the map now
	/// is, never who put it there, so a mode that watched it would cancel itself on its own move.
	/// </para>
	/// <para>
	/// Not every gesture is here. Zoom is deliberately absent - closing in on a rider being
	/// followed is a request to see them better, not to stop following them.
	/// </para>
	/// </summary>
	event Action<MapGesture>? Gestured;

	/// <summary>
	/// Fired when the base map reports a problem it did not throw for - a tile source it cannot
	/// reach, a style it cannot parse, an archive it cannot read.
	/// <para>
	/// <strong>Distinct from <see cref="InitAsync"/> throwing.</strong> That means no map at all;
	/// this means a map that exists and is drawing nothing, which is the failure a rider actually
	/// meets and the one that used to be silent. Several can arrive for one cause - a broken source
	/// raises once per tile - so a subscriber should show the first and not stack them.
	/// </para>
	/// </summary>
	event Action<string>? ErrorOccurred;

	/// <summary>
	/// Fired when the answer to "does the ground on screen have tiles behind it" changes. The
	/// canonical account of this failure; every other site refers back here.
	/// <para>
	/// <strong>Only an offline pack ever answers no.</strong> A pack holds one region and declares
	/// its box, so MapLibre declines to request outside it - no error is raised, the map simply
	/// draws coarse land and water with nothing on them. That is the failure this event exists for,
	/// because it is the one that looks like success. An online source is asked for the world, and
	/// a tile it refuses arrives as <see cref="ErrorOccurred"/> instead.
	/// </para>
	/// <para>
	/// Raised on the settled view and only when the answer changes: a banner appearing and
	/// vanishing through a pan is worse than the silence it replaced.
	/// </para>
	/// </summary>
	event Action<MapCoverage>? CoverageChanged;

	/// <summary>Attach the map to the DOM element the shared component owns.</summary>
	ValueTask InitAsync(ElementReference host, MapOptions options, CancellationToken cancellationToken = default);

	/// <summary>Move the camera.</summary>
	/// <param name="camera">Where the map should be looking.</param>
	/// <param name="animation">
	/// How long to take getting there. <see cref="TimeSpan.Zero"/> - the default - puts the camera
	/// there on the next frame, which is what a caller <em>asserting</em> a view wants: opening on a
	/// stored camera, framing a ride, straightening after a restyle.
	/// <para>
	/// <strong>Anything above zero is for a camera being driven by something that keeps changing</strong>
	/// - following a rider, or turning the map to their heading (§5.3). Those arrive about once a
	/// second, and a fix-by-fix jump is a map that lurches: the ground holds still for a second and
	/// then teleports a bike-length, and on a corner the whole world snaps round in steps. Spreading
	/// each move across the gap to the next one is what turns that into travel. A duration a little
	/// under the arrival cadence is the useful range; longer and the map is showing where the rider
	/// <em>was</em>.
	/// </para>
	/// <para>
	/// It is a request, not a promise. A device set to reduce motion gets the jump - the base map
	/// honours that preference itself, and a rider who has asked their phone to stop animating things
	/// has not made an exception for maps.
	/// </para>
	/// </param>
	/// <param name="cancellationToken">Cancels the call.</param>
	ValueTask SetCameraAsync(MapCamera camera, TimeSpan animation = default, CancellationToken cancellationToken = default);

	/// <summary>
	/// Frame the camera on a lat / lon box, so the whole of it is on screen at the closest zoom
	/// that still holds it (§15.5).
	/// </summary>
	/// <remarks>
	/// <para>
	/// <strong>Not expressible as a <see cref="SetCameraAsync"/> call</strong>, which is why it is
	/// its own method rather than a helper that computes a <see cref="MapCamera"/>. The zoom that
	/// fits a box depends on the size of the canvas the box has to fit inside, and no caller knows
	/// that: the map is a responsive element whose height is set by CSS and whose width is whatever
	/// is left after the nav rail. A page picking a zoom is guessing, and the guess is wrong on
	/// every screen but the one it was tuned on - which is what a fixed zoom level was.
	/// </para>
	/// <para>
	/// Instantaneous, always - unlike <see cref="SetCameraAsync"/>, which takes a duration for the
	/// modes that drive a camera continuously. Nothing drives a fit continuously: it is a caller
	/// stating what should be on screen, once, and a flight to it reads as the route sliding into
	/// place rather than as a map opening on it.
	/// </para>
	/// <para>
	/// A no-op on a map that has not attached, and on a box that is not well formed
	/// (<see cref="TrackBounds.IsWellFormed"/>) - a track with one point, or none, has nothing to
	/// frame, and a caller holding one should not have to know that.
	/// </para>
	/// </remarks>
	/// <param name="bounds">The ground to fit on screen.</param>
	/// <param name="paddingPx">
	/// CSS pixels of breathing room between the box and the edge of the map. A route drawn hard
	/// against the frame looks clipped, and the overlay's own line has width the base map knows
	/// nothing about.
	/// </param>
	/// <param name="maxZoomLevel">
	/// How far in the fit may go. It only ever binds on a box smaller than the screen - a track
	/// recorded round a car park, or one whose points all landed on the same fix - where the fit
	/// would otherwise run to the deepest zoom the tiles have and show a rider a roof.
	/// </param>
	/// <param name="cancellationToken">Cancels the call.</param>
	ValueTask FitBoundsAsync(
		TrackBounds bounds,
		double paddingPx = 32,
		double maxZoomLevel = 16,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Put different tiles under the map, without tearing it down (§4.5).
	/// <para>
	/// The camera, the bearing and the rider's place on screen all survive - this replaces the
	/// style, which is the layer the tiles live in, and MapLibre keeps the view across one. That
	/// matters because the setting is changed on a screen showing a live preview, and a map that
	/// jumped back to a default camera on every keystroke of a URL would be unusable.
	/// </para>
	/// <para>
	/// A no-op on a map that has not attached. Callers change a device setting and let the map
	/// catch up; whether one is currently on screen is not their business.
	/// </para>
	/// </summary>
	/// <param name="source">The tiles to draw. Already normalised by <c>MapSourceState</c>.</param>
	/// <param name="cancellationToken">Cancels the call.</param>
	ValueTask SetSourceAsync(MapSource source, CancellationToken cancellationToken = default);

	/// <summary>Tear the map down and release the JS resources.</summary>
	ValueTask DisposeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Which base map is behind the interop, and therefore which attribution string is required
/// (§4.5, v0.24).
/// <para>
/// It went down to one member at v0.24 and was kept as an enum rather than deleted because
/// the attribution obligation is per tile source, not per app. That is exactly what happened:
/// the rider now chooses a source (<see cref="MapSource"/>), and each of the three carries a
/// different credit. The renderer behind all of them is still MapLibre.
/// </para>
/// </summary>
public enum MapProvider
{
	/// <summary>MapLibre GL JS with OpenStreetMap raster tiles. The default (§4.5 v0.24).</summary>
	MapLibreOsm = 0,

	/// <summary>A PMTiles archive on this device, read through the <c>pmtiles://</c> protocol (§13 Q26).</summary>
	Pmtiles = 1,

	/// <summary>A rider-supplied XYZ raster source, credited with whatever they supplied with it.</summary>
	CustomRaster = 2,
}

/// <summary>Bootstrap options for <see cref="IMapInterop.InitAsync"/>.</summary>
/// <param name="Camera">Where the map opens.</param>
/// <param name="ShowUserLocation">Whether the platform's "blue dot" is drawn. Phone-only in practice.</param>
/// <param name="AllowRotation">
/// Whether the rider may turn the map off north, and therefore whether a compass is offered.
/// <para>
/// On by default, and off on the screens where a tap <em>places</em> something - the private-area
/// picker (§10.1) and the marker composer (§16.1). On those the map is a coordinate entry field:
/// a rider who has rotated it and then taps is reasoning about a north-up mental image that is no
/// longer on screen, and the point lands somewhere they did not mean.
/// </para>
/// <para>
/// There is no matching option for pitch. Tilting is refused on every map, because the Skia
/// overlay projects flat Web Mercator from a <see cref="MapViewport"/> that has no pitch term -
/// a tilted base map would leave every pin, track and circle drawn for a view nobody is looking
/// at. That is a constraint of the design, not a preference.
/// </para>
/// </param>
/// <param name="Source">
/// Which tiles go underneath (§4.5). <c>null</c> means <see cref="MapSource.Default"/> - OSM,
/// which is what every map drew before the setting existed, and the right answer for a caller
/// that has no opinion.
/// </param>
public sealed record MapOptions(
	MapCamera Camera,
	bool ShowUserLocation = false,
	bool AllowRotation = true,
	MapSource? Source = null)
{
	/// <summary>The tiles to draw, resolving <c>null</c> to the default.</summary>
	public MapSource EffectiveSource => Source ?? MapSource.Default;
}

/// <summary>
/// A move of the base map the <em>rider</em> performed, reported so an automatic camera mode can
/// step out of the way (see <see cref="IMapInterop.Gestured"/>).
/// <para>
/// Two members rather than one, because the two modes they cancel are independent: a rider who
/// has panned away to look at a junction has not asked for the map to swing back to north, and a
/// rider who has turned the map has not asked to stop being followed.
/// </para>
/// </summary>
public enum MapGesture
{
	/// <summary>A drag. Cancels "follow me" (§5.3).</summary>
	Pan = 0,

	/// <summary>
	/// A turn - a two-finger twist, or the compass button, which are the same statement about
	/// which way up the map should be. Cancels "heading up".
	/// </summary>
	Rotate = 1,
}

/// <summary>A point the user tapped on the base map, in decimal degrees (§16.1).</summary>
/// <param name="Latitude">Decimal degrees.</param>
/// <param name="Longitude">Decimal degrees.</param>
public readonly record struct MapClick(double Latitude, double Longitude);

/// <summary>
/// Whether the tiles under the map reach the ground it is looking at - see
/// <see cref="IMapInterop.CoverageChanged"/>.
/// </summary>
/// <param name="HasTiles">Whether anything on screen is inside what the source holds.</param>
/// <param name="ZoomLevel">
/// The zoom it was taken at. Part of the answer rather than context: "there is nothing here" is
/// only true of where the map is now.
/// </param>
public readonly record struct MapCoverage(bool HasTiles, double ZoomLevel);

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
/// renders natively - so a pixel here lands on the corresponding tile pixel.
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
	/// Latitude is <em>not</em> linear in Web Mercator - the midpoint of the two edge
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
/// which reads as stopped - an arrow is a claim about direction, and a fix with no speed is not
/// evidence for one.
/// </param>
/// <param name="Colour">
/// The label's background as <c>#rrggbb</c>, or null for <c>MarkerColours.Default</c>. Riders
/// only - it is the rider's own choice from their profile (§7.14, §16.3), and the text and border
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
/// purpose. Icon keys are forward-compatible by design - <c>MarkerIcons.ForSymbol</c> passes
/// any well-shaped GPX <c>&lt;sym&gt;</c> straight through so a newer client's key survives a
/// round trip - so a sentinel key would collide with real authored data the moment someone
/// imported a file containing it, and the server has no way to reserve one.
/// </para>
/// </summary>
public enum MarkerKind
{
	/// <summary>Something a rider placed: icon on a disc, with its title beneath (§16.2).</summary>
	Authored = 0,

	/// <summary>
	/// A live position: a heading arrow - or a dot when stopped - on the rounded end of a label
	/// carrying the rider's name in their own colour (§5.3, §16.3). No icon: which of twenty
	/// people this is, and which way they are going, is the whole of what a live position says.
	/// </summary>
	Rider = 1,

	/// <summary>
	/// <em>You</em> - this device's own fix, read straight from the platform receiver rather than
	/// from a published position (§4.3).
	/// <para>
	/// Drawn as a bare arrow or dot with no label, which is what tells it apart from
	/// <see cref="Rider"/> at a glance: every other pin on the map is somebody with a name on it,
	/// and the one without a name is the person holding the phone.
	/// </para>
	/// <para>
	/// It exists because the rider pins are a round trip - the ride only carries positions once it
	/// is <c>Live</c>, and the fan-out is on a 5 s tick (§5.3) - and none of that should stand
	/// between somebody and seeing where they are. It replaces the base map's own "blue dot" on
	/// the phone, which cannot work there: that one asks the WebView for <c>navigator.geolocation</c>,
	/// a permission gate entirely separate from the one the app holds for
	/// <see cref="ILocationProvider"/>, and neither MAUI host grants it.
	/// </para>
	/// </summary>
	Self = 2,
}

/// <summary>
/// A route drawn as a polyline overlay by the Skia layer.
/// <para>
/// <see cref="Colour"/> is what the <em>caller</em> asks for - the per-route palette entry
/// (§5.4). It is not necessarily what gets drawn: the overlay resolves it against the device's
/// own route-display preferences (§18.6), which can pin a colour to <see cref="TrackId"/> or
/// paint every route the same. Resolving here rather than in each calling page keeps the answer
/// in one place; a page that omits <see cref="TrackId"/> simply cannot be individually
/// recoloured, which is the right answer for a line that is not a saved track.
/// </para>
/// </summary>
/// <param name="EncodedPolyline">Google-style encoded polyline; the overlay decodes it once.</param>
/// <param name="Colour">Hex colour string - the palette's answer, before the device's preferences are applied.</param>
/// <param name="TrackId">The track this line is, when it is one. <c>null</c> for the editor's unsaved working copy.</param>
public sealed record RouteOverlay(string EncodedPolyline, string Colour = "#2563eb", Guid? TrackId = null);

/// <summary>
/// A ground circle drawn by the Skia overlay: a centre, a radius in <em>metres</em>, and a dot
/// on the middle so the centre is findable when the ring is off screen.
/// <para>
/// Metres rather than pixels because every circle this app draws is a statement about the
/// ground - today the private area on the Location screen (§10.1), whose whole meaning is
/// "this far around here". A pixel radius would grow and shrink the protected area as the rider
/// zoomed, which is exactly the wrong thing for a control someone is trying to reason about.
/// </para>
/// </summary>
/// <param name="Latitude">Centre latitude in decimal degrees.</param>
/// <param name="Longitude">Centre longitude in decimal degrees.</param>
/// <param name="RadiusM">Radius on the ground, in metres.</param>
/// <param name="Colour">Hex colour for the ring and the wash inside it.</param>
public sealed record MapCircle(double Latitude, double Longitude, double RadiusM, string Colour = "#dc2626");

/// <summary>
/// A lat / lon rectangle drawn by the Skia overlay - an outline with a wash inside it.
/// <para>
/// Its one caller is the map picker on the settings screen (§4.2), which draws the ground every
/// offline pack covers so a rider can point at the one they want instead of hunting for its name
/// in a list of two hundred. That is why it is a box and not a <see cref="RouteOverlay"/> tracing
/// four corners: a route carries the device's own line colour, width and direction chevrons
/// (§18.6), none of which mean anything on an extent, and a rider who had turned arrows on would
/// find them marching round the edge of every region on offer.
/// </para>
/// <para>
/// Edges are straight in Web Mercator - the projection the overlay draws in - so the four corners
/// are the whole of the shape however far the box is from the equator. It is drawn as a
/// quadrilateral rather than a screen-aligned rectangle so that it still sits over the ground it
/// names on a map that has been turned.
/// </para>
/// </summary>
/// <param name="Bounds">The ground it covers.</param>
/// <param name="Colour">Hex colour for the outline and the wash inside it.</param>
/// <param name="Emphasised">
/// Whether to draw it as the one being talked about - a heavier edge and a stronger wash. Set on
/// the boxes a tap landed in while the rider is choosing between them, which is the only way to
/// tell somebody <em>which</em> of two overlapping regions a name in a list refers to.
/// </param>
public sealed record MapBox(TrackBounds Bounds, string Colour = "#2563eb", bool Emphasised = false);
