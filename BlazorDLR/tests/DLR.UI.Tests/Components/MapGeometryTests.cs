using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Components;

/// <summary>
/// The heading arrow's rotation convention (§5.3). <c>SkiaMapOverlay</c> cannot render in
/// bUnit — <c>SKCanvasView</c> reaches for browser-only interop — so the one piece with a
/// sign in it is asserted here rather than eyeballed on a screenshot. A flipped sign points
/// every rider the wrong way, which looks plausible right up until someone follows it.
/// </summary>
public sealed class MapGeometryTests
{
	[Theory]
	// North-up map: the screen bearing is the true bearing.
	[InlineData(0, 0, 0)]
	[InlineData(90, 0, 90)]
	[InlineData(180, 0, 180)]
	[InlineData(270, 0, 270)]
	// Heading-up map: the base map's rotation comes back out, or the arrow points at true
	// north while the tiles under it do not.
	[InlineData(90, 90, 0)]
	[InlineData(0, 90, 270)]
	[InlineData(45, 90, 315)]
	public void ScreenBearing_RemovesTheBaseMapsRotation(double bearing, double mapHeading, double expected)
	{
		MapGeometry.ScreenBearingDeg(bearing, mapHeading).ShouldBe(expected, tolerance: 1e-9);
	}

	/// <summary>
	/// A square view one degree across, 1000 px on a side, centred on the origin. Mercator's
	/// latitude distortion is negligible this close to the equator, so the expected pixels are
	/// plain arithmetic.
	/// </summary>
	private static readonly MapViewport UnitViewport = new(
		TopLeftLatitude: 0.5, TopLeftLongitude: -0.5,
		BottomRightLatitude: -0.5, BottomRightLongitude: 0.5,
		ZoomLevel: 12, HeadingDeg: 0,
		CanvasWidthPx: 1000, CanvasHeightPx: 1000, DevicePixelRatio: 1);

	/// <summary>
	/// The same view on a canvas twice as wide as it is tall, so a quarter turn swaps two
	/// different numbers. A square canvas cannot tell a correct projection from one that scales
	/// each axis by the other's length, which is exactly how the bug below survived.
	/// </summary>
	private static readonly MapViewport WideViewport = new(
		TopLeftLatitude: MapGeometry.InverseMercatorY(0.5), TopLeftLongitude: -1,
		BottomRightLatitude: MapGeometry.InverseMercatorY(-0.5), BottomRightLongitude: 1,
		ZoomLevel: 12, HeadingDeg: 0,
		CanvasWidthPx: 800, CanvasHeightPx: 400, DevicePixelRatio: 1);

	/// <summary>
	/// The viewport a base map would actually report for a view turned to a bearing.
	/// <para>
	/// <strong>Setting <c>HeadingDeg</c> alone does not model a rotation.</strong> The bounds a
	/// base map reports are axis-aligned, so turning the map <em>widens and heightens</em> them —
	/// the box has to enclose the corners of the turned rectangle. A test that holds the bounds
	/// still and only changes the heading is asserting against a view that cannot exist, and will
	/// happily pass a projection that gets every off-centre pin wrong on a real screen.
	/// </para>
	/// </summary>
	/// <param name="northUp">The view before it is turned.</param>
	/// <param name="headingDeg">Degrees clockwise from north.</param>
	/// <returns>The same view, turned, with the bounds a base map would report for it.</returns>
	private static MapViewport Turned(MapViewport northUp, double headingDeg)
	{
		double radians = headingDeg * Math.PI / 180.0;
		double absCos = Math.Abs(Math.Cos(radians));
		double absSin = Math.Abs(Math.Sin(radians));

		double topY = MapGeometry.MercatorY(northUp.TopLeftLatitude);
		double bottomY = MapGeometry.MercatorY(northUp.BottomRightLatitude);

		// Taken north-up, where the box is the canvas and the scale is unambiguous.
		double degreesPerPixelX =
			(northUp.BottomRightLongitude - northUp.TopLeftLongitude) / northUp.CanvasWidthPx;
		double unitsPerPixelY = (topY - bottomY) / northUp.CanvasHeightPx;

		// The axis-aligned box enclosing a W x H rectangle turned by theta.
		double halfX = ((northUp.CanvasWidthPx * absCos) + (northUp.CanvasHeightPx * absSin))
			* degreesPerPixelX / 2;
		double halfY = ((northUp.CanvasWidthPx * absSin) + (northUp.CanvasHeightPx * absCos))
			* unitsPerPixelY / 2;

		double centreLon = (northUp.TopLeftLongitude + northUp.BottomRightLongitude) / 2;
		double centreY = (topY + bottomY) / 2;

		return northUp with
		{
			HeadingDeg = headingDeg,
			TopLeftLongitude = centreLon - halfX,
			BottomRightLongitude = centreLon + halfX,
			TopLeftLatitude = MapGeometry.InverseMercatorY(centreY + halfY),
			BottomRightLatitude = MapGeometry.InverseMercatorY(centreY - halfY),
		};
	}

