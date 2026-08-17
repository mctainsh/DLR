using DLR.Core.Tracks;

namespace DLR.Core.Tests.Tracks;

/// <summary>
/// §15.7's numbers. One computation used by the recorder, the importer and the editor,
/// so every property has to be pinned by test — three copies that agree today is not the
/// same guarantee as one that always agrees.
/// </summary>
public sealed class TrackStatsTests
{
	private static readonly DateTimeOffset T0 = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	[Fact]
	public void EmptyGeometry_IsAllZeroAndNulls()
	{
		TrackStats stats = TrackStats.From(new TrackGeometry(Array.Empty<TrackPoint>()));

		stats.DistanceM.ShouldBe(0);
		stats.AscentM.ShouldBeNull();
		stats.DurationS.ShouldBeNull();
		stats.MaxSpeedMps.ShouldBeNull();
		stats.StartedUtc.ShouldBeNull();
		stats.EndedUtc.ShouldBeNull();
		stats.PointCount.ShouldBe(0);
		stats.SegmentCount.ShouldBe(0);
		stats.Bounds.ShouldBeNull();
	}

	[Fact]
	public void Distance_SumsHaversineLegs_WithinASegment()
	{
		// Two 1-degree steps east near the equator. Approximate distance per degree is 111 km.
		TrackPoint a = new(0.0, 0.0);
		TrackPoint b = new(0.0, 1.0);
		TrackPoint c = new(0.0, 2.0);

		TrackStats stats = TrackStats.From(new TrackGeometry(new[] { a, b, c }));

		// Two legs of ~111 km each, so total ~222 km. Assert within 1% tolerance.
		double km = stats.DistanceM / 1000d;
		km.ShouldBeGreaterThan(220);
		km.ShouldBeLessThan(224);
	}

	[Fact]
	public void Distance_DoesNotSpanSegmentBreak()
	{
		// Same three points, but the third is a new segment (the middle-of-nowhere jump).
		TrackPoint a = new(0.0, 0.0);
		TrackPoint b = new(0.0, 1.0);
		TrackPoint c = new(0.0, 2.0);

		TrackGeometry oneSegment = new(new[] { a, b, c });
		TrackGeometry twoSegments = new(new[] { a, b, c }, segmentStarts: new[] { 2 });

		double whole = TrackStats.From(oneSegment).DistanceM;
		double broken = TrackStats.From(twoSegments).DistanceM;

		// The second geometry loses the b→c leg (crosses the segment break), so its distance
		// is about half the whole. This is §15.3's "geometry wins over gaps".
		broken.ShouldBeLessThan(whole);
		(whole - broken).ShouldBeGreaterThan(100_000, "the b→c leg is ~111 km and must not be counted across the pause.");
	}

	[Fact]
	public void Ascent_IgnoresBelowNoiseThreshold_CountsAboveIt()
	{
		// Noise threshold is 3 m. A rise of 2 m then a rise of 5 m should count only the 5 m
		// (against the running reference).
		TrackPoint a = new(0.0, 0.0, TimeUtc: null, ElevationM: 100);
		TrackPoint b = new(0.0001, 0.0, TimeUtc: null, ElevationM: 102); // +2m — noise
		TrackPoint c = new(0.0002, 0.0, TimeUtc: null, ElevationM: 107); // +5m from ref → counts

		TrackStats stats = TrackStats.From(new TrackGeometry(new[] { a, b, c }));

		stats.AscentM.ShouldNotBeNull();
		stats.AscentM!.Value.ShouldBeInRange(6.5, 7.5,
			"§15.7: the 2 m step is below the 3 m noise threshold; the 5 m step from the same reference lands as 7 m against the original.");
	}

	[Fact]
	public void Ascent_IsNull_WhenNoPointHasElevation()
	{
		TrackPoint a = new(0.0, 0.0);
		TrackPoint b = new(0.0001, 0.0);

		TrackStats stats = TrackStats.From(new TrackGeometry(new[] { a, b }));

		stats.AscentM.ShouldBeNull(
			"§15.7: no elevation on the file means null ascent — inventing zero would claim a measurement nobody took.");
	}

	[Fact]
	public void DurationAndMaxSpeed_AreNull_WhenAnyPointLacksATime()
	{
		TrackPoint a = new(0.0, 0.0, TimeUtc: T0);
		TrackPoint b = new(0.0, 0.001, TimeUtc: null);

		TrackStats stats = TrackStats.From(new TrackGeometry(new[] { a, b }));

		stats.DurationS.ShouldBeNull();
		stats.MaxSpeedMps.ShouldBeNull();
		stats.StartedUtc.ShouldBeNull();
		stats.EndedUtc.ShouldBeNull();
	}

	[Fact]
	public void DurationAndMaxSpeed_AreNull_OnNonMonotonicTimestamps()
	{
		TrackPoint a = new(0.0, 0.0, TimeUtc: T0);
		TrackPoint b = new(0.0, 0.001, TimeUtc: T0.AddSeconds(-1)); // goes backwards

		TrackStats stats = TrackStats.From(new TrackGeometry(new[] { a, b }));

		stats.DurationS.ShouldBeNull(
			"§15.3: non-monotonic clocks drop stats rather than reorder — geometry wins over the clock.");
	}

	[Fact]
	public void Duration_SumsPerSegment_SkippingThePause()
	{
		TrackPoint a = new(0.0, 0.000, TimeUtc: T0);
		TrackPoint b = new(0.0, 0.001, TimeUtc: T0.AddSeconds(10));
		TrackPoint c = new(0.0, 0.002, TimeUtc: T0.AddMinutes(30)); // a long pause between b and c
		TrackPoint d = new(0.0, 0.003, TimeUtc: T0.AddMinutes(30).AddSeconds(10));

		// Break the geometry at c so the 30-minute pause is not counted.
		TrackStats stats = TrackStats.From(new TrackGeometry(new[] { a, b, c, d }, segmentStarts: new[] { 2 }));

		stats.DurationS.ShouldNotBeNull();
		stats.DurationS!.Value.ShouldBe(20d,
			"§15.7: duration is per-segment. Two 10-second legs = 20 s — the pause between segments is not travelling time.");
	}

	[Fact]
	public void Bounds_AreTheMinMaxOfPoints()
	{
		TrackPoint a = new(1.0, -2.0);
		TrackPoint b = new(3.0, 4.0);
		TrackPoint c = new(-1.0, 0.0);

		TrackStats stats = TrackStats.From(new TrackGeometry(new[] { a, b, c }));

		stats.Bounds.ShouldNotBeNull();
		stats.Bounds!.Value.MinLatitude.ShouldBe(-1.0);
		stats.Bounds.Value.MaxLatitude.ShouldBe(3.0);
		stats.Bounds.Value.MinLongitude.ShouldBe(-2.0);
		stats.Bounds.Value.MaxLongitude.ShouldBe(4.0);
	}

	[Fact]
	public void SegmentCount_ReflectsExplicitStartsPlusImplicitZero()
	{
		TrackPoint[] points = new[]
		{
			new TrackPoint(0.0, 0.0),
			new TrackPoint(0.0, 0.001),
			new TrackPoint(0.0, 0.002),
			new TrackPoint(0.0, 0.003),
		};

		TrackStats singleSegment = TrackStats.From(new TrackGeometry(points));
		TrackStats twoSegments = TrackStats.From(new TrackGeometry(points, segmentStarts: new[] { 2 }));

		singleSegment.SegmentCount.ShouldBe(1);
		twoSegments.SegmentCount.ShouldBe(2, "the explicit start at index 2 adds a second segment.");
	}
}
