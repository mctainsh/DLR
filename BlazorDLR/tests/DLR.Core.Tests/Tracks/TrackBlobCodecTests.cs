using DLR.Core.Tracks;

namespace DLR.Core.Tests.Tracks;

/// <summary>
/// §8 and §15's write-once, read-whole track blob. Three properties to pin:
/// <list type="bullet">
///   <item>Round-trip is lossless: an edit re-reading the blob must produce the same
///     stats. Even a rounding of the tenth-of-a-metre would break §15.7's
///     "no-op edit produces identical stats" invariant.</item>
///   <item>Elevation and time are optional; a null must not travel as zero (§8).</item>
///   <item>A stray or older-version blob is a hard read error, not silently reinterpreted.</item>
/// </list>
/// </summary>
public sealed class TrackBlobCodecTests
{
	private static readonly DateTimeOffset T0 = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	private static byte[] Encode(TrackGeometry g)
	{
		using MemoryStream ms = new();
		TrackBlobCodec.Write(g, ms);
		return ms.ToArray();
	}

	private static TrackGeometry Decode(byte[] bytes)
	{
		using MemoryStream ms = new(bytes);
		return TrackBlobCodec.Read(ms);
	}

	[Fact]
	public void RoundTrip_FullyPopulatedPoints_PreservesEverything()
	{
		TrackPoint a = new(-33.868, 151.209, ElevationM: 12.5, TimeUtc: T0);
		TrackPoint b = new(-33.869, 151.211, ElevationM: 13.0, TimeUtc: T0.AddSeconds(10));
		TrackPoint c = new(-33.870, 151.213, ElevationM: 14.2, TimeUtc: T0.AddSeconds(20));
		TrackGeometry original = new(new[] { a, b, c }, segmentStarts: new[] { 2 });

		TrackGeometry decoded = Decode(Encode(original));

		decoded.Points.Count.ShouldBe(3);
		decoded.Points[0].Latitude.ShouldBe(a.Latitude);
		decoded.Points[0].Longitude.ShouldBe(a.Longitude);
		decoded.Points[0].ElevationM.ShouldBe(12.5);
		decoded.Points[0].TimeUtc.ShouldBe(T0);
		decoded.SegmentCount.ShouldBe(2, "§15.3: segment starts round-trip so a pause survives a save.");
	}

	[Fact]
	public void RoundTrip_NullElevationAndTime_StayNull_NotZero()
	{
		TrackPoint noElevationNoTime = new(0.0, 0.0);
		TrackPoint withElevationOnly = new(0.0, 0.001, ElevationM: 100);
		TrackPoint withTimeOnly = new(0.0, 0.002, TimeUtc: T0);
		TrackGeometry original = new(new[] { noElevationNoTime, withElevationOnly, withTimeOnly });

		TrackGeometry decoded = Decode(Encode(original));

		decoded.Points[0].ElevationM.ShouldBeNull(
			"§8: null must not travel as zero - sending zero elevation would claim sea level for a point that was never measured.");
		decoded.Points[0].TimeUtc.ShouldBeNull();
		decoded.Points[1].ElevationM.ShouldBe(100);
		decoded.Points[1].TimeUtc.ShouldBeNull("elevation-only point kept time null through the round trip.");
		decoded.Points[2].TimeUtc.ShouldBe(T0);
		decoded.Points[2].ElevationM.ShouldBeNull();
	}

	[Fact]
	public void RoundTrip_EmptyGeometry_ProducesEmptyGeometry()
	{
		TrackGeometry decoded = Decode(Encode(new TrackGeometry(Array.Empty<TrackPoint>())));

		decoded.Points.Count.ShouldBe(0);
		decoded.SegmentCount.ShouldBe(0);
	}

	[Fact]
	public void RoundTrip_StatsAreIdentical_LosslessInvariant()
	{
		TrackPoint a = new(-33.868, 151.209, ElevationM: 100.0, TimeUtc: T0);
		TrackPoint b = new(-33.869, 151.211, ElevationM: 108.5, TimeUtc: T0.AddSeconds(10));
		TrackPoint c = new(-33.870, 151.213, ElevationM: 105.7, TimeUtc: T0.AddSeconds(25));
		TrackGeometry original = new(new[] { a, b, c });

		TrackStats before = TrackStats.From(original);
		TrackStats after = TrackStats.From(Decode(Encode(original)));

		before.DistanceM.ShouldBe(after.DistanceM, tolerance: 1e-9,
			"§15.7: a save/load round-trip must produce identical distance - otherwise an untouched half of an edited ride would report a different number.");
		before.AscentM.ShouldBe(after.AscentM);
		before.DurationS.ShouldBe(after.DurationS);
	}

	[Fact]
	public void ContentHash_SameContent_SameHash()
	{
		TrackGeometry g1 = new(new[] { new TrackPoint(-33.868, 151.209, ElevationM: 12) });
		TrackGeometry g2 = new(new[] { new TrackPoint(-33.868, 151.209, ElevationM: 12) });

		byte[] h1 = TrackBlobCodec.ContentHash(g1);
		byte[] h2 = TrackBlobCodec.ContentHash(g2);

		h1.SequenceEqual(h2).ShouldBeTrue(
			"§15.3: two blobs with identical content must hash identically - that's the whole point of the content hash for duplicate detection.");
	}

	[Fact]
	public void ContentHash_DifferentContent_DifferentHash()
	{
		TrackGeometry g1 = new(new[] { new TrackPoint(0, 0) });
		TrackGeometry g2 = new(new[] { new TrackPoint(0, 1) });

		byte[] h1 = TrackBlobCodec.ContentHash(g1);
		byte[] h2 = TrackBlobCodec.ContentHash(g2);

		h1.SequenceEqual(h2).ShouldBeFalse();
	}

	[Fact]
	public void Read_WrongMagicInsideValidGzip_ThrowsInvalidData()
	{
		// Build a valid gzip payload whose first four bytes are NOT the DLR1 magic. The
		// codec must recognise the mismatch and refuse - reading it as ours would produce
		// nonsense stats or crash somewhere further in.
		using MemoryStream ms = new();
		using (System.IO.Compression.GZipStream gzip = new(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
		using (BinaryWriter writer = new(gzip))
		{
			writer.Write(0xDEADBEEFu); // wrong magic
			writer.Write((ushort)1);
			writer.Write(0); // point count
			writer.Write(0); // segment count
		}

		ms.Position = 0;
		Should.Throw<InvalidDataException>(() => TrackBlobCodec.Read(ms));
	}
}
