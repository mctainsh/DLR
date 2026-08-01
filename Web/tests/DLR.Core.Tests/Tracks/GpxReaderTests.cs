using DLR.Core.Tracks;
using DLR.TestSupport.Tracks;

namespace DLR.Core.Tests.Tracks;

/// <summary>
/// Reading ordinary GPX, and the format's own untidiness (§15.3).
/// <para>
/// GPX in the wild is looser than the schema suggests, and every row of §15.3's table is a
/// thing some real exporter does. Rejecting them would fail imports people have every reason to
/// expect to work.
/// </para>
/// </summary>
public sealed class GpxReaderTests
{
	[Fact]
	public void Import_GpxWithSingleTrack_CreatesTrackWithComputedStats()
	{
		GpxDocument document = Read(GpxFixtures.SingleTrack(points: 5, metresApart: 20, secondsApart: 10));

		GpxTrack track = document.Tracks.ShouldHaveSingleItem();

		track.Name.ShouldBe("Morning loop");
		track.Source.ShouldBe(GpxTrackSource.Track);
		track.Geometry.Points.Count.ShouldBe(5);
		track.Geometry.SegmentCount.ShouldBe(1);

		TrackStats stats = TrackStats.From(track.Geometry);

		// Four legs of roughly twenty metres. Loose, because the fixture converts metres to
		// degrees with a flat approximation — the assertion is that a real distance was
		// measured, not that the arithmetic matches a spreadsheet.
		stats.DistanceM.ShouldBeInRange(75, 85);

		stats.DurationS.ShouldBe(40);
		stats.StartedUtc.ShouldBe(GpxFixtures.Start);
		stats.EndedUtc.ShouldBe(GpxFixtures.Start.AddSeconds(40));
		stats.MaxSpeedMps.ShouldNotBeNull();
		stats.PointCount.ShouldBe(5);

		stats.Bounds.ShouldNotBeNull();
		stats.Bounds!.Value.MinLatitude.ShouldBeLessThan(stats.Bounds.Value.MaxLatitude);
	}

	/// <summary>
	/// A segment break is a pause, a tunnel or a lost signal. Summing across one draws a
	/// straight line through the tunnel and calls it distance ridden.
	/// </summary>
	[Fact]
	public void Import_MultipleSegments_AreKeptAsBreaksAndNotSummedAcross()
	{
		GpxDocument document = Read(
			GpxFixtures.TrackWithSegmentBreak(pointsPerSegment: 3, gapMetres: 5_000));

		GpxTrack track = document.Tracks.ShouldHaveSingleItem();

		track.Geometry.Points.Count.ShouldBe(6);
		track.Geometry.SegmentCount.ShouldBe(2);
		track.Geometry.SegmentStarts.ShouldBe([0, 3]);

		TrackStats stats = TrackStats.From(track.Geometry);

		// Four legs of 20 m, and the 5 km jump between segments is not one of them.
		stats.DistanceM.ShouldBeLessThan(
			1_000,
			"the gap between segments is a tunnel, not five kilometres of riding");
	}

	[Fact]
	public void Import_GpxWithMultipleTracks_CreatesOnePerTrkUpToCap()
	{
		GpxDocument within = Read(GpxFixtures.ManyTracks(3));

		within.Tracks.Count.ShouldBe(3);
		within.TracksTruncated.ShouldBeFalse();
		within.Tracks.Select(track => track.Name).ShouldBe(["Track 0", "Track 1", "Track 2"]);

		GpxDocument capped = GpxReader.Read(
			GpxFixtures.AsStream(GpxFixtures.ManyTracks(25)),
			new GpxLimits { MaxTracksPerFile = 20 });

		capped.Tracks.Count.ShouldBe(20);

		capped.TracksTruncated.ShouldBeTrue(
			"the preview lists what was read and says what was left — refusing a file for " +
			"having twenty-one tracks would be less use to everyone");
	}

	/// <summary>
	/// Planning tools emit <c>&lt;rte&gt;</c>, and rejecting them would fail the most common
	/// import there is (§15.3).
	/// </summary>
	[Fact]
	public void Import_GpxRouteElement_ImportsAsTrackWithoutTimestamps()
	{
		GpxDocument document = Read(GpxFixtures.Route(points: 4));

		GpxTrack route = document.Tracks.ShouldHaveSingleItem();

		route.Source.ShouldBe(GpxTrackSource.Route);
		route.Name.ShouldBe("Planned route");
		route.Geometry.Points.Count.ShouldBe(4);
		route.Geometry.Points.ShouldAllBe(point => point.TimeUtc == null);

		TrackStats stats = TrackStats.From(route.Geometry);

		stats.DistanceM.ShouldBeGreaterThan(0);

		// A route is not a ride. Zero would claim a measurement nobody took (§8, §15.1).
		stats.DurationS.ShouldBeNull();
		stats.MaxSpeedMps.ShouldBeNull();
		stats.StartedUtc.ShouldBeNull();
		stats.EndedUtc.ShouldBeNull();
	}

