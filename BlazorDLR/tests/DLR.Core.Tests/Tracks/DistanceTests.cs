using DLR.Core.Tracks;

namespace DLR.Core.Tests.Tracks;

/// <summary>
/// The great-circle distance used everywhere in stats and the gap list. Haversine
/// is not a routing library - it computes the straight-line arc on a sphere - but
/// several rides' worth of legs (and every "off route" call) depend on it being
/// symmetric and non-negative.
/// </summary>
public sealed class DistanceTests
{
	[Fact]
	public void SamePoint_IsZero()
	{
		TrackPoint p = new(-33.868, 151.209);
		Distance.BetweenM(p, p).ShouldBe(0, tolerance: 0.001);
	}

	[Fact]
	public void OneDegreeOfLatitude_IsAboutOneHundredEleven_Km()
	{
		// A degree of latitude is 60 nautical miles ≈ 111.2 km regardless of longitude.
		double metres = Distance.BetweenM(new TrackPoint(0.0, 0.0), new TrackPoint(1.0, 0.0));

		metres.ShouldBeInRange(110_000, 112_000,
			"one degree of latitude ≈ 111 km - the haversine implementation must produce this to be usable for gap-list distances.");
	}

	[Fact]
	public void Symmetric_A_To_B_Equals_B_To_A()
	{
		TrackPoint a = new(-33.868, 151.209);
		TrackPoint b = new(-33.900, 151.250);

		double ab = Distance.BetweenM(a, b);
		double ba = Distance.BetweenM(b, a);

		ab.ShouldBe(ba, tolerance: 1e-6,
			"the leg from A to B must be identical to the leg from B to A - an asymmetric distance would make the gap list flicker.");
	}

	[Fact]
	public void Antipode_IsAboutHalfTheCircumference()
	{
		// (0,0) and (0,180) are antipodal along the equator; distance ≈ π · R ≈ 20 015 km.
		double metres = Distance.BetweenM(new TrackPoint(0.0, 0.0), new TrackPoint(0.0, 180.0));

		metres.ShouldBeInRange(19_900_000, 20_100_000,
			"antipodal points along the equator are half the earth's circumference apart - sanity check on the arc formula.");
	}

	[Fact]
	public void CrossingTheDateLine_IsShortNotAlmostCircumference()
	{
		// A tenth of a degree just west and just east of the date line is a short distance,
		// not one that goes the long way round.
		TrackPoint west = new(0.0, 179.95);
		TrackPoint east = new(0.0, -179.95);

		double metres = Distance.BetweenM(west, east);

		metres.ShouldBeLessThan(15_000,
			"haversine picks the great-circle short way across the date line - an implementation that summed longitude naively would report ~40 000 km.");
	}
}

/// <summary>
/// §15.5's raw-index span. Tiny value type but the whole editor speaks in these.
/// </summary>
public sealed class PointRangeTests
{
	[Fact]
	public void Length_IsToMinusFrom()
	{
		new PointRange(10, 20).Length.ShouldBe(10);
	}

	[Fact]
	public void IsEmpty_WhenToEqualsFrom()
	{
		new PointRange(5, 5).IsEmpty.ShouldBeTrue(
			"a zero-length span is empty - the editor uses this to skip no-op removals.");
	}

	[Fact]
	public void IsEmpty_WhenToLessThanFrom()
	{
		new PointRange(10, 5).IsEmpty.ShouldBeTrue(
			"a reversed range is empty rather than negative-length - the editor must not compute Length as -5.");
	}

	[Fact]
	public void Contains_HalfOpen_ExcludesToBoundary()
	{
		PointRange range = new(10, 20);

		range.Contains(10).ShouldBeTrue("From is inclusive.");
		range.Contains(19).ShouldBeTrue("the last included index is To - 1.");
		range.Contains(20).ShouldBeFalse("To is exclusive - the point AT To is the first one KEPT.");
		range.Contains(9).ShouldBeFalse("below the span");
	}

	[Fact]
	public void ToString_FormatsAsHalfOpen()
	{
		new PointRange(3, 7).ToString().ShouldBe("[3, 7)");
	}
}
