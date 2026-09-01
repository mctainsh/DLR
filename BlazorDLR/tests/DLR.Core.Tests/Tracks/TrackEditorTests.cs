using DLR.Core.Tracks;
using DLR.TestSupport.Tracks;

namespace DLR.Core.Tests.Tracks;

/// <summary>
/// One primitive, three gestures (§15.5). Trim the start, trim the end, cut something out of the
/// middle - all of them remove a half-open range of raw point indices.
/// </summary>
public sealed class TrackEditorTests
{
	/// <summary>
	/// First for a reason (§15.7). An edit that removes nothing must reproduce the stored
	/// numbers <em>exactly</em>. If a rewrite shifts ascent by a metre on an untouched track,
	/// the algorithm is order-dependent or accumulating error - and the person it looks wrong
	/// to is the one who rode it.
	/// </summary>
	[Fact]
	public void Edit_NoOpEdit_ProducesIdenticalStats()
	{
		TrackGeometry original = Read(GpxFixtures.SingleTrack(points: 200, metresApart: 12));

		TrackStats before = TrackStats.From(original);

		TrackGeometry after = TrackEditor.Remove(original, []).Result;

		after.Points.ShouldBe(original.Points);
		after.SegmentCount.ShouldBe(original.SegmentCount);

		TrackEditor.Restat(after).ShouldBe(
			before,
			"an edit that removed nothing changed a number, so the calculator is not a pure " +
			"function of the points it was given");
	}

	/// <summary>
	/// The same guarantee under repetition, because accumulating error is invisible in one
	/// pass. This is what "recomputed, not adjusted" buys (§15.5).
	/// </summary>
	[Fact]
	public void Edit_RepeatedNoOpEdits_NeverDrift()
	{
		TrackGeometry geometry = Read(GpxFixtures.SingleTrack(points: 120));

		TrackStats first = TrackStats.From(geometry);

		for (int pass = 0; pass < 5; pass++)
		{
			geometry = TrackEditor.Remove(geometry, []).Result;
		}

		TrackEditor.Restat(geometry).ShouldBe(first);
	}

	[Fact]
	public void Edit_TrimStart_RemovesLeadingPointsAndRecomputesStats()
	{
		TrackGeometry original = Read(GpxFixtures.SingleTrack(points: 50, secondsApart: 10));

		TrackStats before = TrackStats.From(original);

		TrackGeometry trimmed = TrackEditor.Remove(original, [new PointRange(0, 10)]).Result;

		trimmed.Points.Count.ShouldBe(40);
		trimmed.Points[0].ShouldBe(original.Points[10]);

		trimmed.SegmentCount.ShouldBe(1,
			"there is nothing outside the start to disconnect from, so trimming makes no break");

		TrackStats after = TrackEditor.Restat(trimmed);

		after.DistanceM.ShouldBeLessThan(before.DistanceM);
		after.StartedUtc.ShouldBe(original.Points[10].TimeUtc);
		after.DurationS.ShouldBe(390, "39 legs of ten seconds");
	}

	[Fact]
	public void Edit_TrimEnd_RemovesTrailingPoints()
	{
		TrackGeometry original = Read(GpxFixtures.SingleTrack(points: 50, secondsApart: 10));

		TrackGeometry trimmed = TrackEditor.Remove(original, [new PointRange(40, 50)]).Result;

		trimmed.Points.Count.ShouldBe(40);
		trimmed.Points[^1].ShouldBe(original.Points[39]);
		trimmed.SegmentCount.ShouldBe(1);

		TrackEditor.Restat(trimmed).EndedUtc.ShouldBe(original.Points[39].TimeUtc);
	}

	/// <summary>
	/// Splicing the neighbours together would draw a straight line across the gap and add its
	/// length to the distance, inventing a path the rider never took. So the removed span
	/// leaves a genuine discontinuity (§15.5) - the same mechanism as a multi-<c>trkseg</c>
	/// import, which is a sign the concept is right rather than bolted on.
	/// </summary>
	[Fact]
	public void Edit_RemoveInteriorRange_InsertsSegmentBreak()
	{
		TrackGeometry original = Read(GpxFixtures.SingleTrack(points: 50));

		TrackGeometry edited = TrackEditor.Remove(original, [new PointRange(20, 30)]).Result;

		edited.Points.Count.ShouldBe(40);
		edited.SegmentCount.ShouldBe(2);
		edited.SegmentStarts.ShouldBe([0, 20]);

		edited.Points[19].ShouldBe(original.Points[19]);
		edited.Points[20].ShouldBe(original.Points[30]);
	}

