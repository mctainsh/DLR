using DLR.Core.Tracks;
using DLR.TestSupport.Tracks;

namespace DLR.Core.Tests.Tracks;

/// <summary>
/// Simplification is for drawing, and nothing else (§4.2, §15.5).
/// <para>
/// The editor addresses full-resolution indices throughout, and every derived number comes from
/// the raw points. If anything editable or countable ever pointed at the simplified copy it
/// would delete the wrong points or report a shorter ride — which is why these tests care as
/// much about what simplification must <em>not</em> touch.
/// </para>
/// </summary>
public sealed class TrackSimplifierTests
{
	[Fact]
	public void Simplify_StraightLine_KeepsOnlyTheEndpoints()
	{
		TrackGeometry straight = new([.. Enumerable.Range(0, 50).Select(index => Along(index, 0))]);

		TrackGeometry simplified = TrackSimplifier.Simplify(straight);

		simplified.Points.Count.ShouldBe(2, "every interior point is on the line between the ends");
		simplified.Points[0].ShouldBe(straight.Points[0]);
		simplified.Points[^1].ShouldBe(straight.Points[^1]);
	}

	[Fact]
	public void Simplify_DeviationBeyondTolerance_IsKept()
	{
		// A straight line with one point pushed 50 m sideways — a real corner, not noise.
		List<TrackPoint> points = [.. Enumerable.Range(0, 21).Select(index => Along(index, 0))];

		points[10] = Along(10, eastMetres: 50);

		TrackGeometry simplified = TrackSimplifier.Simplify(new TrackGeometry(points));

		simplified.Points.ShouldContain(
			points[10],
			"the corner is the whole shape of this track; dropping it would redraw the ride");

		// Not an exact count. Once the corner is kept, the line each half is measured against
		// runs to it rather than straight on, so points that were on the original line now
		// deviate from the new one and some are kept as well. That is Douglas–Peucker working,
		// and pinning the number would be pinning the algorithm's internals rather than the
		// property that matters.
		simplified.Points.Count.ShouldBeInRange(3, 8);
	}

	[Fact]
	public void Simplify_DeviationWithinTolerance_IsDropped()
	{
		List<TrackPoint> points = [.. Enumerable.Range(0, 21).Select(index => Along(index, 0))];

		// Two metres off the line — inside consumer GPS error, and invisible at any zoom that
		// shows a whole ride.
		points[10] = Along(10, eastMetres: 2);

		TrackSimplifier.Simplify(new TrackGeometry(points)).Points.Count.ShouldBe(2);
	}

	/// <summary>
	/// Joining two segments would draw a straight line across a tunnel, which is exactly what
	/// the break exists to prevent (§15.3).
	/// </summary>
	[Fact]
	public void Simplify_SegmentBreaks_Survive()
	{
		TrackGeometry original =
			GpxReader.Read(GpxFixtures.AsStream(GpxFixtures.TrackWithSegmentBreak(pointsPerSegment: 20)))
				.Tracks[0].Geometry;

		original.SegmentCount.ShouldBe(2);

		TrackGeometry simplified = TrackSimplifier.Simplify(original);

		simplified.SegmentCount.ShouldBe(2);
		simplified.Points.Count.ShouldBeLessThan(original.Points.Count);
	}

	/// <summary>
	/// The whole reason the editor never touches this copy. A shorter line is a different index
	/// space, and a browser deleting "the 412th point I am displaying" would cut somewhere else
	/// entirely (§15.5).
	/// </summary>
	[Fact]
	public void Simplify_DoesNotChangeTheRawTrackOrItsStats()
	{
		TrackGeometry original = Read(GpxFixtures.SingleTrack(points: 200, metresApart: 12));

		TrackStats before = TrackStats.From(original);

		TrackGeometry simplified = TrackSimplifier.Simplify(original);

		simplified.Points.Count.ShouldBeLessThan(original.Points.Count);

		original.Points.Count.ShouldBe(200);
		TrackStats.From(original).ShouldBe(before);
	}

	[Fact]
	public void Simplify_TwoPointsOrFewer_IsLeftAlone()
	{
		TrackGeometry pair = new([Along(0, 0), Along(1, 0)]);

		TrackSimplifier.Simplify(pair).Points.Count.ShouldBe(2);

		TrackGeometry single = new([Along(0, 0)]);

		TrackSimplifier.Simplify(single).Points.Count.ShouldBe(1);
	}

	[Fact]
	public void Simplify_LargerTolerance_KeepsFewerPoints()
	{
		TrackGeometry original = Read(GpxFixtures.SingleTrack(points: 300, metresApart: 8));

		int tight = TrackSimplifier.Simplify(original, toleranceM: 1).Points.Count;
		int loose = TrackSimplifier.Simplify(original, toleranceM: 50).Points.Count;

		loose.ShouldBeLessThanOrEqualTo(tight);
		loose.ShouldBeGreaterThanOrEqualTo(2, "the endpoints are never dropped");
	}

	/// <summary>
	/// A long dense track has to simplify without recursing to a depth that overflows the
	/// stack. Iterative rather than recursive is the reason, and this is what says so.
	/// </summary>
	[Fact]
	public void Simplify_LongTrack_DoesNotOverflowTheStack()
	{
		// A spiral: every point deviates, so nothing can be discarded early and the worst case
		// is a subdivision per point.
		List<TrackPoint> points = [];

		for (int index = 0; index < 40_000; index++)
		{
			double angle = index * 0.01;
			double radius = index * 0.05;

			points.Add(new TrackPoint(
				GpxFixtures.BaseLatitude + GpxFixtures.MetresToDegreesLatitude(radius * Math.Sin(angle)),
				GpxFixtures.BaseLongitude + GpxFixtures.MetresToDegreesLatitude(radius * Math.Cos(angle))));
		}

		TrackGeometry simplified = TrackSimplifier.Simplify(new TrackGeometry(points));

		simplified.Points.Count.ShouldBeGreaterThan(2);
		simplified.Points.Count.ShouldBeLessThan(points.Count);
	}

	private static TrackGeometry Read(string gpx) =>
		GpxReader.Read(GpxFixtures.AsStream(gpx)).Tracks[0].Geometry;

	private static TrackPoint Along(int index, double eastMetres) => new(
		GpxFixtures.BaseLatitude + (index * GpxFixtures.MetresToDegreesLatitude(20)),
		GpxFixtures.BaseLongitude + GpxFixtures.MetresToDegreesLatitude(eastMetres));
}