	[Fact]
	public void ProjectToCanvas_PutsTheViewCentreAtTheCanvasCentre()
	{
		CanvasPoint centre = MapGeometry.ProjectToCanvas(UnitViewport, 0, 0);

		centre.X.ShouldBe(500, tolerance: 0.5);
		centre.Y.ShouldBe(500, tolerance: 0.5);
	}

	[Fact]
	public void ProjectToCanvas_PutsNorthAtTheTop_AndEastAtTheRight()
	{
		// The one that a flipped sign gets wrong, and the one that makes every marker land in
		// the wrong quadrant when it does.
		CanvasPoint northEast = MapGeometry.ProjectToCanvas(UnitViewport, 0.25, 0.25);

		northEast.X.ShouldBeGreaterThan(500, "east of centre draws right of centre.");
		northEast.Y.ShouldBeLessThan(500, "north of centre draws *above* centre — Y grows downward.");
	}

	[Fact]
	public void ProjectToCanvas_OnAnUnmeasuredCanvas_CollapsesToTheCentre()
	{
		MapViewport unmeasured = UnitViewport with
		{
			TopLeftLongitude = 0,
			BottomRightLongitude = 0,
		};

		CanvasPoint point = MapGeometry.ProjectToCanvas(unmeasured, 0.25, 0.25);

		point.X.ShouldBe(500);
		point.Y.ShouldBe(500);
	}

	[Theory]
	[InlineData(37)]
	[InlineData(90)]
	[InlineData(180)]
	public void ProjectToCanvas_RotatesAboutTheCanvasCentre(double headingDeg)
	{
		// A heading-up map turns the whole canvas about its middle, so the centre pixel is the
		// fixed point and distances from it are preserved. Both are what the tap hit test on
		// the live ride page leans on.
		MapViewport rotated = Turned(UnitViewport, headingDeg);

		CanvasPoint centre = MapGeometry.ProjectToCanvas(rotated, 0, 0);
		centre.X.ShouldBe(500, tolerance: 0.5);
		centre.Y.ShouldBe(500, tolerance: 0.5);

		double northUp = MapGeometry.ProjectToCanvas(UnitViewport, 0.25, 0.1)
			.DistanceTo(new CanvasPoint(500, 500));
		double turned = MapGeometry.ProjectToCanvas(rotated, 0.25, 0.1)
			.DistanceTo(new CanvasPoint(500, 500));

		turned.ShouldBe(northUp, tolerance: 1e-6,
			"rotation is an isometry — turning the map must not move a pin nearer to or " +
			"further from the middle of the screen.");
	}

	[Fact]
	public void ProjectToCanvas_OnAQuarterTurnedWideCanvas_KeepsAPinAtItsOwnDistanceFromTheCentre()
	{
		// The reported bug, at the bearing that showed it worst. A pin 200 px due north of the
		// centre on a north-up 800 x 400 canvas has to be 200 px to the *left* of the centre once
		// the map is turned to 90 degrees, because north now points left. Deriving the scale by
		// dividing the axis-aligned span by the canvas side puts it at 100 px instead — the
		// centre stays put, and the error grows with distance from it. At 0 and 180 the box is
		// the canvas and the arithmetic is accidentally right, which is why the rider saw it come
		// good again at half a turn.
		CanvasPoint northUp = MapGeometry.ProjectToCanvas(WideViewport, MapGeometry.InverseMercatorY(0.5), 0);
		northUp.X.ShouldBe(400, tolerance: 1e-6);
		northUp.Y.ShouldBe(0, tolerance: 1e-6);

		CanvasPoint turned = MapGeometry.ProjectToCanvas(
			Turned(WideViewport, 90), MapGeometry.InverseMercatorY(0.5), 0);

		turned.X.ShouldBe(200, tolerance: 1e-6, "a quarter turn clockwise carries north to the left.");
		turned.Y.ShouldBe(200, tolerance: 1e-6, "and leaves it level with the centre.");
	}

	[Theory]
	[InlineData(30)]
	[InlineData(90)]
	[InlineData(135)]
	[InlineData(180)]
	[InlineData(270)]
	public void ProjectToCanvas_OnAWideCanvas_IsRigidAtEveryBearing(double headingDeg)
	{
		// The general form of the case above: a turn is an isometry, so no bearing may move a pin
		// nearer to or further from the middle of the screen. Asserted on the oblong canvas
		// because the square one cannot see the failure.
		double northUp = MapGeometry.ProjectToCanvas(WideViewport, 0.3, 0.7)
			.DistanceTo(new CanvasPoint(400, 200));
		double turned = MapGeometry.ProjectToCanvas(Turned(WideViewport, headingDeg), 0.3, 0.7)
			.DistanceTo(new CanvasPoint(400, 200));

		turned.ShouldBe(northUp, tolerance: 1e-6);
	}

