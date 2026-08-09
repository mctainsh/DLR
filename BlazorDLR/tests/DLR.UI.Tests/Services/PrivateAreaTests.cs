using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Services;

/// <summary>
/// The value behind the private area (§10.1, §18.6): what counts as inside it, and the
/// encoding it survives a restart through.
/// <para>
/// Worth asserting rather than eyeballing because both halves fail silently. A containment
/// test that is wrong by a factor publishes a fix from inside the circle, and a round trip
/// that loses a decimal moves the circle off the house it was drawn around — neither shows
/// up as an error anywhere.
/// </para>
/// </summary>
public sealed class PrivateAreaTests
{
	/// <summary>A kilometre circle over inner Sydney — the default radius, somewhere real.</summary>
	private static readonly PrivateArea Home = new(-33.868, 151.209, 1_000);

	[Fact]
	public void Contains_IsTrueAtTheCentre_AndFalseWellOutsideIt()
	{
		Home.Contains(-33.868, 151.209).ShouldBeTrue();

		// ~0.05° of latitude is about 5.5 km — comfortably outside a 1 km circle.
		Home.Contains(-33.918, 151.209).ShouldBeFalse();
	}

	[Fact]
	public void Contains_MeasuresGroundDistance_NotDegrees()
	{
		// 0.005° of longitude at latitude -34 is about 460 m; the same 0.005° of latitude is
		// about 555 m. A degrees-based box would put one of these outside a 500 m circle and
		// the other inside, which is how "500 m" quietly becomes a different distance east-west.
		PrivateArea small = Home with { RadiusM = 500 };

		small.Contains(-33.868, 151.214).ShouldBeTrue("~460 m east is inside a 500 m radius.");
		small.Contains(-33.873, 151.209).ShouldBeFalse("~555 m south is outside it.");
	}

	[Fact]
	public void Contains_TreatsAnUnplaceableFixAsInside()
	{
		// A fix we cannot locate cannot be shown to be outside the area, and it is worth nothing
		// to a map anyway. Suppressing is the only answer that cannot leak.
		Home.Contains(double.NaN, 151.209).ShouldBeTrue();
		Home.Contains(-33.868, double.PositiveInfinity).ShouldBeTrue();
	}

	[Fact]
	public void Normalised_ClampsTheRadius_AndRefusesACentreOffTheEarth()
	{
		(Home with { RadiusM = 5 }).Normalised()!.RadiusM.ShouldBe(PrivateArea.MinRadiusM);
		(Home with { RadiusM = 500_000 }).Normalised()!.RadiusM.ShouldBe(PrivateArea.MaxRadiusM);
		(Home with { RadiusM = double.NaN }).Normalised()!.RadiusM.ShouldBe(PrivateArea.DefaultRadiusM);

		new PrivateArea(91, 0, 1_000).Normalised().ShouldBeNull();
		new PrivateArea(0, 181, 1_000).Normalised().ShouldBeNull();
		new PrivateArea(double.NaN, 0, 1_000).Normalised().ShouldBeNull();
	}

	[Fact]
	public void Encode_RoundTrips_ToTheMetre()
	{
		PrivateArea decoded = PrivateArea.Decode(Home.Encode())!;

		decoded.ShouldNotBeNull();
		decoded.Latitude.ShouldBe(Home.Latitude, tolerance: 1e-6);
		decoded.Longitude.ShouldBe(Home.Longitude, tolerance: 1e-6);
		decoded.RadiusM.ShouldBe(Home.RadiusM);

		// The centre is a house. Six decimal places is ~0.1 m, so a save-and-reload must not
		// move the circle by anything a person could notice.
		decoded.Contains(Home.Latitude, Home.Longitude).ShouldBeTrue();
	}

	[Fact]
	public void Encode_IsCultureInvariant()
	{
		// The device store is a string store, and a comma decimal separator written on one
		// device and read on another is how a Sydney circle lands in the Atlantic.
		Home.Encode().ShouldContain(".");
		Home.Encode().ShouldNotContain(",");
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("2|-33.868|151.209|1000")]   // a version this build does not know
	[InlineData("1|-33.868|151.209")]        // truncated
	[InlineData("1|south|151.209|1000")]     // unparseable centre
	[InlineData("1|-33.868|151.209|wide")]   // unparseable radius
	[InlineData("1|-91|151.209|1000")]       // off the earth
	public void Decode_AnythingNotWhollyReadable_MeansNoArea(string? stored)
	{
		// Deliberately unlike RouteStyle.Decode, which repairs field by field. A half-recovered
		// circle would sit somewhere the rider never put one — silently protecting the wrong
		// place is worse than visibly having lost the setting.
		PrivateArea.Decode(stored).ShouldBeNull();
	}

	[Fact]
	public void Decode_ClampsARadiusStoredOutsideTheOfferedRange()
	{
		PrivateArea.Decode("1|-33.868|151.209|999999")!.RadiusM.ShouldBe(PrivateArea.MaxRadiusM);
	}
}
