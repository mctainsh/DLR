using AngleSharp.Dom;
using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.State;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Components;

/// <summary>
/// The title bar every page opens with: the heading, and the arrow back out of the page.
/// <para>
/// The arrow carries two behaviours that must not collapse into one. It is a real anchor
/// with a real href, because the SSR pass renders it before any JS exists and a rider may
/// open it in a new tab. On an interactive render it prefers real in-app history, so going
/// back returns the rider to the page they left — scroll position, map view and all —
/// rather than to a freshly-loaded parent.
/// </para>
/// </summary>
public sealed class PageNavTests : PageTestContext
{
	private const string Child = "/group-rides/11111111-1111-1111-1111-111111111111/thread";
	private const string Parent = "/group-rides/11111111-1111-1111-1111-111111111111";

	private IRenderedComponent<PageNav> RenderNav(string title = "Ride thread", string? backHref = Parent) =>
		Render<PageNav>(parameters => parameters
			.Add(p => p.Title, title)
			.Add(p => p.BackHref, backHref));

	[Fact]
	public void TheTitle_IsThePagesOneH1()
	{
		IRenderedComponent<PageNav> nav = RenderNav();

		nav.Find("h1").TextContent.Trim().ShouldBe("Ride thread",
			"pages no longer carry their own heading — the bar's title is the document's h1, so what a " +
			"screen reader announces and what a rider reads are the same string.");
	}

	[Fact]
	public void TheBackArrow_IsARealAnchorToTheParent()
	{
		IRenderedComponent<PageNav> nav = RenderNav();

		IElement back = nav.Find("a.page-nav-back");
		back.GetAttribute("href").ShouldBe(Parent,
			"a static render has no JS to intercept the click, so the href has to be the answer on its own.");
	}

	[Fact]
	public void BackLabel_NamesTheDestination_ForACallerWhoCannotSeeTheArrow()
	{
		IRenderedComponent<PageNav> nav = Render<PageNav>(parameters => parameters
			.Add(p => p.Title, "Join requests")
			.Add(p => p.BackHref, Parent)
			.Add(p => p.BackLabel, "Back to the ride"));

		nav.Find("a.page-nav-back").GetAttribute("aria-label").ShouldBe("Back to the ride",
			"the arrow is a glyph with no text, so its accessible name is the only thing naming where it goes.");
	}

	[Fact]
	public void NoBackHref_RendersNoArrow()
	{
		IRenderedComponent<PageNav> nav = RenderNav(title: "Home", backHref: null);

		nav.FindAll("a.page-nav-back").ShouldBeEmpty(
			"a page with no parent renders no arrow rather than one that goes nowhere.");
		nav.Find("h1").TextContent.Trim().ShouldBe("Home");
	}

	[Fact]
	public void Actions_RenderBesideTheTitle()
	{
		IRenderedComponent<PageNav> nav = Render<PageNav>(parameters => parameters
			.Add(p => p.Title, "My rides")
			.Add(p => p.BackHref, "/")
			.Add(p => p.Actions, (RenderFragment)(builder =>
			{
				builder.OpenElement(0, "a");
				builder.AddAttribute(1, "class", "button");
				builder.AddContent(2, "Import GPX");
				builder.CloseElement();
			})));

		nav.Find(".page-nav-actions .button").TextContent.ShouldBe("Import GPX");
	}

	/// <summary>
	/// The deep-link case: nothing behind this page, so the arrow follows the declared parent
	/// rather than calling history.back() — which on the first entry of a tab either does
	/// nothing or leaves the app.
	/// </summary>
	[Fact]
	public void WithNoInAppHistory_ClickingBack_FollowsTheParentRoute()
	{
		NavigationManager navigation = Services.GetRequiredService<NavigationManager>();
		navigation.NavigateTo(Child);

		// Resolved after the navigation above, exactly as a cold page load resolves it: the
		// counter never saw a move, so it reports no history.
		IRenderedComponent<PageNav> nav = RenderNav();

		nav.Find("a.page-nav-back").Click();

		navigation.Uri.EndsWith(Parent, StringComparison.Ordinal).ShouldBeTrue(
			"a rider who opened a shared link has no history to step into — the parent route is the answer.");
		JSInterop.Invocations.ShouldBeEmpty("and history.back() must not be called when there is no history.");
	}

	/// <summary>
	/// The walked-here case. The rider navigated in, so there is a real entry behind this
	/// page — stepping into it returns them to that page as they left it, which re-navigating
	/// to the parent route would not.
	/// </summary>
	[Fact]
	public void AfterWalkingIn_ClickingBack_StepsIntoRealHistory()
	{
		JSInterop.SetupVoid("history.back");

		// Resolve before navigating so the counter sees the move — the app's order too, since
		// the previous page's own PageNav injected it.
		NavigationHistory history = Services.GetRequiredService<NavigationHistory>();
		NavigationManager navigation = Services.GetRequiredService<NavigationManager>();
		navigation.NavigateTo(Child);
		history.CanGoBack.ShouldBeTrue();

		IRenderedComponent<PageNav> nav = RenderNav();
		nav.Find("a.page-nav-back").Click();

		JSInterop.VerifyInvoke("history.back");
		navigation.Uri.EndsWith(Child, StringComparison.Ordinal).ShouldBeTrue(
			"the browser unwinds the stack — PageNav must not also navigate, or the rider skips a page.");

		// The stubbed history.back() moves nothing, so stand in for the popstate a real one
		// would raise. The counter must read that as unwinding: without it, using the arrow
		// would deepen the stack it is supposed to be walking out of.
		navigation.NavigateTo(Parent);
		history.CanGoBack.ShouldBeFalse(
			"the navigation that follows a step back unwinds the stack rather than adding to it.");
	}
}
