using BlazorDLR.Shared.Services;
using DLR.Core.Tracks;

namespace DLR.UI.Tests.Services;

/// <summary>
/// The rules a track is kept by (§15.1, §15.3) and the two things a device does with one it has
/// not saved yet: write it down so a relaunch still has it, and cut the private area out of it on
/// the way to the server (§10.1).
/// <para>
/// Pure — no device store, no clock, no network. Everything here is a decision about a list of
/// points, which is exactly the part that must be right before the phone is involved.
/// </para>
/// </summary>
public sealed class TrackRecordingTests
{
	private static readonly DateTimeOffset Start = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	private const double Latitude = -33.868;
	private const double Longitude = 151.209;

	/// <summary>Moves a point north by roughly <paramref name="metres"/>.</summary>
	private static double NorthOf(double metres) => Latitude + (metres / 111_320d);

	private static LocationFix Fix(double latitude = Latitude, double longitude = Longitude, int secondsIn = 0) =>
		new(latitude, longitude, 5, 12.5, 90, Start.AddSeconds(secondsIn));

	private static TrackPoint Point(double latitude, int secondsIn) =>
		new(latitude, Longitude, null, Start.AddSeconds(secondsIn));

	[Fact]
	public void TheFirstFix_StartsASegment()
	{
		TrackRecording.Decide(null, Fix(), 10).ShouldBe(RecordDecision.StartSegment);
	}

	[Fact]
	public void AFixInsideTheInterval_IsDropped()
	{
		// The whole reason an interval exists: a receiver keeps producing fixes whether or not the
		// bike moved, and a track made of them is a day of standing still at a set of lights.
		TrackRecording.Decide(Point(Latitude, 0), Fix(secondsIn: 1), 10)
			.ShouldBe(RecordDecision.Drop);
	}

	[Fact]
	public void AFixPastTheInterval_IsAppended()
	{
		TrackRecording.Decide(Point(Latitude, 0), Fix(latitude: NorthOf(12), secondsIn: 1), 10)
			.ShouldBe(RecordDecision.Append);
	}

	[Fact]
	public void TheIntervalIsTheRidersOwn_NotTheAccuracyProfiles()
	{
		// The point of separating them (§4.2 vs §15.1). Twelve metres is past a 10 m interval and
		// well inside a 50 m one, and the same fix has to answer differently for each.
		TrackRecording.Decide(Point(Latitude, 0), Fix(latitude: NorthOf(12), secondsIn: 1), 10)
			.ShouldBe(RecordDecision.Append);

		TrackRecording.Decide(Point(Latitude, 0), Fix(latitude: NorthOf(12), secondsIn: 1), 50)
			.ShouldBe(RecordDecision.Drop);
	}

	[Fact]
	public void AHoleInTheFixes_StartsANewSegment()
	{
		// §15.3: a tunnel, a car park, a phone that lost the sky. The ride did not happen in a
		// straight line between the two ends of it, so the track must not draw one.
		TrackRecording.Decide(
			Point(Latitude, 0),
			Fix(secondsIn: (int)TrackRecording.SegmentGap.TotalSeconds),
			10).ShouldBe(RecordDecision.StartSegment);
	}

	[Fact]
	public void AFixStampedBeforeTheLastOneKept_IsDropped()
	{
		// TrackStats drops duration, max speed and both timestamps wholesale for a non-monotonic
		// track (§15.3) — so one replayed cached point costs the rider every time-derived figure
		// about the whole ride.
		TrackRecording.Decide(Point(Latitude, 60), Fix(latitude: NorthOf(500), secondsIn: 10), 10)
			.ShouldBe(RecordDecision.Drop);
	}

	[Theory]
	[InlineData(double.NaN, Longitude)]
	[InlineData(Latitude, double.PositiveInfinity)]
	[InlineData(91, Longitude)]
	[InlineData(Latitude, -181)]
	public void SomethingThatIsNotAPointOnTheEarth_IsDropped(double latitude, double longitude)
	{
		TrackRecording.Decide(null, Fix(latitude, longitude), 10).ShouldBe(RecordDecision.Drop);
	}

	[Fact]
	public void ARecordedPoint_CarriesTheShapeAndTheClock_AndNothingElse()
	{
		// Speed and heading are deliberately not carried: §15.7 recomputes every speed from the
		// legs, so a recorded ride and an imported one cannot disagree about the same number.
		TrackPoint point = TrackRecording.ToPoint(Fix());

		point.Latitude.ShouldBe(Latitude);
		point.Longitude.ShouldBe(Longitude);
		point.TimeUtc.ShouldBe(Start);
		point.ElevationM.ShouldBeNull();
	}

	// ---------- The private-area filter (§10.1) ----------

	[Fact]
	public void WithNoPrivateArea_TheTrackIsUntouched()
	{
		TrackGeometry source = new([Point(Latitude, 0), Point(NorthOf(20), 2)]);

		TrackRecording.WithoutPrivateArea(source, null).ShouldBeSameAs(source);
	}

