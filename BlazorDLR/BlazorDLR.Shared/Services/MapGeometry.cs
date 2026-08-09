namespace BlazorDLR.Shared.Services;

/// <summary>
/// Small pieces of map maths the overlay needs, kept out of the component so they can be
/// asserted. <c>SkiaMapOverlay</c> cannot render in bUnit — its <c>SKCanvasView</c> is
/// browser-only — so anything with a sign in it is worth extracting rather than eyeballing
/// on a screenshot.
/// </summary>
public static class MapGeometry
{
	/// <summary>
	/// Turns a true bearing into the on-screen angle the overlay should rotate by.
	/// <para>
	/// A rider's <c>HeadingDeg</c> is degrees clockwise from <em>true north</em>. The overlay
	/// projects north-up, then <c>Project</c> rotates the whole canvas by the base map's
	/// heading so the pixels stay in register with a heading-up map. An arrow therefore has
	/// to lose that same rotation, or it points at true north while the tiles under it point
	/// somewhere else.
	/// </para>
	/// </summary>
	/// <param name="bearingDeg">Degrees clockwise from true north.</param>
	/// <param name="mapHeadingDeg">The base map's rotation, degrees clockwise from north.</param>
	/// <returns>Degrees clockwise from screen-up, normalised to [0, 360).</returns>
	public static double ScreenBearingDeg(double bearingDeg, double mapHeadingDeg)
	{
		double screen = (bearingDeg - mapHeadingDeg) % 360.0;
		return screen < 0 ? screen + 360.0 : screen;
	}

	/// <summary>
	/// The latitude halfway down a view whose top and bottom edges are given.
	/// <para>
	/// Latitude is <em>not</em> linear in Web Mercator, so the plain mean of the two edge
	/// latitudes is not the middle of the screen — the error grows with zoom-out and with
	/// distance from the equator. Averaging in projected space and inverting is what the
	/// base maps do themselves, so this agrees with the pixel the user is looking at.
	/// </para>
	/// </summary>
	/// <param name="topLatitudeDeg">Northmost latitude visible.</param>
	/// <param name="bottomLatitudeDeg">Southmost latitude visible.</param>
	/// <returns>The centre latitude in degrees.</returns>
	public static double MercatorMidLatitude(double topLatitudeDeg, double bottomLatitudeDeg)
	{
		double y = (MercatorY(topLatitudeDeg) + MercatorY(bottomLatitudeDeg)) / 2;
		return InverseMercatorY(y);
	}

	/// <summary>
	/// Web Mercator's forward latitude projection, in degree-like units.
	/// </summary>
	/// <param name="latitudeDeg">Latitude in degrees. Clamped to the projection's limit —
	/// the poles are at infinity, and no base map draws them.</param>
	/// <returns>The projected Y.</returns>
	public static double MercatorY(double latitudeDeg)
	{
		double clamped = Math.Clamp(latitudeDeg, -MercatorLimitDeg, MercatorLimitDeg);
		return 180 / Math.PI * Math.Log(Math.Tan(Math.PI / 4 + clamped * Math.PI / 360));
	}

	/// <summary>The inverse of <see cref="MercatorY"/>.</summary>
	/// <param name="y">A projected Y.</param>
	/// <returns>Latitude in degrees.</returns>
	public static double InverseMercatorY(double y) =>
		180 / Math.PI * ((2 * Math.Atan(Math.Exp(y * Math.PI / 180))) - (Math.PI / 2));

