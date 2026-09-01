using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace DLR.Core.Tracks;

/// <summary>
/// How a track's points are written to a blob (§8, §9.1).
/// <para>
/// <strong>Lossless, deliberately.</strong> §15.5's points endpoint sends the editor an encoded
/// polyline, which quantises coordinates to about a tenth of a metre - fine there, because the
/// editor only ever sends back <em>indices</em>. The blob is different: it is what an edit
/// re-reads and re-stats from, and a format that rounded coordinates would make
/// <c>Edit_NoOpEdit_ProducesIdenticalStats</c> false by construction. A track's ascent must not
/// shift because it was saved.
/// </para>
/// <para>
/// Points are write-once and read-whole, so this is a compressed blob rather than a row per
/// point (§8). Gzip does the work: consecutive coordinates share most of their bytes, which is
/// exactly what a general-purpose compressor is good at.
/// </para>
/// </summary>
public static class TrackBlobCodec
{
	/// <summary>Identifies the format, so a future one can be told from this one.</summary>
	private const uint Magic = 0x444C_5231; // "DLR1"

	private const ushort Version = 1;

	/// <summary>Writes a track's geometry, gzipped.</summary>
	/// <param name="geometry">The points and their segment breaks.</param>
	/// <param name="destination">Where to write; left open.</param>
	public static void Write(TrackGeometry geometry, Stream destination)
	{
		using GZipStream gzip = new(destination, CompressionLevel.SmallestSize, leaveOpen: true);
		using BinaryWriter writer = new(gzip);

		writer.Write(Magic);
		writer.Write(Version);

		writer.Write(geometry.Points.Count);
		writer.Write(geometry.SegmentCount);

		foreach (int start in geometry.SegmentStarts)
		{
			writer.Write(start);
		}

		foreach (TrackPoint point in geometry.Points)
		{
			writer.Write(point.Latitude);
			writer.Write(point.Longitude);

			// Presence flags rather than sentinel values. A missing elevation is not zero and
			// a missing timestamp is not the epoch - §15.1 is emphatic that null and zero are
			// different claims, and a sentinel is how they stop being.
			writer.Write(point.ElevationM is not null);
			writer.Write(point.ElevationM ?? 0);

			writer.Write(point.TimeUtc is not null);
			writer.Write(point.TimeUtc?.UtcTicks ?? 0);
		}
	}

	/// <summary>Reads back what <see cref="Write"/> wrote.</summary>
	/// <param name="source">The blob; left open.</param>
	/// <exception cref="InvalidDataException">The blob is not one of ours.</exception>
	public static TrackGeometry Read(Stream source)
	{
		using GZipStream gzip = new(source, CompressionMode.Decompress, leaveOpen: true);
		using BinaryReader reader = new(gzip);

		if (reader.ReadUInt32() != Magic)
		{
			throw new InvalidDataException("This blob was not written by the track codec.");
		}

		ushort version = reader.ReadUInt16();

		if (version != Version)
		{
			throw new InvalidDataException($"Track blob version {version} is not supported.");
		}

		int pointCount = reader.ReadInt32();
		int segmentCount = reader.ReadInt32();

		List<int> segmentStarts = new(segmentCount);

		for (int index = 0; index < segmentCount; index++)
		{
			segmentStarts.Add(reader.ReadInt32());
		}

		List<TrackPoint> points = new(pointCount);

		for (int index = 0; index < pointCount; index++)
		{
			double latitude = reader.ReadDouble();
			double longitude = reader.ReadDouble();

			bool hasElevation = reader.ReadBoolean();
			double elevation = reader.ReadDouble();

			bool hasTime = reader.ReadBoolean();
			long ticks = reader.ReadInt64();

			points.Add(new TrackPoint(
				latitude,
				longitude,
				hasElevation ? elevation : null,
				hasTime ? new DateTimeOffset(ticks, TimeSpan.Zero) : null));
		}

		return new TrackGeometry(points, segmentStarts);
	}

	/// <summary>
	/// A stable hash over the points themselves (§15.3).
	/// <para>
	/// Computed from the <em>uncompressed</em> encoding, so it identifies a track's content
	/// rather than a particular compressor's output - the same ride hashed the same way
	/// whichever version of gzip ran. This is what lets an import say "you imported this on
	/// 3 June" and let the rider decide.
	/// </para>
	/// </summary>
	/// <param name="geometry">The track to hash.</param>
	public static byte[] ContentHash(TrackGeometry geometry)
	{
		Span<byte> buffer = stackalloc byte[8];

		using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

		foreach (TrackPoint point in geometry.Points)
		{
			BinaryPrimitives.WriteDoubleLittleEndian(buffer, point.Latitude);
			hash.AppendData(buffer);

			BinaryPrimitives.WriteDoubleLittleEndian(buffer, point.Longitude);
			hash.AppendData(buffer);

			BinaryPrimitives.WriteDoubleLittleEndian(buffer, point.ElevationM ?? double.NaN);
			hash.AppendData(buffer);

			BinaryPrimitives.WriteInt64LittleEndian(buffer, point.TimeUtc?.UtcTicks ?? -1);
			hash.AppendData(buffer);
		}

		foreach (int start in geometry.SegmentStarts)
		{
			BinaryPrimitives.WriteInt32LittleEndian(buffer, start);
			hash.AppendData(buffer[..4]);
		}

		return hash.GetHashAndReset();
	}
}
