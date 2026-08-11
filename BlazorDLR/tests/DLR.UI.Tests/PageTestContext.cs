using BlazorDLR.Shared.State;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests;

/// <summary>
/// The base for every test that renders a routable page.
/// <para>
/// Pages open with <c>&lt;PageNav&gt;</c>, which injects <see cref="NavigationHistory"/> to
/// decide whether its back arrow steps into real in-app history or follows the page's
/// declared parent route. That is a page-wide dependency rather than any one screen's, so
/// it is registered once here instead of in each test class — a page test that forgot it
/// would fail inside Blazor's property injector with a stack trace naming the renderer and
/// not the missing service.
/// </para>
/// <para>
/// The real type, not a fake: it is a counter over
/// <see cref="Microsoft.AspNetCore.Components.NavigationManager"/> with no I/O, and bUnit
/// supplies a navigation manager already. A test that wants the "we walked here" branch
/// navigates first; one that does not gets the deep-link branch, which is the honest
/// default for a component rendered cold.
/// </para>
/// <para>
/// Component tests (<c>Components/</c>, <c>Layout/</c>) keep inheriting
/// <see cref="BunitContext"/> directly — they render below the page level, so no
/// <c>PageNav</c> appears in their tree. <c>PageNavTests</c> is the exception and inherits
/// this, since the component under test is the one that needs the service.
/// </para>
/// </summary>
public abstract class PageTestContext : BunitContext
{
	protected PageTestContext() => Services.AddScoped<NavigationHistory>();
}
