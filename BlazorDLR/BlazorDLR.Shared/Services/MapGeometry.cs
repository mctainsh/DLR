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
	/// The latitude Web Mercator stops at. Beyond it the projection runs to infinity, which is
	/// why every slippy map in the world is a square that omits the poles.
	/// </summary>
	private const double MercatorLimitDeg = 85.05112878;
}