	[Fact]
	public void PointsInsideThePrivateArea_AreRemoved()
	{
		PrivateArea area = new(Latitude, Longitude, PrivateArea.MinRadiusM);

		TrackGeometry filtered = TrackRecording.WithoutPrivateArea(
			new TrackGeometry([Point(Latitude, 0), Point(NorthOf(50), 2), Point(NorthOf(500), 4)]),
			area);

		filtered.Points.Count.ShouldBe(1);
		filtered.Points[0].Latitude.ShouldBe(NorthOf(500), tolerance: 1e-9);
	}

	[Fact]
	public void RidingOutAndBackIn_LeavesAGapRatherThanALineThroughTheHouse()
	{
		// The headline claim of the feature. A single segment across the hole would draw a
		// straight line between the two ends of it — through the middle, which is the one
		// coordinate the setting exists to keep off other people's screens.
		PrivateArea area = new(Latitude, Longitude, PrivateArea.MinRadiusM);

		TrackGeometry filtered = TrackRecording.WithoutPrivateArea(
			new TrackGeometry([
				Point(NorthOf(-800), 0),
				Point(NorthOf(-300), 2),
				Point(Latitude, 4),       // inside
				Point(NorthOf(50), 6),    // inside
				Point(NorthOf(300), 8),
				Point(NorthOf(800), 10),
			]),
			area);

		filtered.Points.Count.ShouldBe(4);
		filtered.SegmentCount.ShouldBe(2,
			"§15.3: the hole where the private area was is a segment break, not a leg.");
		filtered.SegmentStarts.ShouldBe(new[] { 0, 2 });

		// And no leg spans the two halves, which is what "never summed across a break" buys.
		filtered.Legs().Count().ShouldBe(2);
	}

	[Fact]
	public void ATrackWhollyInsideThePrivateArea_FiltersToNothing()
	{
		PrivateArea area = new(Latitude, Longitude, PrivateArea.DefaultRadiusM);

		TrackGeometry filtered = TrackRecording.WithoutPrivateArea(
			new TrackGeometry([Point(Latitude, 0), Point(NorthOf(50), 2)]),
			area);

		filtered.Points.ShouldBeEmpty();
	}

	// ---------- The device-store codec ----------

	[Fact]
	public void ATrackSurvivesARoundTripThroughTheDeviceStore()
	{
		// What a relaunch mid-tour depends on. The identifier travels with it because the upload
		// is idempotent on it (§4.4) — a phone that came back and re-sent must not produce two
		// rides.
		Guid clientGuid = Guid.NewGuid();
		TrackGeometry source = new(
			[Point(Latitude, 0), Point(NorthOf(20), 2), Point(NorthOf(900), 400)],
			[2]);

		RecordedTrack? recovered = TrackRecording.Decode(TrackRecording.Encode(clientGuid, source));

		recovered.ShouldNotBeNull();
		recovered.ClientGuid.ShouldBe(clientGuid);
		recovered.Geometry.Points.ShouldBe(source.Points);
		recovered.Geometry.SegmentStarts.ShouldBe(source.SegmentStarts);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("2|00000000000000000000000000000000|AAAA")]
	[InlineData("1|not-a-guid|AAAA")]
	[InlineData("1|00000000000000000000000000000000|not base64")]
	[InlineData("1|00000000000000000000000000000000|AAAA")]
	public void AnythingNotWhollyReadable_IsNoTrackAtAll(string? stored)
	{
		// PrivateArea.Decode's posture rather than RouteStyle.Decode's, for the same reason:
		// half a ride recovered from a truncated blob is a shape the rider never rode, and it
		// would be uploaded under their name without anybody being told.
		TrackRecording.Decode(stored).ShouldBeNull();
	}

	[Fact]
	public void TheOfferedIntervals_AreTheOnesTheScreenPrints()
	{
		TrackRecording.IntervalsM.ShouldBe(new[] { 5d, 10, 50, 100, 500 });
		TrackRecording.DefaultIntervalM.ShouldBe(10);
		TrackRecording.DefaultEnabled.ShouldBeTrue();
	}

	[Theory]
	[InlineData("50", 50)]
	[InlineData("5", 5)]
	[InlineData("500", 500)]
	[InlineData(null, TrackRecording.DefaultIntervalM)]
	[InlineData("", TrackRecording.DefaultIntervalM)]
	[InlineData("nonsense", TrackRecording.DefaultIntervalM)]
	[InlineData("37", TrackRecording.DefaultIntervalM)]
	public void AnIntervalThisDeviceCouldNotHaveChosen_ReadsBackAsTheDefault(string? stored, double expected)
	{
		TrackRecording.DecodeInterval(stored).ShouldBe(expected);
	}
}
