using BlazorDLR.Shared.Services;
using DLR.Core.Tracks;

namespace BlazorDLR.Shared.Tracks;

/// <summary>
/// Which point of a track a tap on the base map landed on (§15.5).
/// <para>
/// The sibling of <see cref="Markers.MarkerHitTest"/>, and for the same reason: the overlay
/// draws the line but is <c>pointer-events: none</c>, so it never sees the tap. The base map
/// raises the tap as lat/lon and the answer is worked out here, in screen space, through the
/// projection the overlay drew with.
/// </para>
/// <para>
/// <strong>Screen space, not ground distance.</strong> A tap is aimed at something the eye can
/// see, so "near" has to mean near the drawn line - a metre tolerance would make the track
/// impossible to hit when zoomed out and trivially hittable when zoomed in.
/// </para>
/// </summary>
public static class TrackHitTest
{
	/// <summary>
	/// How close to the line a tap has to land, in canvas pixels.
	/// <para>
	/// Wider than the 4 px the overlay strokes the route at, because the thing doing the tapping
	/// is a thumb and the thing being tapped is a line. Missing entirely is the safe outcome -
	/// the cursor stays where it was rather than jumping to whichever end of the ride happened
	/// to be closest to a tap on bare map.
	/// </para>
	/// </summary>
	public const double DefaultRadiusPx = 36;

	/// <summary>
	/// The point nearest the tap, as a position in <paramref name="points"/>, or null when the
	/// tap landed further than <paramref name="radiusPx"/> from every one of them.
	/// </summary>
	/// <param name="viewport">The base map's current view. Without measured pixels there is no
	/// "near", so an unmeasured canvas hits nothing.</param>
	/// <param name="points">The points to test, normally the surviving ones.</param>
	/// <param name="click">Where the rider tapped, in decimal degrees.</param>
	/// <param name="radiusPx">The hit radius; defaults to <see cref="DefaultRadiusPx"/>.</param>
	public static int? Nearest(
		MapViewport viewport,
		IReadOnlyList<TrackPoint> points,
		MapClick click,
		double radiusPx = DefaultRadiusPx)
	{
		if (viewport.CanvasWidthPx <= 0 || viewport.CanvasHeightPx <= 0)
		{
			return null;
		}

		CanvasPoint tapped = MapGeometry.ProjectToCanvas(viewport, click.Latitude, click.Longitude);

		int nearest = -1;
		double nearestDistance = double.MaxValue;

		// A plain scan. A 12-hour tour is ~43 000 points (§15.5) and this runs once per tap, so
		// the projection cost is a millisecond or so against a gesture - an index would be more
		// code to keep correct through every trim for no perceptible gain.
		for (int index = 0; index < points.Count; index++)
		{
			double distance = MapGeometry
				.ProjectToCanvas(viewport, points[index].Latitude, points[index].Longitude)
				.DistanceTo(tapped);

			if (distance < nearestDistance)
			{
				nearestDistance = distance;
				nearest = index;
			}
		}

		return nearest >= 0 && nearestDistance <= radiusPx ? nearest : null;
	}
}
