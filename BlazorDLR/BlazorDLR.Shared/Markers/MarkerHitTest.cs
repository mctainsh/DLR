using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Rides;

namespace BlazorDLR.Shared.Markers;

/// <summary>
/// Which markers a tap on the base map landed on (§16.4).
/// <para>
/// <strong>Why this is not in the overlay.</strong> <c>SkiaMapOverlay</c> draws the pins but
/// is <c>pointer-events: none</c> so gestures reach the base map beneath it - it never sees a
/// tap and cannot hit-test one. The base map raises the tap as lat/lon, and the answer to
/// "what is under the finger" is therefore worked out here, in screen space, against the same
/// projection the overlay drew with.
/// </para>
/// <para>
/// Extracted from the page because a page hosting the overlay cannot render in bUnit - the
/// <c>SKCanvasView</c> is browser-only - and this is the part with arithmetic in it.
/// </para>
/// </summary>
public static class MarkerHitTest
{
	/// <summary>
	/// How close to a drawn pin a tap has to land, in the overlay's canvas pixels.
	/// <para>
	/// Comfortably wider than the 14 px disc the overlay paints, because the thing doing the
	/// tapping is a gloved thumb on a bar-mounted phone. Generous in the other direction too:
	/// catching a neighbour is harmless when the answer is a list of everything nearby rather
	/// than a guess at which one was meant.
	/// </para>
	/// </summary>
	public const double DefaultRadiusPx = 36;

	/// <summary>
	/// Every marker drawn within <paramref name="radiusPx"/> of the tapped point, nearest first.
	/// </summary>
	/// <param name="viewport">The base map's current view. A viewport with no measured canvas
	/// yields nothing - with no pixels there is no "near", and answering anything else would
	/// mean returning every marker on the ride.</param>
	/// <param name="markers">The markers on screen, in wire form (scaled integer degrees).</param>
	/// <param name="click">Where the user tapped, in decimal degrees.</param>
	/// <param name="radiusPx">The hit radius; defaults to <see cref="DefaultRadiusPx"/>.</param>
	/// <returns>The markers under the tap, closest first. Empty when the tap hit bare map.</returns>
	public static IReadOnlyList<MarkerDto> Near(
		MapViewport viewport,
		IEnumerable<MarkerDto> markers,
		MapClick click,
		double radiusPx = DefaultRadiusPx)
	{
		if (viewport.CanvasWidthPx <= 0 || viewport.CanvasHeightPx <= 0)
		{
			return Array.Empty<MarkerDto>();
		}

		CanvasPoint tapped = MapGeometry.ProjectToCanvas(viewport, click.Latitude, click.Longitude);

		return
		[
			.. markers
				.Select(marker => (marker, distance: MapGeometry
					.ProjectToCanvas(
						viewport,
						PositionScale.ToDegrees(marker.Lat),
						PositionScale.ToDegrees(marker.Lon))
					.DistanceTo(tapped)))
				.Where(hit => hit.distance <= radiusPx)
				.OrderBy(hit => hit.distance)
				.Select(hit => hit.marker),
		];
	}
}
