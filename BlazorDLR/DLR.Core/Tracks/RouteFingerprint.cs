using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DLR.Core.Tracks;

/// <summary>
/// A hash over where a track goes, and nothing else (§6.2).
/// <para>
/// <strong>Deliberately not <see cref="TrackBlobCodec.ContentHash"/>.</strong> That one answers
/// "is this the same recording?" — it takes in elevation, timestamps and segment breaks, which
/// is right for the import warning it serves: two files that differ by a single timestamp are
/// two different recordings and the rider is entitled to keep both. This one answers a different
/// question, "is this the same road?", asked when a route is about to go on everybody's browse
/// list. A copy of somebody else's route that lost its timestamps on the way through a planning
/// tool is still the same road, and the browse list does not want it twice.
/// </para>
/// <para>
/// Coordinates are rounded to <see cref="DecimalPlaces"/> before they are hashed, because the
/// obvious way to end up with a copy of a shared route is to export it and import it again —
/// and <see cref="GpxWriter"/> writes seven decimal places, so a round trip through a file
/// perturbs the last bits of a double. Six places is a tenth of a metre: far below anything a
/// rider could steer, and far above the noise a file format introduces.
/// </para>
/// <para>
/// It is not, and cannot be, a same-road detector for two independent recordings. Two riders
/// down the same lane produce different points and hash differently — that is a group ride, not
/// a duplicate (§15.3), and finding it would need a spatial comparison rather than a hash.
/// </para>
/// </summary>
public static class RouteFingerprint
{
	/// <summary>
	/// How precisely a coordinate is taken before hashing. Six places is about 0.1 m of latitude.
	/// </summary>
	public const int DecimalPlaces = 6;

	/// <summary>The scale that turns a rounded coordinate into the integer that gets hashed.</summary>
	private const double Scale = 1_000_000;

	/// <summary>
	/// The fingerprint of a track's line, or an empty array when there is no line to fingerprint.
	/// </summary>
	/// <param name="geometry">The points.</param>
	/// <remarks>
	/// Empty in, empty out — and empty means "do not compare". Hashing nothing would give every
	/// pointless track the same well-known SHA-256, and each one would then be reported as a
	/// duplicate of the last.
	/// </remarks>
	public static byte[] Of(TrackGeometry geometry)
	{
		if (geometry.Points.Count == 0)
		{
			return [];
		}

		Span<byte> buffer = stackalloc byte[8];

		using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

		foreach (TrackPoint point in geometry.Points)
		{
			BinaryPrimitives.WriteInt64LittleEndian(buffer, Quantise(point.Latitude));
			hash.AppendData(buffer);

			BinaryPrimitives.WriteInt64LittleEndian(buffer, Quantise(point.Longitude));
			hash.AppendData(buffer);
		}

		return hash.GetHashAndReset();
	}

	/// <summary>
	/// A coordinate as the fixed-point integer the hash sees. Integers rather than the rounded
	/// double, so that two values that agree to six places agree here exactly — which is the
	/// whole point, and is not something two doubles can be relied on to do.
	/// </summary>
	/// <param name="degrees">The coordinate.</param>
	private static long Quantise(double degrees) =>
		(long)Math.Round(degrees * Scale, MidpointRounding.AwayFromZero);
}