	/// <summary>
	/// Web Mercator projection from geographic degrees to overlay canvas pixels.
	/// <para>
	/// <strong>This is the one implementation.</strong> <c>SkiaMapOverlay</c> draws every pin
	/// through it and <c>GroupRideLive</c> hit-tests taps through it, so "near the pin" means
	/// near where the pin was actually drawn. A second copy of this arithmetic is precisely the
	/// drift §4.5 keeps one overlay to avoid — the two would agree until somebody rotated the
	/// map, and then taps would land on markers that are no longer under the finger.
	/// </para>
	/// <para>
	/// The base map is already in this projection — all three we support — so a pixel here
	/// lands on the corresponding tile pixel.
	/// </para>
	/// </summary>
	/// <param name="viewport">The base map's current view.</param>
	/// <param name="latitudeDeg">Latitude in degrees.</param>
	/// <param name="longitudeDeg">Longitude in degrees.</param>
	/// <returns>The point in canvas pixels, with the base map's rotation applied.</returns>
	public static CanvasPoint ProjectToCanvas(MapViewport viewport, double latitudeDeg, double longitudeDeg)
	{
		double width = viewport.CanvasWidthPx;
		double height = viewport.CanvasHeightPx;

		double spanX = viewport.BottomRightLongitude - viewport.TopLeftLongitude;
		double topY = MercatorY(viewport.TopLeftLatitude);
		double spanY = MercatorY(viewport.BottomRightLatitude) - topY;

		// A canvas that has not been measured yet, or a view with no extent. Everything
		// collapses to the centre — the only answer that cannot be mistaken for a position.
		if (Math.Abs(spanX) < 1e-12 || Math.Abs(spanY) < 1e-12)
		{
			return new CanvasPoint(width / 2, height / 2);
		}

		double x = width * (longitudeDeg - viewport.TopLeftLongitude) / spanX;
		double y = height * (MercatorY(latitudeDeg) - topY) / spanY;

		if (Math.Abs(viewport.HeadingDeg) <= 1e-6)
		{
			return new CanvasPoint(x, y);
		}

		// The overlay projects north-up; the base map's rotation is applied here so the pixels
		// stay in register when a heading-up map is in use (§4.5).
		double centreX = width / 2;
		double centreY = height / 2;
		double radians = -viewport.HeadingDeg * Math.PI / 180.0;
		double cos = Math.Cos(radians);
		double sin = Math.Sin(radians);
		double dx = x - centreX;
		double dy = y - centreY;

		return new CanvasPoint(
			centreX + (dx * cos) - (dy * sin),
			centreY + (dx * sin) + (dy * cos));
	}

	/// <summary>
	/// How many canvas pixels a distance on the ground covers, at a given latitude in a given view.
	/// <para>
	/// Measured rather than derived: the same distance is projected twice through
	/// <see cref="ProjectToCanvas"/> and the pixels between the two results are counted. Web
	/// Mercator's scale changes with latitude, so a closed-form answer would need the projection's
	/// derivative — a second piece of arithmetic that has to agree with the first one forever, on
	/// a canvas where disagreement shows up as a circle that does not sit on what it encircles.
	/// Rotation drops out, being rigid.
	/// </para>
	/// <para>
	/// The offset is taken toward the equator so a point near the top of the projection cannot be
	/// clamped at <see cref="MercatorLimitDeg"/> and silently measure short.
	/// </para>
	/// </summary>
	/// <param name="viewport">The base map's current view.</param>
	/// <param name="latitudeDeg">Where on the map the distance is being measured.</param>
	/// <param name="metres">The ground distance.</param>
	/// <returns>The distance in canvas pixels; zero for a canvas with no extent yet.</returns>
	public static double MetresToCanvasPixels(MapViewport viewport, double latitudeDeg, double metres)
	{
		if (!double.IsFinite(metres) || metres <= 0)
		{
			return 0;
		}

		double degrees = metres / MetresPerDegreeLatitude;
		double towardEquator = latitudeDeg >= 0 ? latitudeDeg - degrees : latitudeDeg + degrees;

		CanvasPoint from = ProjectToCanvas(viewport, latitudeDeg, 0);
		CanvasPoint to = ProjectToCanvas(viewport, towardEquator, 0);

		return from.DistanceTo(to);
	}

	/// <summary>
	/// Metres in one degree of latitude. Constant enough for this — the ellipsoid varies it by
	/// about a fifth of a percent pole to equator, which on a circle drawn on a phone is nothing.
	/// </summary>
	private const double MetresPerDegreeLatitude = Math.PI * DLR.Core.Tracks.Distance.EarthRadiusM / 180.0;

	/// <summary>
	/// The latitude Web Mercator stops at. Beyond it the projection runs to infinity, which is
	/// why every slippy map in the world is a square that omits the poles.
	/// </summary>
	private const double MercatorLimitDeg = 85.05112878;
}

/// <summary>
/// A point in overlay canvas space — the same pixels <c>SkiaMapOverlay</c> paints in, with the
/// origin at the canvas's top-left.
/// </summary>
/// <param name="X">Pixels from the left edge.</param>
/// <param name="Y">Pixels from the top edge.</param>
public readonly record struct CanvasPoint(double X, double Y)
{
	/// <summary>
	/// Straight-line distance to another canvas point, in the same pixels.
	/// <para>
	/// Screen distance rather than ground distance on purpose: a tap is aimed at something the
	/// eye can see, so the tolerance that matters is "how close to the drawn pin did the finger
	/// land". Metres would make the hit area of a marker grow and shrink as the map zoomed.
	/// </para>
	/// </summary>
	/// <param name="other">The point to measure to.</param>
	/// <returns>The distance in canvas pixels.</returns>
	public double DistanceTo(CanvasPoint other) => Math.Sqrt(
		((X - other.X) * (X - other.X)) + ((Y - other.Y) * (Y - other.Y)));
}
