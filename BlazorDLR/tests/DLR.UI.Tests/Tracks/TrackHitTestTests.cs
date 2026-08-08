using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Tracks;
using DLR.Core.Tracks;

namespace DLR.UI.Tests.Tracks;

/// <summary>
/// Which point of the track a tap landed on (§15.5). The cursor is placed by pointing, so the
/// answer has to be "the one under the finger" in screen pixels — and a tap that hit bare map
/// has to be no answer at all rather than the nearest end of the ride.
/// </summary>
public sealed class TrackHitTestTests
{
	/// <summary>A degree either way across a 1000 px canvas: 500 px per degree, north-up.</summary>
	private static readonly MapViewport UnitViewport = new(
		TopLeftLatitude: 1, TopLeftLongitude: -1,
		BottomRightLatitude: -1, BottomRightLongitude: 1,
		ZoomLevel: 12, HeadingDeg: 0,
		CanvasWidthPx: 1000, CanvasHeightPx: 1000, DevicePixelRatio: 1);

	private static readonly TrackPoint[] Line =
	[
		new(0, -0.4),
		new(0, -0.2),
		new(0, 0),
		new(0, 0.2),
		new(0, 0.4),
	];

	[Fact]
	public void Nearest_AnswersThePointUnderTheTap()
	{
		TrackHitTest.Nearest(UnitViewport, Line, new MapClick(0, 0.19)).ShouldBe(3);
		TrackHitTest.Nearest(UnitViewport, Line, new MapClick(0, -0.4)).ShouldBe(0);
	}

	[Fact]
	public void Nearest_IsNothing_WhenTheTapMissedTheLine()
	{
		// 0.5° north of a line that runs along the equator: 250 px at this viewport, far outside
		// the hit radius. The cursor must stay where it was rather than jumping to an endpoint.
		TrackHitTest.Nearest(UnitViewport, Line, new MapClick(0.5, 0)).ShouldBeNull();
	}

	[Fact]
	public void Nearest_IsNothing_BeforeTheBaseMapHasReportedPixels()
	{
		MapViewport unmeasured = UnitViewport with { CanvasWidthPx = 0, CanvasHeightPx = 0 };

		TrackHitTest.Nearest(unmeasured, Line, new MapClick(0, 0)).ShouldBeNull(
			"with no pixels there is no 'near', and answering anything would place the cursor blind.");
	}

	[Fact]
	public void Nearest_IsNothing_OnAnEmptyLine()
	{
		TrackHitTest.Nearest(UnitViewport, Array.Empty<TrackPoint>(), new MapClick(0, 0)).ShouldBeNull();
	}

	[Fact]
	public void Nearest_MeasuresInPixels_SoZoomChangesWhatCounts()
	{
		// One tenth of a degree off the line is 50 px here — inside the 36 px radius? No.
		TrackHitTest.Nearest(UnitViewport, Line, new MapClick(0.1, 0)).ShouldBeNull();

		// The same tap on a view ten times wider is 5 px off, and hits.
		MapViewport zoomedOut = UnitViewport with
		{
			TopLeftLatitude = 10,
			TopLeftLongitude = -10,
			BottomRightLatitude = -10,
			BottomRightLongitude = 10,
		};

		TrackHitTest.Nearest(zoomedOut, Line, new MapClick(0.1, 0)).ShouldBe(2,
			"a tolerance in metres would make the line unhittable zoomed out and huge zoomed in.");
	}
}
