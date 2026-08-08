using BlazorDLR.Shared.Markers;
using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Rides;

namespace DLR.UI.Tests.Components;

/// <summary>
/// Tapping the map to ask what is under the finger (§16.4).
/// <para>
/// The overlay that draws the pins is <c>pointer-events: none</c> and cannot render in bUnit
/// anyway, so the projection and the hit radius are asserted here rather than eyeballed. A
/// hit test that is wrong by a rotation looks perfectly plausible on a north-up screenshot and
/// then misses every marker the moment somebody turns the map.
/// </para>
/// </summary>
public sealed class MarkerHitTestTests
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	/// <summary>
	/// A square view one degree across centred on the origin, 1000 px on a side — so 0.001° is
	/// 1 px, which makes the distances in these tests arithmetic anybody can check.
	/// </summary>
	private static MapViewport Viewport(double headingDeg = 0) => new(
		TopLeftLatitude: 0.5, TopLeftLongitude: -0.5,
		BottomRightLatitude: -0.5, BottomRightLongitude: 0.5,
		ZoomLevel: 12,
		HeadingDeg: headingDeg,
		CanvasWidthPx: 1000, CanvasHeightPx: 1000,
		DevicePixelRatio: 1);

	private static MarkerDto At(double latitudeDeg, double longitudeDeg, string title) => new(
		Id: Guid.NewGuid(),
		TrackId: null,
		GroupRideId: null,
		Lat: PositionScale.FromDegrees(latitudeDeg),
		Lon: PositionScale.FromDegrees(longitudeDeg),
		Icon: "gravel",
		Title: title,
		Note: null,
		DirectionDeg: null,
		PhotoId: null,
		CreatedByUserId: Guid.NewGuid(),
		CreatedByUserName: "Alice",
		CreatedUtc: FixedInstant,
		UpdatedUtc: FixedInstant);

	[Fact]
	public void ATapOnThePin_FindsIt()
	{
		MarkerDto marker = At(0, 0, "Gravel");

		IReadOnlyList<MarkerDto> hits = MarkerHitTest.Near(
			Viewport(), [marker], new MapClick(0, 0));

		hits.Count.ShouldBe(1);
		hits[0].Title.ShouldBe("Gravel");
	}

	[Fact]
	public void ATapOnBareMap_FindsNothing()
	{
		MarkerDto marker = At(0, 0, "Gravel");

		// 0.2° east on this viewport is 200 px — far outside any sane finger.
		MarkerHitTest.Near(Viewport(), [marker], new MapClick(0, 0.2)).ShouldBeEmpty(
			"§16.4: a tap on empty map is a tap on empty map — it must not drag in the nearest " +
			"marker on the ride from half a screen away.");
	}

	[Fact]
	public void EveryMarkerUnderTheTap_IsReturned_NearestFirst()
	{
		// 0.005° = 5 px away, 0.02° = 20 px away, 0.1° = 100 px away.
		MarkerDto near = At(0, 0.005, "Near");
		MarkerDto middle = At(0, 0.02, "Middle");
		MarkerDto far = At(0, 0.1, "Far");

		IReadOnlyList<MarkerDto> hits = MarkerHitTest.Near(
			Viewport(), [far, middle, near], new MapClick(0, 0));

		hits.Select(hit => hit.Title).ShouldBe(["Near", "Middle"],
			"Markers pile up at low zoom, so the answer is the list — ordered nearest first, " +
			"because the one the finger was aimed at should be the one read first.");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(37)]
	[InlineData(90)]
	[InlineData(210)]
	public void TheHitRadius_SurvivesARotatedMap(double headingDeg)
	{
		// Rotation is an isometry about the canvas centre, so a marker 5 px from the tap is
		// still 5 px from it however the base map is turned. This is the property that would
		// break first if the page ever grew its own copy of the projection.
		MarkerDto marker = At(0, 0.005, "Near");

		MarkerHitTest.Near(Viewport(headingDeg), [marker], new MapClick(0, 0)).Count.ShouldBe(1,
			$"a tap must still find the pin it landed on with the map rotated to {headingDeg}°.");
	}

	[Fact]
	public void AnUnmeasuredCanvas_FindsNothing()
	{
		MapViewport unmeasured = Viewport() with { CanvasWidthPx = 0, CanvasHeightPx = 0 };

		MarkerHitTest.Near(unmeasured, [At(0, 0, "Gravel")], new MapClick(0, 0)).ShouldBeEmpty(
			"With no pixels there is no 'near'. Every marker projects to the centre of a " +
			"degenerate viewport, so answering at all would return the whole ride.");
	}

	[Fact]
	public void TheRadius_IsScreenDistance_NotGroundDistance()
	{
		// The same marker, the same ground distance from the tap, on two viewports an order of
		// magnitude apart in zoom. Zoomed in it is off-screen-far; zoomed out it is under the
		// finger. A hit radius in metres would get this backwards.
		MarkerDto marker = At(0, 0.05, "Gravel");

		MapViewport zoomedIn = Viewport();
		MapViewport zoomedOut = new(
			TopLeftLatitude: 5, TopLeftLongitude: -5,
			BottomRightLatitude: -5, BottomRightLongitude: 5,
			ZoomLevel: 8, HeadingDeg: 0,
			CanvasWidthPx: 1000, CanvasHeightPx: 1000, DevicePixelRatio: 1);

		MarkerHitTest.Near(zoomedIn, [marker], new MapClick(0, 0)).ShouldBeEmpty();
		MarkerHitTest.Near(zoomedOut, [marker], new MapClick(0, 0)).Count.ShouldBe(1);
	}
}
