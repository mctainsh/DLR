namespace DLR.Core.Tracks;

/// <summary>
/// The numbers a track is described by (§15.1, §15.7).
/// <para>
/// One implementation, used by the recorder, the importer and the editor alike. Three copies of
/// "compute the stats" is how a track's ascent comes out different depending on where it came
/// from - and the person it looks wrong to is the one who rode it.
/// </para>
/// <para>
/// Every time-derived figure is nullable, and null is not zero. A track with no timestamps is a
/// route rather than a ride: it has a distance and a shape, and rendering its duration as
/// <c>0</c> would claim a measurement nobody took (§8).
/// </para>
/// </summary>
/// <param name="DistanceM">Metres along the ground, never summed across a segment break.</param>
/// <param name="AscentM">
/// Metres climbed, or null when the file carried no elevation. No interpolation and no DEM
/// lookup: inventing elevation adds a paid dependency and a failure mode for a number nobody
/// is checking (§15.1).
/// </param>
/// <param name="DurationS">Seconds, or null for a timeless or non-monotonic track.</param>
/// <param name="MaxSpeedMps">Fastest leg, or null on the same terms as duration.</param>
/// <param name="StartedUtc">First timestamp, if any.</param>
/// <param name="EndedUtc">Last timestamp, if any.</param>
/// <param name="PointCount">Points in the track.</param>
/// <param name="SegmentCount">Segments in the track.</param>
/// <param name="Bounds">The box the track fits in, or null when it has no points.</param>
public sealed record TrackStats(
	double DistanceM,
	double? AscentM,
	double? DurationS,
	double? MaxSpeedMps,
	DateTimeOffset? StartedUtc,
	DateTimeOffset? EndedUtc,
	int PointCount,
	int SegmentCount,
	TrackBounds? Bounds)
{
	/// <summary>
	/// Elevation change below this is noise, not climbing.
	/// <para>
	/// GPS altitude wanders by several metres while standing still, so summing every rise
	/// produces a number that grows with how long the ride took rather than with how much of
	/// it was uphill. §15.7 requires the importer and the editor to use the recorder's
	/// threshold unchanged - an edited track whose untouched half reports different climbing
	/// reads as data corruption to the person who owns the ride.
	/// </para>
	/// </summary>
	public const double AscentNoiseThresholdM = 3.0;

	/// <summary>Computes every figure from a track's shape.</summary>
	/// <param name="geometry">The points and their segment breaks.</param>
	public static TrackStats From(TrackGeometry geometry)
	{
		if (geometry.Points.Count == 0)
		{
			return new TrackStats(0, null, null, null, null, null, 0, 0, null);
		}

		bool hasElevation = geometry.Points.Any(point => point.ElevationM is not null);

		// Times are usable only if every point has one *and* they never go backwards. §15.3 is
		// explicit that geometry wins over the clock: reordering points to satisfy a timestamp
		// would silently change the shape of the ride, which is far worse than losing a
		// duration, so the stats are dropped instead.
		bool timesUsable = geometry.Points.All(point => point.TimeUtc is not null)
			&& IsMonotonic(geometry.Points);

		double distance = 0;
		double? maxSpeed = null;

		foreach ((TrackPoint from, TrackPoint to) in geometry.Legs())
		{
			double leg = Distance.BetweenM(from, to);

			distance += leg;

			if (!timesUsable)
			{
				continue;
			}

			double seconds = (to.TimeUtc!.Value - from.TimeUtc!.Value).TotalSeconds;

			if (seconds > 0)
			{
				maxSpeed = Math.Max(maxSpeed ?? 0, leg / seconds);
			}
		}

		return new TrackStats(
			distance,
			hasElevation ? Ascent(geometry) : null,
			timesUsable ? Duration(geometry) : null,
			timesUsable ? maxSpeed : null,
			timesUsable ? geometry.Points[0].TimeUtc : null,
			timesUsable ? geometry.Points[^1].TimeUtc : null,
			geometry.Points.Count,
			geometry.SegmentCount,
			TrackBounds.Around(geometry.Points));
	}

	/// <summary>
	/// Climbing, counted only once a rise clears the noise threshold.
	/// <para>
	/// Tracked against a running reference rather than point to point, so a long steady climb
	/// made of half-metre steps still counts - a naive per-point threshold would discard all
	/// of it.
	/// </para>
	/// </summary>
	private static double Ascent(TrackGeometry geometry)
	{
		double total = 0;
		double? reference = null;

		foreach (TrackPoint point in geometry.Points)
		{
			if (point.ElevationM is not { } elevation)
			{
				continue;
			}

			if (reference is not { } from)
			{
				reference = elevation;

				continue;
			}

			if (elevation - from >= AscentNoiseThresholdM)
			{
				total += elevation - from;
				reference = elevation;
			}
			else if (elevation < from)
			{
				// Descending moves the reference down immediately. Waiting for a threshold on
				// the way down would let a descent bank credit for the climb that follows it.
				reference = elevation;
			}
		}

		return total;
	}

	/// <summary>Seconds ridden, summed per segment so a pause is not counted as riding.</summary>
	private static double Duration(TrackGeometry geometry)
	{
		double seconds = 0;

		foreach ((TrackPoint from, TrackPoint to) in geometry.Legs())
		{
			seconds += (to.TimeUtc!.Value - from.TimeUtc!.Value).TotalSeconds;
		}

		return seconds;
	}

	private static bool IsMonotonic(IReadOnlyList<TrackPoint> points)
	{
		for (int index = 1; index < points.Count; index++)
		{
			if (points[index].TimeUtc < points[index - 1].TimeUtc)
			{
				return false;
			}
		}

		return true;
	}
}

