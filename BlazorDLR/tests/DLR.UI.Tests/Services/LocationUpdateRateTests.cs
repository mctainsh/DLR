using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Services;

/// <summary>
/// The three numbers a rider sets on the Location screen (§4.2), and the one rule that binds them.
/// <para>
/// The invariant is the reason this type exists rather than three loose fields: "the maximum is
/// longer than the minimum" is checked in one place, so the settings screen, the device store and
/// the Android service extra all get it for free.
/// </para>
/// </summary>
public sealed class LocationUpdateRateTests
{
	[Fact]
	public void TheDefault_IsWhatTheScreenSays()
	{
		LocationUpdateRate.Default.DistanceM.ShouldBe(25);
		LocationUpdateRate.Default.Maximum.ShouldBe(TimeSpan.FromSeconds(60));
		LocationUpdateRate.Default.Minimum.ShouldBe(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public void AValueThatIsNotOnTheList_SnapsToTheNearestOneThatIs()
	{
		// Nothing in the app offers these, but a device store is a text file somebody can edit and
		// an Intent extra survives an app upgrade. A rate built from either is still one of ours.
		LocationUpdateRate rate = new(23, TimeSpan.FromSeconds(70), TimeSpan.FromSeconds(4));

		rate.DistanceM.ShouldBe(25);
		rate.Maximum.ShouldBe(TimeSpan.FromSeconds(60));
		rate.Minimum.ShouldBe(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public void AMaximumUnderTheMinimum_CannotBeBuilt()
	{
		// The rule the rider is told about, made structural: 10 s is on the maximum list, but not
		// while the floor is 30 s, so the nearest legal one is taken instead.
		LocationUpdateRate rate = new(25, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));

		rate.Minimum.ShouldBe(TimeSpan.FromSeconds(30));
		rate.Maximum.ShouldBeGreaterThan(rate.Minimum);
		rate.Maximum.ShouldBe(TimeSpan.FromSeconds(60));
	}

	[Fact]
	public void RaisingTheMinimumPastTheMaximum_TakesTheMaximumUpWithIt()
	{
		// What the settings screen does when a rider moves the floor above the ceiling. Refusing
		// would leave them stuck behind a control that does nothing when they use it.
		LocationUpdateRate rate = LocationUpdateRate.Default.WithMinimum(TimeSpan.FromSeconds(60));

		rate.Minimum.ShouldBe(TimeSpan.FromSeconds(60));
		rate.Maximum.ShouldBe(TimeSpan.FromSeconds(120));
	}

	[Fact]
	public void EveryOfferedMinimum_LeavesALegalMaximum()
	{
		// The lists have to stay compatible: a floor with no ceiling above it would make the
		// constructor pick from an empty set.
		foreach (TimeSpan minimum in LocationUpdateRate.Minimums)
		{
			LocationUpdateRate rate = LocationUpdateRate.Default.WithMinimum(minimum);

			rate.Minimum.ShouldBe(minimum);
			rate.Maximum.ShouldBeGreaterThan(minimum, $"a {minimum.TotalSeconds:0} s floor has no ceiling above it.");
		}
	}

	[Fact]
	public void EveryOfferedValue_SurvivesTheRoundTripThroughTheDeviceStore()
	{
		foreach (double distance in LocationUpdateRate.Distances)
		{
			foreach (TimeSpan minimum in LocationUpdateRate.Minimums)
			{
				LocationUpdateRate rate = new(distance, TimeSpan.FromMinutes(10), minimum);

				LocationUpdateRate.Decode(rate.Encode()).ShouldBe(rate);
			}
		}
	}

	[Theory]
	[InlineData("Eco", 50, 60, 10)]
	[InlineData("Balanced", 10, 30, 5)]
	[InlineData("Precise", 5, 10, 2)]
	public void TheProfileThisReplaced_IsCarriedAcross_NotDropped(
		string stored,
		double distance,
		int maximum,
		int minimum)
	{
		// The key is the one the profile used, so every phone that has ever chosen has a value in
		// it. Dropping it would silently move a rider who picked Precise for track days onto a
		// default four times coarser, with nothing on screen to say why.
		LocationUpdateRate rate = LocationUpdateRate.Decode(stored);

		rate.DistanceM.ShouldBe(distance);
		rate.Maximum.ShouldBe(TimeSpan.FromSeconds(maximum));
		rate.Minimum.ShouldBe(TimeSpan.FromSeconds(minimum));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("nonsense")]
	[InlineData("25/60")]
	public void AnythingUnreadable_IsADeviceThatHasNotChosen(string? stored) =>
		LocationUpdateRate.Decode(stored).ShouldBe(LocationUpdateRate.Default);
}
