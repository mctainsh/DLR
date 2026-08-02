using BlazorDLR.Shared.Pages.GroupRides;
using Bunit;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The signed-in landing for group rides is deliberately minimal (§5.2). Two cards,
/// two entry points, and copy that says the organiser controls both. What matters
/// for the test is that both entry points remain reachable — a landing that only
/// offered "Join" would silently gate ride creation behind rediscovery of the URL.
/// </summary>
public sealed class GroupRidesLandingTests : BunitContext
{
	[Fact]
	public void BothEntryPoints_AreRenderedAsCards()
	{
		IRenderedComponent<GroupRides> component = Render<GroupRides>();

		component.FindAll("a[href='/group-rides/join']").ShouldNotBeEmpty(
			"§5.2: the Join card is one of the two entry points; the landing has no reason to exist without it.");
		component.FindAll("a[href='/group-rides/create']").ShouldNotBeEmpty(
			"§5.2: the Create card is the other entry — a would-be organiser starts here.");
	}

	[Fact]
	public void Copy_StatesOrganiserControlsBothPaths()
	{
		IRenderedComponent<GroupRides> component = Render<GroupRides>();

		component.Markup.Contains("organiser controls both", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
			"§5.2: the framing on this page (organiser controls both paths) is the design choice — the copy must carry it.");
	}
}
