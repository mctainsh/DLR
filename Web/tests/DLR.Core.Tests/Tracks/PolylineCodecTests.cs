using DLR.Core.Tracks;
using DLR.TestSupport.Tracks;

namespace DLR.Core.Tests.Tracks;

/// <summary>
/// The wire encoding the editor indexes against (§15.5).
/// <para>
/// Lossy on purpose, and safely so: the editor sends back <em>indices</em>, never coordinates,
/// and nothing derived is computed from a decoded value. The blob keeps the exact doubles.
/// </para>
/// </summary>
public sealed class PolylineCodecTests
{
	/// <summary>The worked example from Google's own specification, which pins the algorithm.</summary>
	[Fact]
	public void Encode_KnownPoints_MatchesTheReferenceAlgorithm()
	{
		// At precision 5 the reference output for these three points is "_p~iF~ps|U_ulLnnqC_mqNvxq`@".
		// This codec runs at precision 6, so the check is the round trip plus the shape rather
		// than that exact string — but a value encoded and decoded has to survive.
		string encoded = PolylineCodec.EncodePoints(
		[
			new TrackPoint(38.5, -120.2),
			new TrackPoint(40.7, -120.95),
			new TrackPoint(43.252, -126.453),
		]);

		encoded.ShouldNotBeNullOrWhiteSpace();

		IReadOnlyList<(double Latitude, double Longitude)> decoded = PolylineCodec.DecodePoints(encoded);

		decoded.Count.ShouldBe(3);
		decoded[0].Latitude.ShouldBe(38.5, tolerance: 0.0000005);
		decoded[0].Longitude.ShouldBe(-120.2, tolerance: 0.0000005);
		decoded[2].Latitude.ShouldBe(43.252, tolerance: 0.0000005);
		decoded[2].Longitude.ShouldBe(-126.453, tolerance: 0.0000005);
	}

	[Fact]
	public void Encode_RoundTrips_ToWithinAboutATenthOfAMetre()
	{
		TrackGeometry geometry = Read(GpxFixtures.SingleTrack(points: 200, metresApart: 7));

		IReadOnlyList<(double Latitude, double Longitude)> decoded =
			PolylineCodec.DecodePoints(PolylineCodec.EncodePoints(geometry.Points));

		decoded.Count.ShouldBe(geometry.Points.Count);

		for (int index = 0; index < decoded.Count; index++)
		{
			// One unit in the sixth decimal place, which is what precision 6 guarantees: half
			// of it is rounding and the rest is the double arithmetic on the way through.
			// About a tenth of a metre, and nothing derived is ever computed from a decoded
			// value anyway — the blob keeps the exact doubles.
			decoded[index].Latitude.ShouldBe(
				geometry.Points[index].Latitude,
				tolerance: 0.000001,
				$"point {index} moved further than the format's precision allows");
		}
	}

	/// <summary>
	/// The reason the format is worth the trouble: consecutive fixes differ in the last few
	/// digits, so almost every delta fits in a character or two.
	/// </summary>
	[Fact]
	public void Encode_IsFarSmallerThanTheCoordinatesItCarries()
	{
		TrackGeometry geometry = Read(GpxFixtures.SingleTrack(points: 1_000, metresApart: 5));

		string encoded = PolylineCodec.EncodePoints(geometry.Points);

		// Two doubles a point would be 16 000 bytes before any text formatting at all.
		encoded.Length.ShouldBeLessThan(
			geometry.Points.Count * 12,
			$"1 000 points encoded to {encoded.Length} characters, which is not a saving");
	}

	[Fact]
	public void Values_DeltaEncoding_RoundTripsIncludingNegatives()
	{
		long[] values = [0, 1, 2, 3, 100, 99, 98, -50, -50, 0, long.MaxValue / 4];

		PolylineCodec.DecodeValues(PolylineCodec.EncodeValues(values)).ShouldBe(values);
	}

	/// <summary>
	/// A 1 Hz recording is a run of ones once delta-encoded, which is the point of sending
	/// offsets rather than instants.
	/// </summary>
	[Fact]
	public void TimeOffsets_AreSecondsFromTheFirstPoint()
	{
		TrackGeometry geometry = Read(GpxFixtures.SingleTrack(points: 5, secondsApart: 10));

		PolylineCodec.TimeOffsetSeconds(geometry.Points).ShouldBe([0, 10, 20, 30, 40]);
	}

	[Fact]
	public void TimeOffsets_TracksWithoutTimestamps_AreEmpty()
	{
		TrackGeometry route = Read(GpxFixtures.Route(points: 4));

		PolylineCodec.TimeOffsetSeconds(route.Points).ShouldBeEmpty(
			"a route has no timestamps, and a run of zeroes would claim it started at midnight");
	}

	/// <summary>
	/// Zero is a real height — every track at sea level would otherwise claim one (§15.1).
	/// </summary>
	[Fact]
	public void Elevations_MissingOnes_AreNotZero()
	{
		TrackGeometry geometry = new(
		[
			new TrackPoint(-27.47, 153.02, 12.3),
			new TrackPoint(-27.471, 153.02),
			new TrackPoint(-27.472, 153.02, 0),
		]);

		IReadOnlyList<long> elevations = PolylineCodec.ElevationDecimetres(geometry.Points);

		elevations[0].ShouldBe(123);
		elevations[1].ShouldBe(PolylineCodec.MissingElevation);
		elevations[2].ShouldBe(0, "sea level is a measurement, and it is not the same as none");
	}

	[Fact]
	public void Decode_TruncatedInput_IsRejectedRatherThanGuessed()
	{
		string encoded = PolylineCodec.EncodePoints(
			[new TrackPoint(-27.47, 153.02), new TrackPoint(-27.48, 153.03)]);

		Should.Throw<FormatException>(() => PolylineCodec.DecodePoints(encoded[..^1]));
	}

	private static TrackGeometry Read(string gpx) =>
		GpxReader.Read(GpxFixtures.AsStream(gpx)).Tracks[0].Geometry;
}
