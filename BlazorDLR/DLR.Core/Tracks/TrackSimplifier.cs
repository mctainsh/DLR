namespace DLR.Core.Tracks;

/// <summary>
/// Ramer–Douglas–Peucker, for the copy that gets drawn (§4.2, §15.5).
/// <para>
/// The map draws a simplified polyline because a 12-hour tour at 1 Hz is ~43 000 points and no
/// renderer wants all of them. The raw track is never replaced by this - the editor addresses
/// full-resolution indices throughout (§15.5), and a simplified copy that anything editable
/// pointed at would delete the wrong points.
/// </para>
/// </summary>
public static class TrackSimplifier
{
	/// <summary>
	/// How far a point may sit from the line before it has to be kept.
	/// <para>
	/// Five metres is below what a rider can distinguish on a map at any zoom that shows a whole
	/// ride, and comfortably inside consumer GPS error - so the simplified line is not a
	/// different shape, it is the same shape with the noise taken out. It is a display
	/// tolerance and nothing derived is computed from the result: distance, ascent and duration
	/// all come from the raw points (§15.7).
	/// </para>
	/// </summary>
	public const double DefaultToleranceM = 5.0;

	/// <summary>Simplifies each segment independently, keeping every segment's endpoints.</summary>
	/// <param name="geometry">The full-resolution track.</param>
	/// <param name="toleranceM">Maximum deviation; defaults to <see cref="DefaultToleranceM"/>.</param>
	public static TrackGeometry Simplify(TrackGeometry geometry, double toleranceM = DefaultToleranceM)
	{
		if (geometry.Points.Count <= 2)
		{
			return geometry;
		}

		List<TrackPoint> kept = [];
		List<int> segmentStarts = [];

		for (int segment = 0; segment < geometry.SegmentCount; segment++)
		{
			int start = geometry.SegmentStarts[segment];

			int end = segment + 1 < geometry.SegmentCount
				? geometry.SegmentStarts[segment + 1]
				: geometry.Points.Count;

			if (kept.Count > 0)
			{
				// A break survives simplification. Joining two segments here would draw a
				// straight line across a tunnel, which is exactly what the break exists to
				// prevent (§15.3).
				segmentStarts.Add(kept.Count);
			}

			kept.AddRange(SimplifySegment(geometry.Points, start, end, toleranceM));
		}

		return new TrackGeometry(kept, segmentStarts);
	}

	private static List<TrackPoint> SimplifySegment(
		IReadOnlyList<TrackPoint> points,
		int start,
		int end,
		double toleranceM)
	{
		int length = end - start;

		if (length <= 2)
		{
			return [.. points.Skip(start).Take(length)];
		}

		bool[] keep = new bool[length];

		keep[0] = true;
		keep[length - 1] = true;

		// Iterative rather than recursive. A pathological track - 43 000 points along a curve
		// that never straightens - recurses to a depth that overflows the stack on a phone,
		// and the failure is a crash rather than a slow render.
		Stack<(int First, int Last)> spans = new();

		spans.Push((0, length - 1));

		while (spans.Count > 0)
		{
			(int first, int last) = spans.Pop();

			double worst = 0;
			int worstIndex = -1;

			for (int index = first + 1; index < last; index++)
			{
				double distance = PerpendicularDistanceM(
					points[start + index],
					points[start + first],
					points[start + last]);

				if (distance > worst)
				{
					worst = distance;
					worstIndex = index;
				}
			}

			if (worstIndex < 0 || worst <= toleranceM)
			{
				continue;
			}

			keep[worstIndex] = true;

			spans.Push((first, worstIndex));
			spans.Push((worstIndex, last));
		}

		List<TrackPoint> simplified = [];

		for (int index = 0; index < length; index++)
		{
			if (keep[index])
			{
				simplified.Add(points[start + index]);
			}
		}

		return simplified;
	}

	/// <summary>
	/// How far a point lies from the segment between two others, in metres.
	/// <para>
	/// The points are projected to a local flat plane centred on the line's start. Over the
	/// tens of metres between consecutive fixes the curvature of the earth is far below the
	/// tolerance, and doing this properly on a sphere would cost trigonometry per candidate
	/// point for an answer that rounds to the same value.
	/// </para>
	/// </summary>
	private static double PerpendicularDistanceM(TrackPoint point, TrackPoint lineStart, TrackPoint lineEnd)
	{
		(double px, double py) = Project(point, lineStart);
		(double ax, double ay) = (0.0, 0.0);
		(double bx, double by) = Project(lineEnd, lineStart);

		double dx = bx - ax;
		double dy = by - ay;

		double lengthSquared = (dx * dx) + (dy * dy);

		if (lengthSquared == 0)
		{
			// The line is a single place - a stationary rider. Distance to the point it is.
			return Math.Sqrt((px * px) + (py * py));
		}

		// Clamped, so a point beyond either end measures to the endpoint rather than to an
		// imaginary extension of the line.
		double t = Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / lengthSquared, 0, 1);

		double nearestX = ax + (t * dx);
		double nearestY = ay + (t * dy);

		return Math.Sqrt(((px - nearestX) * (px - nearestX)) + ((py - nearestY) * (py - nearestY)));
	}

	private static (double X, double Y) Project(TrackPoint point, TrackPoint origin)
	{
		const double metresPerDegree = Math.PI * Distance.EarthRadiusM / 180.0;

		double y = (point.Latitude - origin.Latitude) * metresPerDegree;

		double x = (point.Longitude - origin.Longitude)
			* metresPerDegree
			* Math.Cos(double.DegreesToRadians(origin.Latitude));

		return (x, y);
	}
}
