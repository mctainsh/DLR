using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Services;

/// <summary>
/// The two rules behind a rider's marker on the live map (§5.3, §16.3): arrow or dot, and how much
/// of a name the label carries.
/// <para>
/// Extracted from <c>SkiaMapOverlay</c> for the reason <see cref="MapGeometry"/> was: the overlay's
/// <c>SKCanvasView</c> cannot render outside a browser, so a rule left inside it is a rule nobody
/// can assert.
/// </para>
/// </summary>
public sealed class RiderMarkerTests
{
	[Fact]
	public void Moving_NeedsBothASpeedAndAHeading()
	{
		RiderMarker.IsMoving(speedMps: 8, headingDeg: 90).ShouldBeTrue();

		RiderMarker.IsMoving(speedMps: 8, headingDeg: null).ShouldBeFalse(
			"a speed with no heading has nothing to point along.");

		RiderMarker.IsMoving(speedMps: null, headingDeg: 90).ShouldBeFalse(
			"a heading with no speed is the compass reading of somebody parked at a café — an " +
			"arrow drawn from it sends whoever is looking for them down a road nobody is on.");
	}

	/// <summary>
	/// Zero is a real bearing — due north (§16.2) — so it must not be mistaken for "no heading".
	/// </summary>
	[Fact]
	public void Moving_HeadingOfZero_IsAHeading() =>
		RiderMarker.IsMoving(speedMps: 8, headingDeg: 0).ShouldBeTrue();

	[Theory]
	[InlineData(0)]
	[InlineData(0.4999)]
	public void Moving_BelowWalkingPace_IsStopped(double speedMps) =>
		RiderMarker.IsMoving(speedMps, headingDeg: 90).ShouldBeFalse(
			"below 1 m/s the heading is GPS drift at a junction, not a direction of travel.");

	[Fact]
	public void Moving_AtTheThreshold_IsMoving() =>
		RiderMarker.IsMoving(RiderMarker.MovingAtLeastMps, headingDeg: 90).ShouldBeTrue();

	[Fact]
	public void Label_AName_IsUsedAsIs() => RiderMarker.Label("DaveSmith").ShouldBe("DaveSmith");

	[Fact]
	public void Label_IsTrimmed() => RiderMarker.Label("  DaveSmith  ").ShouldBe("DaveSmith");

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void Label_NoNameYet_IsAQuestionMark(string? name) =>
		RiderMarker.Label(name).ShouldBe("?",
			"a fix can arrive before the member list does, and an empty pill names nobody.");

	[Fact]
	public void Label_AnOverlongName_IsCroppedVisibly()
	{
		string label = RiderMarker.Label(new string('x', 50));

		label.Length.ShouldBe(RiderMarker.LabelMaxChars,
			"the label is drawn to the right of the traveller; an unbounded one is a banner across the map.");

		label.ShouldEndWith("…", customMessage:
			"a crop that is not visibly a crop reads as a different traveller with a shorter name.");
	}
}
