using DLR.Core.Tracks;

namespace DLR.Core.Tests.Tracks;

/// <summary>
/// The box itself, as distinct from the box a track produces (<c>TrackStatsTests</c>).
/// <para>
/// These three members exist for the map-pack picker (§4.2), where a box is the ground one
/// downloadable extract covers: <c>Contains</c> is what turns a tap on a world map into a
/// selection, <c>SpanDeg2</c> orders the several regions a tap can land inside, and
/// <c>IsWellFormed</c> is what a publisher's claim has to survive before either is asked of it.
/// </para>
/// </summary>
public sealed class TrackBoundsTests
{
	/// <summary>Roughly New South Wales, which is the shape of a real catalogue entry.</summary>
	private static readonly TrackBounds Nsw = new(-37.52, 140.99, -28.15, 153.65);

	[Fact]
	public void APointInside_IsContained()
	{
		Nsw.Contains(-33.87, 151.21).ShouldBeTrue("Sydney.");
	}

	[Theory]
	[InlineData(-27.47, 153.03)] // Brisbane — north of the top edge.
	[InlineData(-37.81, 144.96)] // Melbourne — south of the bottom edge.
	[InlineData(-33.87, 115.75)] // West of the western edge.
	public void APointOutside_IsNot(double latitude, double longitude)
	{
		Nsw.Contains(latitude, longitude).ShouldBeFalse();
	}

	/// <summary>
	/// The edges count. A rider pointing at a coastline is pointing at the edge of the extract that
	/// covers it, and an exclusive test would leave a thin band round every region on the map that
	/// selects nothing and reads as a broken tap.
	/// </summary>
	[Fact]
	public void TheEdgesAreInside()
	{
		Nsw.Contains(Nsw.MinLatitude, Nsw.MinLongitude).ShouldBeTrue();
		Nsw.Contains(Nsw.MaxLatitude, Nsw.MaxLongitude).ShouldBeTrue();
		Nsw.Contains(Nsw.MinLatitude, 145.0).ShouldBeTrue();
	}

	/// <summary>
	/// What orders the choices when a tap lands in more than one region: the smallest box containing
	/// a point is the most specific answer to it, and also the smaller download.
	/// </summary>
	[Fact]
	public void SpanRanksTheSmallerBoxFirst()
	{
		TrackBounds australia = new(-43.64, 112.92, -10.06, 153.64);

		Nsw.SpanDeg2.ShouldBeLessThan(australia.SpanDeg2);
	}

	[Fact]
	public void AWellFormedBoxIsAccepted()
	{
		Nsw.IsWellFormed.ShouldBeTrue();
		new TrackBounds(-90, -180, 90, 180).IsWellFormed.ShouldBeTrue("the whole world is a box.");
	}

	/// <summary>
	/// Every one of these comes off a JSON file on a web host (§4.2), so none of them is impossible.
	/// A box that survived would be drawn across the world and answer a tap anywhere on it.
	/// </summary>
	[Theory]
	[InlineData(10.0, 0.0, -10.0, 20.0)] // Latitudes swapped.
	[InlineData(-10.0, 20.0, 10.0, 0.0)] // Longitudes swapped.
	[InlineData(-10.0, 0.0, 91.0, 20.0)] // Off the top of the planet.
	[InlineData(-10.0, -181.0, 10.0, 20.0)] // Off the side of it.
	[InlineData(double.NaN, 0.0, 10.0, 20.0)] // A field that was not a number.
	public void AMalformedBoxIsNot(double minLatitude, double minLongitude, double maxLatitude, double maxLongitude)
	{
		new TrackBounds(minLatitude, minLongitude, maxLatitude, maxLongitude).IsWellFormed.ShouldBeFalse();
	}

	/// <summary>A single point is a legitimate box — degenerate, but nothing here has to refuse it.</summary>
	[Fact]
	public void AZeroSizedBoxIsWellFormed()
	{
		TrackBounds point = new(-33.87, 151.21, -33.87, 151.21);

		point.IsWellFormed.ShouldBeTrue();
		point.Contains(-33.87, 151.21).ShouldBeTrue();
		point.SpanDeg2.ShouldBe(0);
	}
}
