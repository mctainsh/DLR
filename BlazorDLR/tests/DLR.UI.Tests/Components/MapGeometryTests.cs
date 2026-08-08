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
		MapViewport rotated = UnitViewport with { HeadingDeg = headingDeg };

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