	[Fact]
	public void MetresToCanvasPixels_ScalesADistanceOnTheGroundIntoTheView()
	{
		// One degree of latitude across 1000 px, so a degree's worth of metres is the canvas.
		const double MetresPerDegree = Math.PI * DLR.Core.Tracks.Distance.EarthRadiusM / 180.0;

		MapGeometry.MetresToCanvasPixels(UnitViewport, 0, MetresPerDegree)
			.ShouldBe(1000, tolerance: 1,
				"the private-area circle is a radius on the ground (§10.1) — if this drifts, the " +
				"ring stops enclosing the area the setting actually suppresses.");

		MapGeometry.MetresToCanvasPixels(UnitViewport, 0, MetresPerDegree / 2)
			.ShouldBe(500, tolerance: 1);
	}

	[Theory]
	[InlineData(37)]
	[InlineData(90)]
	[InlineData(180)]
	public void MetresToCanvasPixels_IsUnchangedByAHeadingUpMap(double headingDeg)
	{
		// Rotation is rigid, so a radius is a radius however the map is turned. A circle that
		// grew when the rider rotated the map would be reporting a protected area they do not have.
		double northUp = MapGeometry.MetresToCanvasPixels(UnitViewport, 0, 5_000);
		double turned = MapGeometry.MetresToCanvasPixels(Turned(UnitViewport, headingDeg), 0, 5_000);

		turned.ShouldBe(northUp, tolerance: 1e-6);
	}

	[Fact]
	public void MetresToCanvasPixels_OnAnUnmeasuredCanvas_IsZero()
	{
		// Everything collapses to the canvas centre before there is a view, and a circle of
		// radius zero draws nothing rather than a dot of arbitrary size.
		MapViewport unmeasured = UnitViewport with { TopLeftLongitude = 0, BottomRightLongitude = 0 };

		MapGeometry.MetresToCanvasPixels(unmeasured, 0, 1_000).ShouldBe(0);
		MapGeometry.MetresToCanvasPixels(UnitViewport, 0, 0).ShouldBe(0);
		MapGeometry.MetresToCanvasPixels(UnitViewport, 0, double.NaN).ShouldBe(0);
	}

	[Fact]
	public void ScreenBearing_IsAlwaysAPositiveAngle()
	{
		// Skia's RotateDegrees copes with negatives, but a normalised value is what makes
		// the assertions above readable and keeps a caller from reasoning about -270.
		for (int bearing = 0; bearing < 360; bearing += 17)
		{
			for (int heading = 0; heading < 360; heading += 23)
			{
				double screen = MapGeometry.ScreenBearingDeg(bearing, heading);
				screen.ShouldBeGreaterThanOrEqualTo(0);
				screen.ShouldBeLessThan(360);
			}
		}
	}

	[Fact]
	public void ScreenBearing_HandlesAFixReportingThreeFiftyNine()
	{
		// A GPS heading is degrees clockwise from north and can sit just shy of a full turn.
		MapGeometry.ScreenBearingDeg(359, 0).ShouldBe(359, tolerance: 1e-9);
		MapGeometry.ScreenBearingDeg(359, 358).ShouldBe(1, tolerance: 1e-9);
	}

	[Fact]
	public void MercatorMidLatitude_IsTheMeanAtTheEquator()
	{
		// Symmetric about zero: projection distortion cancels, so the naive mean is right.
		MapGeometry.MercatorMidLatitude(10, -10).ShouldBe(0, tolerance: 1e-9);
	}

	[Fact]
	public void MercatorMidLatitude_DisagreesWithTheNaiveMean_AwayFromTheEquator()
	{
		// This is the whole reason the method exists. A tall view over Sydney: the pixel
		// halfway down the screen is NOT at the average of the two edge latitudes, because
		// Mercator stretches the poleward edge. Getting this wrong puts the marker composer's
		// opening camera off by kilometres.
		const double top = -30.0;
		const double bottom = -40.0;

		double centre = MapGeometry.MercatorMidLatitude(top, bottom);
		double naiveMean = (top + bottom) / 2;

		centre.ShouldBeInRange(bottom, top);
		Math.Abs(centre - naiveMean).ShouldBeGreaterThan(0.01,
			"south of the equator the projected midpoint sits measurably poleward of the plain average.");
	}

	[Fact]
	public void MercatorY_RoundTripsThroughItsInverse()
	{
		foreach (double latitude in new[] { -84.0, -45.0, -0.5, 0.0, 12.25, 51.5, 84.0 })
		{
			MapGeometry.InverseMercatorY(MapGeometry.MercatorY(latitude))
				.ShouldBe(latitude, tolerance: 1e-9);
		}
	}

	[Fact]
	public void MercatorY_ClampsAtThePole_RatherThanRunningToInfinity()
	{
		// Web Mercator is undefined at ±90; every slippy map stops at ~85.05. Without the
		// clamp this is Infinity, and a viewport containing it would poison the centre.
		double atLimit = MapGeometry.MercatorY(85.05112878);

		MapGeometry.MercatorY(90).ShouldBe(atLimit, tolerance: 1e-9);
		double.IsFinite(MapGeometry.MercatorY(-90)).ShouldBeTrue();
	}
}
