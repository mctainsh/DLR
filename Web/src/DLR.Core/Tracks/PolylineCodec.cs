using System.Globalization;
using System.Text;

namespace DLR.Core.Tracks;

/// <summary>
/// The encoded-polyline format, for sending a track over the wire (§15.5).
/// <para>
/// Google's algorithm at precision 6: values are scaled, delta-encoded against the previous
/// point, zig-zagged so a negative delta is a small number, then written five bits at a time as
/// printable ASCII. Consecutive fixes on a ride differ in the last few digits, so the deltas are
/// tiny and almost every one fits in a character or two.
/// </para>
/// <para>
/// <strong>Lossy, and that is fine here.</strong> Precision 6 rounds to about a tenth of a
/// metre. The editor is the only consumer and it sends back <em>indices</em>, never coordinates,
/// so nothing derived is ever computed from a decoded value — the blob keeps the exact doubles
/// (<see cref="TrackBlobCodec"/>) and that is what an edit re-stats from.
/// </para>
/// <para>
/// A 12-hour tour at 1 Hz is ~43 000 points, roughly 200 KB gzipped in this encoding — fine for
/// a desktop browser, which is the only place the editor runs.
/// </para>
/// </summary>
public static class PolylineCodec
{
	/// <summary>Six decimal places: about 0.1 m, and the precision the format is named for.</summary>
	public const int Precision = 6;

	private const double Scale = 1_000_000;

	/// <summary>Encodes coordinates as a polyline string.</summary>
	/// <param name="points">The points, in order.</param>
	public static string EncodePoints(IReadOnlyList<TrackPoint> points)
	{
		StringBuilder encoded = new(points.Count * 6);

		long previousLatitude = 0;
		long previousLongitude = 0;

		foreach (TrackPoint point in points)
		{
			long latitude = (long)Math.Round(point.Latitude * Scale, MidpointRounding.AwayFromZero);
			long longitude = (long)Math.Round(point.Longitude * Scale, MidpointRounding.AwayFromZero);

			AppendSigned(encoded, latitude - previousLatitude);
			AppendSigned(encoded, longitude - previousLongitude);

			previousLatitude = latitude;
			previousLongitude = longitude;
		}

		return encoded.ToString();
	}

	/// <summary>Decodes what <see cref="EncodePoints"/> produced.</summary>
	/// <param name="encoded">The polyline string.</param>
	public static IReadOnlyList<(double Latitude, double Longitude)> DecodePoints(string encoded)
	{
		List<(double, double)> points = [];

		int index = 0;
		long latitude = 0;
		long longitude = 0;

		while (index < encoded.Length)
		{
			latitude += ReadSigned(encoded, ref index);
			longitude += ReadSigned(encoded, ref index);

			points.Add((latitude / Scale, longitude / Scale));
		}

		return points;
	}

	/// <summary>
	/// Encodes a series of longs — timestamps as seconds, or elevations in decimetres — with
	/// the same delta and zig-zag scheme the coordinates use.
	/// </summary>
	/// <param name="values">The values, in order.</param>
	public static string EncodeValues(IReadOnlyList<long> values)
	{
		StringBuilder encoded = new(values.Count * 2);

		long previous = 0;

		foreach (long value in values)
		{
			AppendSigned(encoded, value - previous);

			previous = value;
		}

		return encoded.ToString();
	}

	/// <summary>Decodes what <see cref="EncodeValues"/> produced.</summary>
	/// <param name="encoded">The encoded series.</param>
	public static IReadOnlyList<long> DecodeValues(string encoded)
	{
		List<long> values = [];

		int index = 0;
		long running = 0;

		while (index < encoded.Length)
		{
			running += ReadSigned(encoded, ref index);

			values.Add(running);
		}

		return values;
	}

	/// <summary>
	/// Elevations as decimetres, so they travel as integers.
	/// <para>
	/// A missing elevation is <see cref="MissingElevation"/> rather than zero, because zero is a
	/// real height and every track at sea level would otherwise claim one (§15.1).
	/// </para>
	/// </summary>
	/// <param name="points">The points, in order.</param>
	public static IReadOnlyList<long> ElevationDecimetres(IReadOnlyList<TrackPoint> points) =>
		[.. points.Select(point => point.ElevationM is { } metres
			? (long)Math.Round(metres * 10, MidpointRounding.AwayFromZero)
			: MissingElevation)];

	/// <summary>The value that stands for "this point had no elevation".</summary>
	public const long MissingElevation = long.MinValue / 4;

	/// <summary>
	/// Timestamps as whole seconds since the first point, which is what makes the deltas small:
	/// a 1 Hz recording encodes as a run of ones.
	/// </summary>
	/// <param name="points">The points, in order.</param>
	public static IReadOnlyList<long> TimeOffsetSeconds(IReadOnlyList<TrackPoint> points)
	{
		if (points.Count == 0 || points[0].TimeUtc is not { } origin)
		{
			return [];
		}

		return [.. points.Select(point => point.TimeUtc is { } time
			? (long)Math.Round((time - origin).TotalSeconds, MidpointRounding.AwayFromZero)
			: 0)];
	}

	private static void AppendSigned(StringBuilder destination, long value)
	{
		// Zig-zag: the sign goes in the low bit, so −1 encodes as 1 rather than as a long run
		// of set bits. Without it every westward step would cost eleven characters.
		ulong zigzag = (ulong)((value << 1) ^ (value >> 63));

		while (zigzag >= 0x20)
		{
			destination.Append((char)((int)((zigzag & 0x1F) | 0x20) + 63));

			zigzag >>= 5;
		}

		destination.Append((char)((int)zigzag + 63));
	}

	private static long ReadSigned(string encoded, ref int index)
	{
		int shift = 0;
		ulong result = 0;
		int current;

		do
		{
			if (index >= encoded.Length)
			{
				throw new FormatException(
					string.Create(
						CultureInfo.InvariantCulture,
						$"The encoded value ending at {index} is truncated."));
			}

			current = encoded[index++] - 63;

			result |= (ulong)(current & 0x1F) << shift;
			shift += 5;
		}
		while (current >= 0x20);

		return (long)(result >> 1) ^ -(long)(result & 1);
	}
}