	[Fact]
	public void Edit_SeveralInteriorRanges_EachInsertsItsOwnBreak()
	{
		TrackGeometry original = Read(GpxFixtures.SingleTrack(points: 100));

		TrackGeometry edited = TrackEditor.Remove(
			original,
			[new PointRange(10, 15), new PointRange(40, 50), new PointRange(80, 82)]).Result;

		edited.Points.Count.ShouldBe(100 - 5 - 10 - 2);
		edited.SegmentCount.ShouldBe(4, "three interior cuts make three breaks");
	}

	/// <summary>
	/// The gap is a discontinuity, not a shortcut. Distance and duration are summed within
	/// segments only, so the removed span contributes neither - and neither does the jump
	/// across it.
	/// </summary>
	[Fact]
	public void Edit_RemovedSpan_IsExcludedFromDistanceAndDuration()
	{
		TrackGeometry original = Read(
			GpxFixtures.SingleTrack(points: 61, metresApart: 20, secondsApart: 10));

		TrackStats before = TrackStats.From(original);

		before.DurationS.ShouldBe(600, "60 legs of ten seconds");

		// Twenty points out of the middle: nineteen interior legs, plus the leg that used to
		// join point 20 to point 21 and the one joining 40 to 41 - twenty-one legs in all.
		TrackGeometry edited = TrackEditor.Remove(original, [new PointRange(21, 41)]).Result;

		TrackStats after = TrackEditor.Restat(edited);

		after.DurationS.ShouldBe(
			390,
			"the removed span and the gap across it are both gone: 39 legs of ten seconds");

		after.DistanceM.ShouldBeLessThan(before.DistanceM * 0.7);

		// The end timestamps are unchanged - the ride still finished when it finished. Duration
		// is not end-minus-start on a track with a break in it, and conflating them is how a
		// trimmed lunch stop reappears in the total.
		after.StartedUtc.ShouldBe(before.StartedUtc);
		after.EndedUtc.ShouldBe(before.EndedUtc);
	}

	/// <summary>
	/// The same threshold as import and record, unchanged (§15.7). An edited track whose
	/// untouched half reports different climbing reads as data corruption to its owner.
	/// </summary>
	[Fact]
	public void Edit_RecomputedAscent_UsesRecorderThreshold()
	{
		// Ten points of altitude wander - a rider standing at the lights while GPS altitude
		// oscillates by a metre - then a genuine twenty-metre climb.
		List<TrackPoint> points = [];

		for (int index = 0; index < 10; index++)
		{
			points.Add(Point(index, elevation: index % 2 == 0 ? 100 : 101));
		}

		for (int index = 10; index < 20; index++)
		{
			points.Add(Point(index, elevation: 100 + ((index - 9) * 2)));
		}

		TrackGeometry original = new(points);

		TrackStats before = TrackStats.From(original);

		before.AscentM!.Value.ShouldBe(
			20,
			tolerance: 0.001,
			"the climb counts and the wander does not - without a threshold this reads 25, and " +
			"a parked bike accrues ascent for as long as it is parked");

		// Trim the climb. What survives is the wander, whose ascent must be exactly what those
		// same points report on their own (§15.7).
		TrackGeometry head = TrackEditor.Remove(original, [new PointRange(10, 20)]).Result;

		TrackStats headOnly = TrackStats.From(new TrackGeometry([.. points.Take(10)]));

		headOnly.AscentM!.Value.ShouldBe(0, tolerance: 0.001);

		TrackEditor.Restat(head).AscentM.ShouldBe(headOnly.AscentM);
	}

	/// <summary>
	/// The other half of the threshold, and the reason it tracks a running reference rather than
	/// consecutive points: a long steady climb made of small steps is still a climb.
	/// </summary>
	[Fact]
	public void Ascent_SmallStepsThatAddUp_AreCounted()
	{
		List<TrackPoint> points =
			[.. Enumerable.Range(0, 21).Select(index => Point(index, elevation: 100 + index))];

		TrackStats.From(new TrackGeometry(points)).AscentM!.Value.ShouldBe(
			18,
			tolerance: 0.001,
			"twenty one-metre steps, counted in sixes as each one clears the threshold - a " +
			"per-point comparison would report none of it");
	}

