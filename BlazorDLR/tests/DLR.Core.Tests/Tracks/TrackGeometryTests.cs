using DLR.Core.Tracks;

namespace DLR.Core.Tests.Tracks;

/// <summary>
/// §15.3's flat-list-with-segment-starts shape. The invariants that matter:
/// <list type="bullet">
///   <item>An implicit zero is added — a single-segment track can be built with an
///     empty starts list.</item>
///   <item>Out-of-range or duplicate starts are silently dropped rather than throwing —
///     an imported GPX with a stray track break must not crash the recorder.</item>
///   <item><c>Legs()</c> never yields a pair across a segment break — the whole reason
///     the shape exists (§15.3).</item>
/// </list>
/// </summary>
public sealed class TrackGeometryTests
{
	private static TrackPoint P(double lat) => new(lat, 0.0);

	[Fact]
	public void ImplicitZeroSegmentStart_IsAlwaysPresent_WhenPointsExist()
	{
		TrackGeometry g = new(new[] { P(0), P(1), P(2) });

		g.SegmentStarts.Count.ShouldBe(1);
		g.SegmentStarts[0].ShouldBe(0, "an implicit zero-index start is always added — a caller must not need to pass one.");
		g.SegmentCount.ShouldBe(1);
	}

	[Fact]
	public void EmptyPoints_HaveNoSegments_AndNoLegs()
	{
		TrackGeometry g = new(Array.Empty<TrackPoint>());

		g.SegmentCount.ShouldBe(0);
		g.SegmentStarts.ShouldBeEmpty();
		g.Legs().ShouldBeEmpty();
	}

	[Fact]
	public void OutOfRangeStarts_AreDropped()
	{
		// Points 0..2; a start at index 5 (past the end) and a start at 0 (redundant) must
		// both be dropped without complaint.
		TrackGeometry g = new(new[] { P(0), P(1), P(2) }, segmentStarts: new[] { 5, 0, 1 });

		g.SegmentCount.ShouldBe(2, "the redundant 0 and out-of-range 5 are dropped; only index 1 remains as an explicit start.");
		g.SegmentStarts[0].ShouldBe(0);
		g.SegmentStarts[1].ShouldBe(1);
	}

	[Fact]
	public void DuplicateStarts_AreDeduplicated()
	{
		TrackGeometry g = new(new[] { P(0), P(1), P(2), P(3) }, segmentStarts: new[] { 2, 2, 2 });

		g.SegmentCount.ShouldBe(2, "duplicate explicit starts collapse to one — a broken exporter must not create N segments per pause.");
	}

	[Fact]
	public void StartsAreSorted_EvenIfCallerPassesUnordered()
	{
		TrackGeometry g = new(new[] { P(0), P(1), P(2), P(3) }, segmentStarts: new[] { 3, 1 });

		g.SegmentStarts.SequenceEqual(new[] { 0, 1, 3 }).ShouldBeTrue(
			"the segment-starts list is sorted so downstream code (Legs, editor) can rely on ordered indices.");
	}

	[Fact]
	public void Legs_YieldsConsecutivePairsWithinASegmentOnly()
	{
		TrackGeometry g = new(new[] { P(0), P(1), P(2), P(3) }, segmentStarts: new[] { 2 });

		(TrackPoint From, TrackPoint To)[] legs = g.Legs().ToArray();

		legs.Length.ShouldBe(2,
			"§15.3: two legs — (0→1) inside segment 1, (2→3) inside segment 2. The pair (1→2) crosses the break and must not be yielded.");
		legs[0].From.Latitude.ShouldBe(0);
		legs[0].To.Latitude.ShouldBe(1);
		legs[1].From.Latitude.ShouldBe(2);
		legs[1].To.Latitude.ShouldBe(3);
	}

	[Fact]
	public void Legs_EmptyGeometry_YieldsNothing()
	{
		TrackGeometry g = new(Array.Empty<TrackPoint>());

		g.Legs().ShouldBeEmpty();
	}

	[Fact]
	public void SinglePoint_ProducesNoLegs()
	{
		TrackGeometry g = new(new[] { P(0) });

		g.SegmentCount.ShouldBe(1, "one point is still one segment — the zero-index start is present.");
		g.Legs().ShouldBeEmpty("no leg exists in a one-point segment.");
	}
}
