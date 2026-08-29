using System.Globalization;
using DLR.Core.Tracks;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// The rules the recorder keeps a track by, and how the part-finished one survives a relaunch
/// (§4.4, §15.1, §18.6).
/// <para>
/// <strong>Separate from <see cref="PositionGate"/>, and deliberately.</strong> That gate decides
/// what is worth <em>publishing</em> — it spends a rider's uplink and every other rider's redraw,
/// so it is tuned as a battery decision. This decides what is worth <em>keeping</em>, which costs
/// nothing but device storage and is the rider's own record of where they went. A rider publishing
/// every 50 m who wants a 5 m track should get one; the two settings answer different questions
/// and are not derived from each other.
/// </para>
/// <para>
/// Pure and static: the interval rules, the encoding and the private-area filter all take what
/// they need as arguments, so <see cref="State.TrackRecordingState"/> is the only thing that has
/// to know about a device store.
/// </para>
/// </summary>
public static class TrackRecording
{
	/// <summary>
	/// The intervals the Location screen offers, in metres. Five choices rather than a slider, for
	/// the same reason the update rate is three controls: they are answers to "how much detail
	/// do you want a corner drawn in", not points on a continuum anybody can feel.
	/// </summary>
	public static readonly IReadOnlyList<double> IntervalsM = [5, 10, 50, 100, 500];

	/// <summary>
	/// What a device that has never chosen records at. Ten metres draws a roundabout as a
	/// roundabout and still costs about 100 points a kilometre.
	/// </summary>
	public const double DefaultIntervalM = 10;

	/// <summary>Whether a device that has never chosen records at all. It does — see §15.1.</summary>
	public const bool DefaultEnabled = true;

	/// <summary>
	/// A hole this long in the fixes starts a new segment rather than drawing a line across it
	/// (§15.3). A tunnel, a car park, a phone that lost the sky — the ride did not happen in a
	/// straight line between the two ends of it.
	/// </summary>
	public static readonly TimeSpan SegmentGap = TimeSpan.FromMinutes(5);

	/// <summary>
	/// Where the recorder stops appending. Twelve hours at 1 Hz is about 43 000 points (§15.5), so
	/// this is several days of touring — and it is a stop rather than a wrap, because silently
	/// dropping the start of somebody's ride is worse than telling them to save it.
	/// </summary>
	public const int MaxPoints = 200_000;

	/// <summary>Reads back an interval this device stored, falling back to <see cref="DefaultIntervalM"/>.</summary>
	/// <param name="stored">The stored string, or <c>null</c> on a device that has never chosen.</param>
	public static double DecodeInterval(string? stored) =>
		double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
			&& IntervalsM.Contains(parsed)
				? parsed
				: DefaultIntervalM;

	/// <summary>Writes an interval for <see cref="IDeviceSettings"/>.</summary>
	/// <param name="intervalM">One of <see cref="IntervalsM"/>. Anything else stores the default.</param>
	public static string EncodeInterval(double intervalM) =>
		(IntervalsM.Contains(intervalM) ? intervalM : DefaultIntervalM)
			.ToString("0.###", CultureInfo.InvariantCulture);

	/// <summary>
	/// Whether a fix is worth appending to a track whose last point is <paramref name="last"/>.
	/// <para>
	/// Three answers rather than a bool, because the caller has to do something different with
	/// each: append, append and break the segment first, or drop it.
	/// </para>
	/// </summary>
	/// <param name="last">The last point kept, or <c>null</c> when the track is empty.</param>
	/// <param name="fix">The fix the platform just produced.</param>
	/// <param name="intervalM">The rider's chosen interval.</param>
	public static RecordDecision Decide(TrackPoint? last, LocationFix fix, double intervalM)
	{
		if (!double.IsFinite(fix.Latitude) || !double.IsFinite(fix.Longitude)
			|| fix.Latitude is < -90 or > 90 || fix.Longitude is < -180 or > 180)
		{
			return RecordDecision.Drop;
		}

		if (last is not { } previous)
		{
			return RecordDecision.StartSegment;
		}

		// Out of order. Stats are dropped wholesale for a non-monotonic track (§15.3), so one
		// replayed cached point would cost the rider the duration of the whole ride.
		if (previous.TimeUtc is { } previousTime && fix.RecordedUtc < previousTime)
		{
			return RecordDecision.Drop;
		}

		if (previous.TimeUtc is { } since && fix.RecordedUtc - since >= SegmentGap)
		{
			return RecordDecision.StartSegment;
		}

		return Distance.BetweenM(previous, ToPoint(fix)) >= intervalM
			? RecordDecision.Append
			: RecordDecision.Drop;
	}