	[Theory]
	[InlineData(30, 20, "descending")]
	[InlineData(0, 0, "empty")]
	[InlineData(10, 10, "empty at a non-zero index")]
	public void Edit_OverlappingOrDescendingRanges_AreRefused(int from, int to, string why)
	{
		TrackGeometry geometry = Read(GpxFixtures.SingleTrack(points: 50));

		TrackEditResult result = TrackEditor.Remove(geometry, [new PointRange(from, to)]);

		result.IsValid.ShouldBeFalse(why);
		result.Message.ShouldNotBeNullOrWhiteSpace();
	}

	[Fact]
	public void Edit_OverlappingRanges_AreRefusedAndNameTheOffender()
	{
		TrackGeometry geometry = Read(GpxFixtures.SingleTrack(points: 50));

		TrackEditResult result = TrackEditor.Remove(
			geometry,
			[new PointRange(10, 25), new PointRange(20, 30)]);

		result.Error.ShouldBe(TrackEditError.OverlappingOrDescending);

		result.Message.ShouldNotBeNull();
		result.Message!.ShouldContain("[20, 30)");
		result.Message.ShouldContain("[10, 25)");
	}

	[Fact]
	public void Edit_RangesOutOfOrder_AreRefused()
	{
		TrackGeometry geometry = Read(GpxFixtures.SingleTrack(points: 50));

		TrackEditor
			.Remove(geometry, [new PointRange(30, 40), new PointRange(5, 10)])
			.Error.ShouldBe(TrackEditError.OverlappingOrDescending);
	}

	[Theory]
	[InlineData(-1, 10)]
	[InlineData(0, 51)]
	[InlineData(45, 60)]
	public void Edit_RangeOutOfBounds_IsRefused(int from, int to)
	{
		TrackGeometry geometry = Read(GpxFixtures.SingleTrack(points: 50));

		TrackEditResult result = TrackEditor.Remove(geometry, [new PointRange(from, to)]);

		result.Error.ShouldBe(TrackEditError.OutOfBounds);
		result.Message.ShouldNotBeNull();
		result.Message!.ShouldContain("50 points");
	}

	[Theory]
	[InlineData(0, 50, "everything")]
	[InlineData(0, 49, "all but one")]
	public void Edit_LeavingFewerThanTwoPoints_IsRefused(int from, int to, string why)
	{
		TrackGeometry geometry = Read(GpxFixtures.SingleTrack(points: 50));

		TrackEditResult result = TrackEditor.Remove(geometry, [new PointRange(from, to)]);

		result.Error.ShouldBe(TrackEditError.TooFewPointsRemain, why);

		result.Message.ShouldNotBeNull();
		// Below two points it is not a line, and the caller needs to know what to do instead.
		result.Message!.ShouldContain("delete the track");
	}

	[Fact]
	public void Edit_RefusedEdit_ChangesNothing()
	{
		TrackGeometry original = Read(GpxFixtures.SingleTrack(points: 50));

		TrackStats before = TrackStats.From(original);

		TrackEditor.Remove(original, [new PointRange(0, 50)]).IsValid.ShouldBeFalse();

		TrackStats.From(original).ShouldBe(before);
		original.Points.Count.ShouldBe(50);
	}

	/// <summary>
	/// A track that already has breaks keeps them where they were, with the cut's own break
	/// added. Two mechanisms, one representation (§15.3, §15.5).
	/// </summary>
	[Fact]
	public void Edit_TrackWithExistingSegments_KeepsThemAndAddsTheCut()
	{
		TrackGeometry original = Read(GpxFixtures.TrackWithSegmentBreak(pointsPerSegment: 10));

		original.SegmentCount.ShouldBe(2);
		original.SegmentStarts.ShouldBe([0, 10]);

		// A cut inside the second segment.
		TrackGeometry edited = TrackEditor.Remove(original, [new PointRange(13, 16)]).Result;

		edited.Points.Count.ShouldBe(17);
		edited.SegmentCount.ShouldBe(3);
		edited.SegmentStarts.ShouldBe([0, 10, 13]);
	}

	private static TrackGeometry Read(string gpx) =>
		GpxReader.Read(GpxFixtures.AsStream(gpx)).Tracks[0].Geometry;

	private static TrackPoint Point(int index, double elevation) => new(
		GpxFixtures.BaseLatitude + (index * GpxFixtures.MetresToDegreesLatitude(20)),
		GpxFixtures.BaseLongitude,
		elevation,
		GpxFixtures.Start.AddSeconds(index * 10));
}
