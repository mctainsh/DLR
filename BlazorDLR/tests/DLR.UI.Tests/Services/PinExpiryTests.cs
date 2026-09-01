using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Services;

/// <summary>
/// How long a rider's pin outlives the fix behind it (§5.3, §18.6). Two things are worth a test
/// and neither is the list of options itself:
/// <list type="bullet">
///   <item><see cref="PinExpiry.Decode"/> reads a value off a device we do not control and hands
///     it straight to a dropdown. Anything it cannot place has to land on an offered value, or
///     the settings screen shows one thing while the map does another.</item>
///   <item><see cref="PinExpiry.IsExpired"/> is what takes somebody off the map. Its edges - the
///     exact cut-off, and a fix stamped in the future by a phone whose clock runs fast - are the
///     difference between a ghost pin and a rider who vanishes while still riding.</item>
/// </list>
/// </summary>
public sealed class PinExpiryTests
{
	private static readonly DateTimeOffset Now = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	[Fact]
	public void TheDefault_IsTenMinutes_AndIsOneOfTheOfferedValues()
	{
		PinExpiry.Default.ShouldBe(TimeSpan.FromMinutes(10));
		PinExpiry.Options.ShouldContain(PinExpiry.Default,
			"the dropdown opens on the default, so a default that is not in it renders as no choice at all.");
	}

	[Fact]
	public void TheOptions_RunFromFiveMinutesToSixHours_ShortestFirst()
	{
		PinExpiry.Options.ShouldBe(new[]
		{
			TimeSpan.FromMinutes(5),
			TimeSpan.FromMinutes(10),
			TimeSpan.FromMinutes(30),
			TimeSpan.FromHours(1),
			TimeSpan.FromHours(2),
			TimeSpan.FromHours(6),
		});
	}

	[Fact]
	public void Encode_ThenDecode_RoundTripsEveryOfferedValue()
	{
		foreach (TimeSpan option in PinExpiry.Options)
		{
			PinExpiry.Decode(PinExpiry.Encode(option)).ShouldBe(option,
				"a device that stored a choice must read back the same one.");
		}
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("nonsense")]
	[InlineData("0")]
	[InlineData("-30")]
	[InlineData("10.5")]
	public void Decode_AnythingItCannotTrust_FallsBackToTheDefault(string? stored)
	{
		PinExpiry.Decode(stored).ShouldBe(PinExpiry.Default,
			"the value comes off a device we do not control; the failure mode is 'looks stock'.");
	}

	[Theory]
	[InlineData(7, 5)]      // between five and ten, and nearer five
	[InlineData(20, 30)]    // between ten and thirty, and nearer thirty
	[InlineData(45, 60)]    // an exact tie, which goes to the longer: dropping a rider early is the worse mistake
	[InlineData(1440, 360)] // a day, from a build that offered more than this one does
	public void Decode_AValueThisBuildDoesNotOffer_LandsOnTheNearestOneItDoes(int storedMinutes, int expectedMinutes)
	{
		PinExpiry.Decode(storedMinutes.ToString()).ShouldBe(TimeSpan.FromMinutes(expectedMinutes),
			"a stored value that is not on the list would leave the dropdown showing something " +
			"other than what the map is doing.");
	}

	[Fact]
	public void AFixYoungerThanTheLimit_IsDrawn()
	{
		PinExpiry.IsExpired(Now.AddMinutes(-9), Now, TimeSpan.FromMinutes(10)).ShouldBeFalse();
	}

	[Fact]
	public void AFixExactlyAtTheLimit_IsStillDrawn()
	{
		// The boundary is a choice rather than an accident: the rider asked to keep pins for ten
		// minutes, and one taken exactly ten minutes ago is the last one that answers that.
		PinExpiry.IsExpired(Now.AddMinutes(-10), Now, TimeSpan.FromMinutes(10)).ShouldBeFalse();
	}

	[Fact]
	public void AFixOlderThanTheLimit_ComesOffTheMap()
	{
		PinExpiry.IsExpired(Now.AddMinutes(-11), Now, TimeSpan.FromMinutes(10)).ShouldBeTrue(
			"a position sits in the ride's cache and is rebroadcast every tick whether or not it " +
			"moved, so nothing else takes a dead phone's pin off the map.");
	}

	[Fact]
	public void AFixFromTheFuture_IsNeverExpired()
	{
		// Stamped by the device that took it (§5.7), so a phone whose clock runs fast produces
		// one. Which side of ours its clock is on says nothing about how long ago the rider was
		// there - and dropping the pin of somebody who is riding beside you is the worse mistake.
		PinExpiry.IsExpired(Now.AddMinutes(30), Now, TimeSpan.FromMinutes(10)).ShouldBeFalse();
	}

	[Theory]
	[InlineData(5, "5 minutes")]
	[InlineData(10, "10 minutes")]
	[InlineData(30, "30 minutes")]
	[InlineData(60, "1 hour")]
	[InlineData(120, "2 hours")]
	[InlineData(360, "6 hours")]
	public void EveryOfferedValue_ReadsAsWhatItIs(int minutes, string expected)
	{
		PinExpiry.Label(TimeSpan.FromMinutes(minutes)).ShouldBe(expected);
	}
}
