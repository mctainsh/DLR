using BlazorDLR.Shared.Components;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Components;

/// <summary>
/// §7.9's redirect: an [Authorize]-gated page reached without a session lands on
/// Welcome via <c>AuthorizeRouteView</c>'s NotAuthorizedContent. The redirect must
/// fire on init and it must land on <c>/welcome</c>. Getting the path wrong or
/// forgetting to navigate would either strand the user on a blank page or route
/// them somewhere without a sign-in form.
/// </summary>
public sealed class RedirectToWelcomeTests : BunitContext
{
	[Fact]
	public void OnInitialized_NavigatesToWelcome()
	{
		BunitNavigationManager nav = Services.GetRequiredService<NavigationManager>() as BunitNavigationManager
			?? throw new InvalidOperationException("bUnit did not register a BunitNavigationManager.");
		string before = nav.Uri;

		Render<RedirectToWelcome>();

		// The FakeNavigationManager reports the absolute URI it navigated to; check the
		// path ends with /welcome to be independent of the test host's base URI.
		nav.Uri.EndsWith("/welcome", StringComparison.Ordinal).ShouldBeTrue(
			$"§7.9: an anonymous caller must land on /welcome; got '{nav.Uri}'.");
		nav.Uri.ShouldNotBe(before, "the component must actually navigate - a no-op leaves the anonymous user on the gated page.");
	}
}
