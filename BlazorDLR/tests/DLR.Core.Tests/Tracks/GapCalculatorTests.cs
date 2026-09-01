using DLR.Core.Tracks;

namespace DLR.Core.Tests.Tracks;

/// <summary>
/// The gap-along-route / off-route projection is the whole basis of §5.4's gap list.
/// A silent regression here would be a live-ride screen quietly reporting wrong
/// distances; the values below are conservative - the projection is metres-per-degree
/// flat, so tolerances allow for that rather than being tight enough to catch a real
/// bug in the wrong place.
/// </summary>
public sealed class GapCalculatorTests
{
	/// <summary>Roughly one metre in degrees, at the equator.</summary>
	private const double OneMetreDeg = 1.0 / 111_320.0;

	[Fact]
	public void Project_EmptyRoute_ReturnsNull()
	{
		GapCalculator.Project(Array.Empty<TrackPoint>(), new TrackPoint(0, 0))
			.ShouldBeNull();
	}

	[Fact]
	public void Project_OnePointRoute_ReturnsNull()
	{
		TrackPoint[] one = [new(0, 0)];
		GapCalculator.Project(one, new TrackPoint(0, 0))
			.ShouldBeNull("a single point is not a route to project against");
	}

	/// <summary>
	/// A due-east straight line: a point directly on it has zero off-route, and the
	/// distance-along matches the point's easting.
	/// </summary>
	[Fact]
	public void Project_PointOnStraightRoute_AlongMatchesEasting_OffIsZero()
	{
		TrackPoint start = new(0, 0);
		TrackPoint end = new(0, OneMetreDeg * 100);
		TrackPoint[] route = [start, end];

		RouteProjection? result = GapCalculator.Project(route, new TrackPoint(0, OneMetreDeg * 50));

		result.ShouldNotBeNull();
		result!.Value.OffMetres.ShouldBeLessThan(0.5);
		result.Value.AlongMetres.ShouldBeInRange(49, 51);
	}

	/// <summary>
	/// A point past the far end snaps to the last vertex - its distance-along is the
	/// route's full length. §5.4's "who is furthest along" would otherwise report a
	/// runaway rider as somewhere off the map.
	/// </summary>
	[Fact]
	public void Project_PointPastRouteEnd_SnapsToLastVertex()
	{
		TrackPoint start = new(0, 0);
		TrackPoint end = new(0, OneMetreDeg * 100);
		TrackPoint[] route = [start, end];

		RouteProjection? result = GapCalculator.Project(route, new TrackPoint(0, OneMetreDeg * 200));

		result.ShouldNotBeNull();
		// Snapped to the end: along ≈ route length, and off ≈ 100 metres past the end.
		result!.Value.AlongMetres.ShouldBeInRange(99, 101);
		result.Value.OffMetres.ShouldBeInRange(95, 105);
	}

	/// <summary>
	/// A point off the line at right angles reports off-route accurately.
	/// </summary>
	[Fact]
	public void Project_PointBesideRoute_OffMetresMatchesPerpendicularDistance()
	{
		TrackPoint start = new(0, 0);
		TrackPoint end = new(0, OneMetreDeg * 100);
		TrackPoint[] route = [start, end];

		// 50 metres east, but also 20 metres north - perpendicular to the west-east line.
		RouteProjection? result = GapCalculator.Project(
			route,
			new TrackPoint(OneMetreDeg * 20 * 111_320.0 / 111_132.0, OneMetreDeg * 50));

		result.ShouldNotBeNull();
		result!.Value.OffMetres.ShouldBeInRange(18, 22);
	}

	/// <summary>
	/// A right-angle route: a point next to the second leg projects onto that leg, and
	/// the distance-along counts the first full leg plus the projected fraction.
	/// </summary>
	[Fact]
	public void Project_KinkedRoute_ProjectsOntoNearestLeg()
	{
		// Leg 1: east 100 m. Leg 2: north 100 m from the corner.
		TrackPoint a = new(0, 0);
		TrackPoint corner = new(0, OneMetreDeg * 100);
		TrackPoint c = new(OneMetreDeg * 100 * 111_320.0 / 111_132.0, OneMetreDeg * 100);
		TrackPoint[] route = [a, corner, c];

		// A point 50 metres up leg 2, and 10 metres east of it.
		TrackPoint point = new(
			OneMetreDeg * 50 * 111_320.0 / 111_132.0,
			OneMetreDeg * 100 + (OneMetreDeg * 10));

		RouteProjection? result = GapCalculator.Project(route, point);

		result.ShouldNotBeNull();
		// Along ≈ 100 m (leg 1) + 50 m (halfway up leg 2) = 150 m.
		result!.Value.AlongMetres.ShouldBeInRange(140, 160);
		result.Value.OffMetres.ShouldBeInRange(8, 12);
	}

	/// <summary>
	/// The gap between two projections is the signed difference of their distance-along
	/// values. A positive gap means the second rider is ahead of the first.
	/// </summary>
	[Fact]
	public void GapMetres_ReturnsSignedDifference()
	{
		RouteProjection behind = new(AlongMetres: 100, OffMetres: 5);
		RouteProjection ahead = new(AlongMetres: 250, OffMetres: 5);

		GapCalculator.GapMetres(behind, ahead).ShouldBe(150);
		GapCalculator.GapMetres(ahead, behind).ShouldBe(-150);
	}

	/// <summary>
	/// A route with a duplicated point (a zero-length leg) does not crash and produces
	/// the same projection it would without the duplicate. Some GPX exporters emit
	/// these; skipping them silently is the only correct behaviour.
	/// </summary>
	[Fact]
	public void Project_RouteWithDuplicatePoints_HandlesGracefully()
	{
		TrackPoint[] route =
		[
			new(0, 0),
			new(0, 0), // duplicate
			new(0, OneMetreDeg * 100),
		];

		RouteProjection? result = GapCalculator.Project(route, new TrackPoint(0, OneMetreDeg * 50));

		result.ShouldNotBeNull();
		result!.Value.OffMetres.ShouldBeLessThan(0.5);
		result.Value.AlongMetres.ShouldBeInRange(49, 51);
	}
}
