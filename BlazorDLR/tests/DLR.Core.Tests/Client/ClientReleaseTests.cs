using DLR.Core.Client;
using DLR.Core.Contracts.Announcements;

namespace DLR.Core.Tests.Client;

/// <summary>
/// §20.1's version floor. The rule is four lines of code and the whole of what stands between a
/// store build shipped a year ago and a screen of failures it cannot explain, so every boundary is
/// pinned here rather than inferred from the endpoint that calls it.
/// </summary>
public sealed class ClientReleaseTests
{
	[Fact]
	public void Check_BelowMinimum_IsUnsupported() =>
		ClientRelease.Check(new Version(1, 0, 0, 0)).ShouldBe(ClientSupport.Unsupported);

	[Fact]
	public void Check_AtTheFloor_IsSupported()
	{
		// The floor is inclusive, and in a build that has not had to raise it the floor and the
		// recommendation are the same version - so this is also "a current client is supported".
		ClientRelease.Check(ClientRelease.Minimum).ShouldBe(ClientSupport.Supported);

		ClientRelease.Check(new Version(99, 0, 0, 0)).ShouldBe(ClientSupport.Supported,
			"a client newer than this server knows about is not a client to nag");
	}

	[Fact]
	public void Check_BetweenTheTwo_OffersAnUpdate() =>
		// Explicit bounds, because the two constants are equal in the shipping build and the band
		// between them is otherwise unreachable.
		ClientRelease
			.Check(new Version(1, 5, 0, 0), new Version(1, 0, 0, 0), new Version(2, 0, 0, 0))
			.ShouldBe(ClientSupport.UpdateAvailable);

	[Fact]
	public void Check_BelowAGivenFloor_IsUnsupported() =>
		ClientRelease
			.Check(new Version(0, 9, 0, 0), new Version(1, 0, 0, 0), new Version(2, 0, 0, 0))
			.ShouldBe(ClientSupport.Unsupported);

	[Fact]
	public void Check_NoVersionAtAll_IsUnsupported() =>
		ClientRelease.Check(null).ShouldBe(ClientSupport.Unsupported,
			"a client that cannot say what it is, is one this server cannot vouch for - the other " +
			"way round would make the check opt-in for exactly the builds most likely to be broken");

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("eight")]
	[InlineData("8.0.0.30-beta")]
	[InlineData(null)]
	public void Parse_Unparseable_IsNothingSaid(string? reported) =>
		ClientRelease.Parse(reported).ShouldBeNull();

	[Fact]
	public void Parse_ReadsAFourPartVersion() =>
		ClientRelease.Parse("8.0.0.30").ShouldBe(new Version(8, 0, 0, 30));

	[Fact]
	public void UpdateUrlFor_MatchesThePlatformTheMauiHostReports()
	{
		// IFormFactor.GetPlatform() appends the OS version, so the match has to be loose.
		ClientRelease.UpdateUrlFor("Android - 14.0").ShouldBe(ClientRelease.PlayStoreUrl);
		ClientRelease.UpdateUrlFor("iOS - 17.2").ShouldBe(ClientRelease.AppStoreUrl);
	}

	[Fact]
	public void UpdateUrlFor_AHostWithNoStore_IsNull()
	{
		ClientRelease.UpdateUrlFor("WinUI").ShouldBeNull();
		ClientRelease.UpdateUrlFor(null).ShouldBeNull();
	}
}