	/// <summary>
	/// Some planning tools stamp a time on a route point anyway. §15.3 imports a route without
	/// timestamps regardless — honouring one would give a planned route a duration and let it
	/// into a total of rides actually ridden.
	/// </summary>
	[Fact]
	public void Import_RouteWithStrayTimestamps_StillHasNone()
	{
		GpxDocument document = Read(GpxFixtures.Route(points: 4, withStrayTime: true));

		document.Tracks.ShouldHaveSingleItem()
			.Geometry.Points.ShouldAllBe(point => point.TimeUtc == null);
	}

	[Fact]
	public void Import_GpxWithoutElevation_LeavesAscentNull()
	{
		GpxDocument document = Read(GpxFixtures.SingleTrack(withElevation: false));

		TrackStats stats = TrackStats.From(document.Tracks.ShouldHaveSingleItem().Geometry);

		stats.AscentM.ShouldBeNull(
			"no interpolation and no DEM lookup — inventing elevation adds a paid dependency " +
			"and a failure mode for a number nobody is checking (§15.1)");

		stats.DistanceM.ShouldBeGreaterThan(0, "the rest of the track is still perfectly usable");
	}

	[Fact]
	public void Import_GpxWithoutTimestamps_LeavesDurationAndSpeedNull()
	{
		GpxDocument document = Read(GpxFixtures.SingleTrack(withTime: false));

		TrackStats stats = TrackStats.From(document.Tracks.ShouldHaveSingleItem().Geometry);

		stats.DurationS.ShouldBeNull();
		stats.MaxSpeedMps.ShouldBeNull();
		stats.StartedUtc.ShouldBeNull();
		stats.EndedUtc.ShouldBeNull();

		stats.AscentM.ShouldNotBeNull("elevation and time are independently absent");
	}

	/// <summary>
	/// Geometry wins over the clock. Reordering points to satisfy a timestamp would silently
	/// change the shape of the ride, which is far worse than losing a duration (§15.3).
	/// </summary>
	[Fact]
	public void Import_NonMonotonicTimestamps_PreservesGeometryAndDropsTimeStats()
	{
		GpxDocument document = Read(GpxFixtures.NonMonotonicTimestamps());

		GpxTrack track = document.Tracks.ShouldHaveSingleItem();

		track.Geometry.Points.Count.ShouldBe(5);

		// File order, untouched — the fourth point's clock reads earlier than the third's and
		// it stays exactly where the file put it.
		track.Geometry.Points[3].TimeUtc.ShouldBe(GpxFixtures.Start.AddSeconds(5));

		track.Geometry.Points
			.Select(point => point.Latitude)
			.ShouldBeInOrder(SortDirection.Ascending);

		TrackStats stats = TrackStats.From(track.Geometry);

		stats.DistanceM.ShouldBeGreaterThan(0);
		stats.AscentM.ShouldNotBeNull();

		stats.DurationS.ShouldBeNull();
		stats.MaxSpeedMps.ShouldBeNull();
		stats.StartedUtc.ShouldBeNull();
	}

	/// <summary>
	/// Read now, persisted in SRV-26. Counting them is what lets the import preview say how
	/// many markers a file would create (§15.3, §16.6).
	/// </summary>
	[Fact]
	public void Import_Waypoints_AreReadWithTheirNameDescriptionAndSymbol()
	{
		GpxDocument document = Read(GpxFixtures.WithWaypoints(2));

		document.Waypoints.Count.ShouldBe(2);

		GpxWaypoint first = document.Waypoints[0];

		first.Name.ShouldBe("Water stop 0");
		first.Description.ShouldBe("Tap on the wall");
		first.Symbol.ShouldBe("Drinking Water");

		// The fixture carries a <link href>. It is text and stays text: fetching one would
		// hand the author of any uploaded file a request from this server.
		document.Waypoints.ShouldAllBe(waypoint => waypoint.Latitude < 0);
	}

	/// <summary>
	/// One reader for the app, the server and the editor (§15.7). Reading the same bytes twice
	/// has to give the same answer, or the offline import and the web import disagree about a
	/// file and only one of them is right.
	/// </summary>
	[Fact]
	public void Import_SameFileTwice_ProducesIdenticalTracks()
	{
		string gpx = GpxFixtures.SingleTrack(points: 40);

		TrackStats first = TrackStats.From(Read(gpx).Tracks[0].Geometry);
		TrackStats second = TrackStats.From(Read(gpx).Tracks[0].Geometry);

		second.ShouldBe(first);
	}

	private static GpxDocument Read(string gpx) => GpxReader.Read(GpxFixtures.AsStream(gpx));
}
