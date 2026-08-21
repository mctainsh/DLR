using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using Bunit;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The signed-in landing for group rides shows two entry points (Create, Join) and two
/// lists of the caller's rides split by role (organised, joined). What matters for the
/// test is that both entry points remain reachable — a landing that only offered "Join"
/// would silently gate ride creation behind rediscovery of the URL — and that the two
/// sections render even when they are empty.
/// </summary>
public sealed class GroupRidesLandingTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private FakeApiClient WireServices()
	{
		FakeApiClient api = new();
		Services.AddSingleton<IApiClient>(api);
		return api;
	}

	[Fact]
	public void BothEntryPoints_AreRenderedAsCards()
	{
		WireServices();

		IRenderedComponent<GroupRides> component = Render<GroupRides>();

		component.FindAll("a[href='/group-rides/join']").ShouldNotBeEmpty(
			"§5.2: the Join card is one of the two entry points; the landing has no reason to exist without it.");
		component.FindAll("a[href='/group-rides/create']").ShouldNotBeEmpty(
			"§5.2: the Create card is the other entry — a would-be organiser starts here.");
	}

	[Fact]
	public void MyRides_RendersOrganisedAndJoinedSections()
	{
		WireServices();

		IRenderedComponent<GroupRides> component = Render<GroupRides>();

		// The two sections are always emitted, even empty, so the caller can see they are
		// empty rather than "not loaded".
		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Organised by you", StringComparison.Ordinal).ShouldBeTrue(
				"§5.2: adventures the caller runs render in their own section, where the join code sits.");
			component.Markup.Contains("Joined", StringComparison.Ordinal).ShouldBeTrue(
				"§5.2: adventures the caller was admitted to render separately from the ones they run.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// §5.2: the join code badge is on both sections. Once somebody has joined, the landing is
	/// the only place they can read the code back off — without it they cannot tell a friend how
	/// to follow along, which is the thing they want at the start line.
	/// </summary>
	[Fact]
	public void JoinedRide_ShowsItsJoinCode()
	{
		FakeApiClient api = WireServices();

		api.MyRidesResult = new(
			[new RideSummary(
				Guid.NewGuid(),
				"Mine to run",
				FixedInstant,
				RideStateDto.Open,
				IsOrganiser: true,
				MemberCount: 3,
				JoinCode: "AB3K9Z")],
			[new RideSummary(
				Guid.NewGuid(),
				"Somebody else's",
				FixedInstant,
				RideStateDto.Open,
				IsOrganiser: false,
				MemberCount: 5,
				JoinCode: "QW7T2M")]);

		IRenderedComponent<GroupRides> component = Render<GroupRides>();

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("AB3K9Z", StringComparison.Ordinal).ShouldBeTrue(
				"the organiser has always seen the code for an adventure they run.");
			component.Markup.Contains("QW7T2M", StringComparison.Ordinal).ShouldBeTrue(
				"§5.2: a joined adventure shows its code too, the same as an organised one.");
		}, timeout: TimeSpan.FromSeconds(3));
	}
}