/// <summary>The box a track fits inside, for map framing and for a cheap overlap test.</summary>
/// <param name="MinLatitude">Southern edge.</param>
/// <param name="MinLongitude">Western edge.</param>
/// <param name="MaxLatitude">Northern edge.</param>
/// <param name="MaxLongitude">Eastern edge.</param>
public readonly record struct TrackBounds(
	double MinLatitude,
	double MinLongitude,
	double MaxLatitude,
	double MaxLongitude)
{
	/// <summary>The smallest box containing every point, or null when there are none.</summary>
	/// <param name="points">The points to bound.</param>
	public static TrackBounds? Around(IReadOnlyList<TrackPoint> points) =>
		points.Count == 0
			? null
			: new TrackBounds(
				points.Min(point => point.Latitude),
				points.Min(point => point.Longitude),
				points.Max(point => point.Latitude),
				points.Max(point => point.Longitude));

	/// <summary>
	/// The smallest box containing every one of <paramref name="boxes"/>, or null when there are
	/// none.
	/// <para>
	/// What a group ride is framed on. A ride carries a <em>set</em> of planned routes rather than
	/// one (§5.4) - the short option and the long one, the way out and the way home - so opening
	/// on the first of them would put the rest off the screen.
	/// </para>
	/// <para>
	/// Naive across the antimeridian in exactly the way the point overload above is, and for the
	/// same reason: a box built from minima and maxima cannot describe one that wraps, so nothing
	/// that produces a <see cref="TrackBounds"/> can hand one to this.
	/// </para>
	/// </summary>
	/// <param name="boxes">The boxes to cover.</param>
	public static TrackBounds? Around(IReadOnlyList<TrackBounds> boxes) =>
		boxes.Count == 0
			? null
			: new TrackBounds(
				boxes.Min(box => box.MinLatitude),
				boxes.Min(box => box.MinLongitude),
				boxes.Max(box => box.MaxLatitude),
				boxes.Max(box => box.MaxLongitude));

	/// <summary>
	/// Whether a point falls inside this box, edges included.
	/// <para>
	/// Inclusive because the boxes this answers for are map-pack extents (§4.2), and a rider
	/// pointing at a coast is pointing at an edge - refusing the boundary would leave a thin band
	/// along every extract that selects nothing and looks like a broken tap.
	/// </para>
	/// <para>
	/// Naive across the antimeridian, in the same way <c>Around</c> is: a box built by taking the
	/// minimum and maximum of a set of longitudes cannot describe one that wraps, so nothing that
	/// produces a <see cref="TrackBounds"/> can hand this one to test against.
	/// </para>
	/// </summary>
	/// <param name="latitudeDeg">Latitude in decimal degrees.</param>
	/// <param name="longitudeDeg">Longitude in decimal degrees.</param>
	public bool Contains(double latitudeDeg, double longitudeDeg) =>
		latitudeDeg >= MinLatitude && latitudeDeg <= MaxLatitude
		&& longitudeDeg >= MinLongitude && longitudeDeg <= MaxLongitude;

	/// <summary>
	/// How much of the world the box covers, in square degrees.
	/// <para>
	/// A comparison key and nothing else - degrees of longitude shrink toward the poles, so this
	/// is not an area on the ground. What it is used for is ordering boxes that all contain the
	/// same point, where the ranking is what matters and any monotonic measure gives the same one.
	/// </para>
	/// </summary>
	public double SpanDeg2 => (MaxLatitude - MinLatitude) * (MaxLongitude - MinLongitude);

	/// <summary>
	/// Whether this is a box a map can be framed on: real numbers, in range, and not inside out.
	/// <para>
	/// Asked of the catalogue's bounds (§4.2), which are a publisher's claim like everything else
	/// in it - an entry whose corners are swapped, or which carries a longitude of 1000, would
	/// otherwise be drawn as a box across the whole world and be selectable by a tap anywhere.
	/// </para>
	/// </summary>
	public bool IsWellFormed =>
		double.IsFinite(MinLatitude) && double.IsFinite(MinLongitude)
		&& double.IsFinite(MaxLatitude) && double.IsFinite(MaxLongitude)
		&& MinLatitude <= MaxLatitude && MinLongitude <= MaxLongitude
		&& MinLatitude >= -90 && MaxLatitude <= 90
		&& MinLongitude >= -180 && MaxLongitude <= 180;
}

/// <summary>Distance over the earth's surface.</summary>
public static class Distance
{
	/// <summary>Mean earth radius, metres. The figure the haversine formula is quoted with.</summary>
	public const double EarthRadiusM = 6_371_008.8;

	/// <summary>
	/// Great-circle distance between two points.
	/// <para>
	/// Haversine rather than Vincenty: the error against an ellipsoid is a few metres in a
	/// thousand kilometres, and every leg here is tens of metres. Spending accuracy nobody can
	/// perceive on a formula that has to iterate would be a poor trade on a phone.
	/// </para>
	/// </summary>
	/// <param name="from">Start of the leg.</param>
	/// <param name="to">End of the leg.</param>
	public static double BetweenM(TrackPoint from, TrackPoint to)
	{
		double fromLatitude = double.DegreesToRadians(from.Latitude);
		double toLatitude = double.DegreesToRadians(to.Latitude);

		double deltaLatitude = toLatitude - fromLatitude;
		double deltaLongitude = double.DegreesToRadians(to.Longitude - from.Longitude);

		double a = (Math.Sin(deltaLatitude / 2) * Math.Sin(deltaLatitude / 2))
			+ (Math.Cos(fromLatitude) * Math.Cos(toLatitude)
				* Math.Sin(deltaLongitude / 2) * Math.Sin(deltaLongitude / 2));

		return 2 * EarthRadiusM * Math.Asin(Math.Min(1, Math.Sqrt(a)));
	}
}
