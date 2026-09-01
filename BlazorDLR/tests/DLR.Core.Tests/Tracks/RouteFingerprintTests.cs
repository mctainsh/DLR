using System.Text;
using DLR.Core.Tracks;

namespace DLR.Core.Tests.Tracks;

/// <summary>
/// The fingerprint the browse list's duplicate check reads (§6.2).
/// <para>
/// Its whole job is to disagree with <see cref="TrackBlobCodec.ContentHash"/> in one specific
/// way: the same road, arriving by a route that lost its timestamps or its elevation, still
/// fingerprints the same. The tests below are about that difference, and about the round trip
/// through a GPX file that is the realistic way a rider ends up holding a copy of somebody
/// else's route in the first place.
/// </para>
/// </summary>
public sealed class RouteFingerprintTests
{
	private static readonly DateTimeOffset T0 = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	[Fact]
	public void SameCoordinates_SameFingerprint()
	{
		TrackGeometry one = Line();
		TrackGeometry two = Line();

		RouteFingerprint.Of(one).ShouldBe(RouteFingerprint.Of(two));
	}

	[Fact]
	public void DifferentCoordinates_DifferentFingerprint()
	{
		TrackGeometry one = Line();
		TrackGeometry two = new([new TrackPoint(-33.868, 151.209), new TrackPoint(-33.869, 151.400)]);

		RouteFingerprint.Of(one).ShouldNotBe(RouteFingerprint.Of(two));
	}

	/// <summary>
	/// The difference from the content hash, stated as a test. A planned route has no clock and
	/// no elevation; the recording it was planned from has both. They are the same road, and a
	/// browse list that listed them side by side would be listing the same road twice.
	/// </summary>
	[Fact]
	public void TimestampsAndElevation_DoNotChangeIt_ThoughTheyChangeTheContentHash()
	{
		TrackGeometry recorded = new(
		[
			new TrackPoint(-33.868, 151.209, ElevationM: 12.5, TimeUtc: T0),
			new TrackPoint(-33.869, 151.211, ElevationM: 13.0, TimeUtc: T0.AddSeconds(10)),
		]);

		TrackGeometry planned = new(
		[
			new TrackPoint(-33.868, 151.209),
			new TrackPoint(-33.869, 151.211),
		]);

		RouteFingerprint.Of(recorded).ShouldBe(RouteFingerprint.Of(planned));

		TrackBlobCodec.ContentHash(recorded).ShouldNotBe(
			TrackBlobCodec.ContentHash(planned),
			"§15.3's hash answers a different question - two recordings, and the rider keeps both");
	}

	/// <summary>
	/// The way a duplicate actually arrives: somebody exports a shared route and imports the
	/// file. <see cref="GpxWriter"/> writes seven decimal places, so the doubles that come back
	/// are not bit-identical to the ones that went out - which is exactly why the fingerprint
	/// rounds before it hashes.
	/// </summary>
	[Fact]
	public void SurvivesARoundTripThroughAGpxFile()
	{
		TrackGeometry original = Line();

		string gpx = GpxWriter.Write("Coast run north", original.Points, []);

		using MemoryStream file = new(Encoding.UTF8.GetBytes(gpx));

		TrackGeometry reimported = GpxReader.Read(file).Tracks.Single().Geometry;

		RouteFingerprint.Of(reimported).ShouldBe(RouteFingerprint.Of(original));
	}

	/// <summary>
	/// Empty means "unknown", and the check that reads it skips a route rather than comparing
	/// it. Hashing nothing would give every point-less track the same well-known SHA-256 and
	/// have each one reported as a duplicate of the last.
	/// </summary>
	[Fact]
	public void NoPoints_NoFingerprint()
	{
		RouteFingerprint.Of(new TrackGeometry([])).ShouldBeEmpty();
	}

	/// <summary>
	/// Two riders down the same lane are a group ride, not a duplicate (§15.3). This is a hash
	/// and not a spatial comparison, and the boundary is worth stating: metres apart is a
	/// different fingerprint, and the check will let both routes onto the list.
	/// </summary>
	[Fact]
	public void TwoSeparateRecordingsOfTheSameRoad_DoNotCollide()
	{
		TrackGeometry mine = Line();

		TrackGeometry theirs = new(
		[
			.. Line().Points.Select(point => point with { Latitude = point.Latitude + 0.00005 }),
		]);

		RouteFingerprint.Of(mine).ShouldNotBe(RouteFingerprint.Of(theirs));
	}

	private static TrackGeometry Line() => new(
	[
		new TrackPoint(-33.868_123_4, 151.209_876_5, ElevationM: 12.5, TimeUtc: T0),
		new TrackPoint(-33.869_234_5, 151.211_765_4, ElevationM: 13.0, TimeUtc: T0.AddSeconds(10)),
		new TrackPoint(-33.870_345_6, 151.213_654_3, ElevationM: 14.2, TimeUtc: T0.AddSeconds(20)),
	]);
}