	/// <summary>
	/// One platform fix as a track point. Speed and heading are not carried: a track is a shape and
	/// a clock, and §15.7 recomputes every speed from the legs so an imported and a recorded ride
	/// cannot disagree about the same number.
	/// </summary>
	/// <param name="fix">The fix as <see cref="ILocationProvider"/> reported it.</param>
	public static TrackPoint ToPoint(LocationFix fix) =>
		new(fix.Latitude, fix.Longitude, null, fix.RecordedUtc);

	/// <summary>
	/// The track with every point inside <paramref name="area"/> removed, and a segment break left
	/// wherever a run of them was (§10.1, §15.3).
	/// <para>
	/// The break is the whole point of doing this here rather than with a <c>Where</c>. Riding out
	/// of a private area and back into it leaves two runs of track with a hole between them, and a
	/// single segment across that hole would draw a straight line through the rider's house — which
	/// is precisely the coordinate the setting exists to keep off other people's screens.
	/// </para>
	/// </summary>
	/// <param name="geometry">The recorded track.</param>
	/// <param name="area">The area to cut out, or <c>null</c> to keep everything.</param>
	public static TrackGeometry WithoutPrivateArea(TrackGeometry geometry, PrivateArea? area)
	{
		if (area is null)
		{
			return geometry;
		}

		HashSet<int> starts = [.. geometry.SegmentStarts];
		List<TrackPoint> kept = new(geometry.Points.Count);
		List<int> keptStarts = [];
		bool breakPending = true;

		for (int index = 0; index < geometry.Points.Count; index++)
		{
			TrackPoint point = geometry.Points[index];

			if (area.Contains(point.Latitude, point.Longitude))
			{
				breakPending = true;

				continue;
			}

			if (breakPending || starts.Contains(index))
			{
				keptStarts.Add(kept.Count);
				breakPending = false;
			}

			kept.Add(point);
		}

		return new TrackGeometry(kept, keptStarts);
	}

	/// <summary>
	/// The in-progress track as one string for <see cref="IDeviceSettings"/>: a format version, the
	/// client identifier the upload is idempotent on (§4.4), and the §15.7 blob in Base64.
	/// <para>
	/// The blob rather than a text encoding of the points, because the store behind this interface
	/// is browser <c>localStorage</c> on one host — a few megabytes for everything the app keeps —
	/// and a day's touring is tens of thousands of points. Gzip is what makes that fit. Reusing the
	/// codec rather than inventing a second one is the same rule §15.7 states about stats: one
	/// implementation, or a recorded track and an imported one quietly stop being the same thing.
	/// </para>
	/// </summary>
	/// <param name="clientGuid">Minted when the track started, and kept across a retry.</param>
	/// <param name="geometry">Everything recorded so far.</param>
	public static string Encode(Guid clientGuid, TrackGeometry geometry)
	{
		using MemoryStream buffer = new();

		TrackBlobCodec.Write(geometry, buffer);

		return string.Join('|', ["1", clientGuid.ToString("N"), Convert.ToBase64String(buffer.ToArray())]);
	}

	/// <summary>
	/// Reads back what <see cref="Encode"/> wrote.
	/// <para>
	/// Anything not wholly readable answers <c>null</c> — "this device has no track in progress" —
	/// on <see cref="PrivateArea.Decode"/>'s reasoning rather than <see cref="RouteStyle.Decode"/>'s:
	/// half a ride recovered from a truncated blob is a shape the rider never rode, and it would be
	/// uploaded under their name without anybody being told.
	/// </para>
	/// </summary>
	/// <param name="stored">A string from <see cref="Encode"/>, or <c>null</c>.</param>
	public static RecordedTrack? Decode(string? stored)
	{
		if (string.IsNullOrWhiteSpace(stored))
		{
			return null;
		}

		string[] parts = stored.Split('|');

		if (parts.Length < 3 || parts[0] != "1" || !Guid.TryParseExact(parts[1], "N", out Guid clientGuid))
		{
			return null;
		}

		try
		{
			using MemoryStream buffer = new(Convert.FromBase64String(parts[2]));

			return new RecordedTrack(clientGuid, TrackBlobCodec.Read(buffer));
		}
		catch (Exception exception) when (exception is FormatException or InvalidDataException or EndOfStreamException)
		{
			return null;
		}
	}
}

/// <summary>What the recorder should do with one fix.</summary>
public enum RecordDecision
{
	/// <summary>Nothing new to keep — too close to the last point, or not a point on the earth.</summary>
	Drop = 0,

	/// <summary>Append it to the segment in progress.</summary>
	Append = 1,

	/// <summary>Append it, and start a new segment at it (§15.3).</summary>
	StartSegment = 2,
}

/// <summary>A track this device is part way through, as it was read back off the device store.</summary>
/// <param name="ClientGuid">Minted when it started; what makes the upload idempotent (§4.4).</param>
/// <param name="Geometry">The points and their segment breaks.</param>
public sealed record RecordedTrack(Guid ClientGuid, TrackGeometry Geometry);
