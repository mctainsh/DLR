namespace DLR.Core.Tracks;

/// <summary>
/// Where a rider is relative to a planned route (§5.4).
/// <para>
/// Two answers per rider, both derived from the same projection: how far along the route
/// they have got (distance-along) and how far off it they are (distance-off). The gap
/// between two riders is the difference of their distance-along, so "who is behind whom"
/// falls out of one function.
/// </para>
/// <para>
/// <strong>Pure geometry, no I/O.</strong> Sits beside <see cref="TrackStats"/> because
/// that is where <see cref="Distance.BetweenM"/> lives, and pointing at three different
/// distance formulas across the codebase is how the numbers stop agreeing (§15.7).
/// </para>
/// </summary>
public static class GapCalculator
{
	/// <summary>
	/// Projects one point onto a route, returning distance-along and distance-off.
	/// </summary>
	/// <param name="route">
	/// The planned route's points, in order. Two or more; a route with one point is a
	/// coordinate, not a line, and cannot describe "along" or "off".
	/// </param>
	/// <param name="point">Where the rider is.</param>
	/// <returns>
	/// <see cref="RouteProjection.AlongMetres"/> is the arc-distance from the route's first
	/// point to the projected point on the closest segment.
	/// <see cref="RouteProjection.OffMetres"/> is the shortest distance from the rider's
	/// point to that same projected point.
	/// Returns null when the route is too short to project against.
	/// </returns>
	public static RouteProjection? Project(IReadOnlyList<TrackPoint> route, TrackPoint point)
	{
		if (route is null || route.Count < 2)
		{
			return null;
		}

		// Cumulative arc-distance from the start of the route to each vertex. Precomputing
		// spares one BetweenM call per segment on every projection — negligible for one
		// rider, real for a 50-member ride with a 40 000-point route.
		//
		// Not cached across calls because callers own the route: passing an updated route
		// after an edit is exactly the case where a stale cache would return a phantom
		// distance-along, so the projection recomputes from the argument each time.
		double bestOff = double.MaxValue;
		double bestAlong = 0;
		double cumulative = 0;

		for (int i = 0; i < route.Count - 1; i++)
		{
			TrackPoint a = route[i];
			TrackPoint b = route[i + 1];
			double legLength = Distance.BetweenM(a, b);

			// Skip zero-length legs so the projection does not divide by zero. A route made
			// of duplicated points is unusual but not impossible — an exporter can emit them.
			if (legLength <= 0)
			{
				continue;
			}

			// Project the point onto the segment in a flat local frame: a metres-per-degree
			// scale at the leg's mean latitude is accurate enough for legs a rider covers in
			// seconds. Great-circle segment projection is a solved problem but it costs a
			// couple of trig calls per leg and the difference is imperceptible below several
			// kilometres per leg — which is the entire regime this operates in.
			double meanLat = (a.Latitude + b.Latitude) * 0.5;
			double metresPerDegLat = 111_132.0;
			double metresPerDegLon = 111_320.0 * Math.Cos(meanLat * Math.PI / 180.0);

			double ax = 0;
			double ay = 0;
			double bx = (b.Longitude - a.Longitude) * metresPerDegLon;
			double by = (b.Latitude - a.Latitude) * metresPerDegLat;
			double px = (point.Longitude - a.Longitude) * metresPerDegLon;
			double py = (point.Latitude - a.Latitude) * metresPerDegLat;

			double dx = bx - ax;
			double dy = by - ay;
			double lengthSquared = (dx * dx) + (dy * dy);

			// Fraction along [0,1] of the projected foot; clamped so a point past either end
			// snaps to the vertex, which is what "distance-along" and "distance-off" mean at
			// a corner.
			double t = 0;
			if (lengthSquared > 0)
			{
				t = ((px * dx) + (py * dy)) / lengthSquared;
				t = Math.Max(0, Math.Min(1, t));
			}

			double footX = ax + (t * dx);
			double footY = ay + (t * dy);
			double offX = px - footX;
			double offY = py - footY;
			double off = Math.Sqrt((offX * offX) + (offY * offY));

			if (off < bestOff)
			{
				bestOff = off;
				bestAlong = cumulative + (t * legLength);
			}

			cumulative += legLength;
		}

		return bestOff == double.MaxValue
			? null
			: new RouteProjection(bestAlong, bestOff);
	}

	/// <summary>
	/// The gap in metres from <paramref name="behind"/> to <paramref name="ahead"/> along
	/// the route. A negative return means <paramref name="behind"/> is actually further
	/// along than <paramref name="ahead"/>, which the UI renders as "ahead by".
	/// </summary>
	public static double GapMetres(RouteProjection behind, RouteProjection ahead) =>
		ahead.AlongMetres - behind.AlongMetres;
}

/// <summary>The result of projecting a point onto a route (§5.4).</summary>
/// <param name="AlongMetres">Arc-distance from the route's first vertex to the projected foot.</param>
/// <param name="OffMetres">Shortest distance from the point to that foot — the "off-route" number.</param>
public readonly record struct RouteProjection(double AlongMetres, double OffMetres);
