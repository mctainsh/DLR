using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using Bunit;
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
		// empty rather than "not loaded". The join code is organiser-only, so the split has
		// to be visible on the client.
		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Organised by you", StringComparison.Ordinal).ShouldBeTrue(
				"§5.2: rides the caller runs render in their own section, where the join code sits.");
			component.Markup.Contains("Joined", StringComparison.Ordinal).ShouldBeTrue(
				"§5.2: rides the caller was admitted to render separately from the ones they run.");
		}, timeout: TimeSpan.FromSeconds(3));
	}
}
