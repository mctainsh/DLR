using BlazorDLR.Shared.State;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.State;

/// <summary>
/// The counter behind <c>PageNav</c>'s back arrow: is there a page inside this app to step
/// back onto, or did this session open here?
/// <para>
/// The distinction is the whole point of the type. Getting it wrong in one direction walks a
/// rider who followed a shared link straight out of the app; in the other it strands them on
/// a page whose arrow leads back to the page they just left it for.
/// </para>
/// </summary>
public sealed class NavigationHistoryTests : BunitContext
{
	/// <summary>
	/// Resolves the counter (which subscribes it to the navigation manager) and hands back
	/// both, so a test can drive navigations and read the answer.
	/// </summary>
	private (NavigationHistory History, NavigationManager Nav) Arrange()
	{
		Services.AddScoped<NavigationHistory>();

		// Resolve first: the counter only sees the navigations that happen after it has
		// subscribed, which is the same order the app gets - PageNav's injection is what
		// constructs it.
		NavigationHistory history = Services.GetRequiredService<NavigationHistory>();
		return (history, Services.GetRequiredService<NavigationManager>());
	}

	[Fact]
	public void ASessionThatJustOpened_HasNothingToStepBackOnto()
	{
		(NavigationHistory history, _) = Arrange();

		history.Depth.ShouldBe(0);
		history.CanGoBack.ShouldBeFalse(
			"the entry a session opens at - app root or deep link - has no in-app page behind it.");
	}

	[Fact]
	public void EachInAppNavigation_DeepensTheStack()
	{
		(NavigationHistory history, NavigationManager nav) = Arrange();

		nav.NavigateTo("/group-rides");
		history.CanGoBack.ShouldBeTrue("the traveller has walked one page into the app.");

		nav.NavigateTo("/group-rides/create");
		history.Depth.ShouldBe(2);
	}

	[Fact]
	public void SteppingBack_UnwindsRatherThanDeepens()
	{
		(NavigationHistory history, NavigationManager nav) = Arrange();

		nav.NavigateTo("/group-rides");
		nav.NavigateTo("/group-rides/create");

		// What PageNav does either side of history.back(): arm the counter, then navigate.
		history.NotifySteppingBack();
		nav.NavigateTo("/group-rides");

		history.Depth.ShouldBe(1,
			"stepping back must unwind the stack - counting it as a forward move would leave the arrow " +
			"pointing deeper into the app the more the traveller used it.");
	}

	/// <summary>
	/// The loop this type exists to prevent. A rider opens a shared link to a child page:
	/// there is no history, so the arrow follows the parent route - and that navigation must
	/// not itself become the history the next arrow steps back into, or child and parent
	/// bounce off each other forever.
	/// </summary>
	[Fact]
	public void TheParentRouteFallback_DoesNotBecomeTheHistoryItWasStandingInFor()
	{
		(NavigationHistory history, NavigationManager nav) = Arrange();

		// Landed cold on a child page: no history, so PageNav walks up to the parent.
		history.CanGoBack.ShouldBeFalse();
		history.NotifySteppingBack();
		nav.NavigateTo("/group-rides");

		history.CanGoBack.ShouldBeFalse(
			"walking up to a parent must leave the traveller no deeper than they started - otherwise the " +
			"parent's own arrow steps back into the child they just left.");
	}

	[Fact]
	public void TheStackNeverGoesNegative()
	{
		(NavigationHistory history, NavigationManager nav) = Arrange();

		history.NotifySteppingBack();
		nav.NavigateTo("/a");
		history.NotifySteppingBack();
		nav.NavigateTo("/b");

		history.Depth.ShouldBe(0,
			"a depth below zero would report history that is not there on the next navigation.");
	}

	[Fact]
	public void DisposeUnsubscribes_SoALaterNavigationIsNotCounted()
	{
		(NavigationHistory history, NavigationManager nav) = Arrange();

		history.Dispose();
		nav.NavigateTo("/group-rides");

		history.Depth.ShouldBe(0,
			"a disposed counter still attached to the navigation manager would keep the whole scope alive.");
	}
}
