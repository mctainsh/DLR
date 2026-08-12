using BlazorDLR.Shared.Layout;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.UI.Tests.Fakes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Layout;

/// <summary>
/// The one layout every page composes with. What matters here is the <c>@Body</c> slot —
/// the layout hands the routed page a place to render, and its own chrome (nav rail, theme
/// attribute, confirm modal) sits around it. The AGPL source-offer footer lives on the
/// pre-auth pages (Welcome / SignIn / Register / etc.) rather than the layout — see
/// <c>SourceOfferFooterTests</c>. The <c>#blazor-error-ui</c> element lives in
/// <c>BlazorDLR.Web/Components/App.razor</c>, the SSR shell that wraps every route.
/// </summary>
public sealed class MainLayoutTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private void WireServices()
	{
		FakeApiClient api = new()
		{
			AboutResult = new AboutInfo("AGPL-3.0-only", "https://github.com/mctainsh/dlr",
				"abcd123456789012", "1.0.0+abcd1234", FixedInstant),
		};
		FakeTokenStore tokens = new();
		FakeTimeProvider clock = new(FixedInstant);
		AuthState auth = new(api, tokens, clock);

		Services.AddSingleton<IApiClient>(api);
		Services.AddSingleton<ITokenStore>(tokens);
		Services.AddSingleton<TimeProvider>(clock);
		Services.AddSingleton(auth);
		Services.AddSingleton<AuthenticationStateProvider>(auth);
		Services.AddRealAuthorizationPipeline();
		this.CascadeAuthenticationState(auth);

		// §18.6: the layout injects ThemeState so it can set the data-theme attribute
		// on the outer <div class="app">. Tests wire the in-memory theme service —
		// no localStorage, no MAUI preferences — so a render never has to hit JS.
		Services.AddSingleton<IThemeService, InMemoryThemeService>();
		Services.AddSingleton<ThemeState>();
		Services.AddSingleton<ConfirmService>();

		// The rail this layout mounts carries the current-ride globe (§18.6), which reads the
		// device store for the ride it points at. In-memory here, as with the theme above.
		Services.AddSingleton<IDeviceSettings, InMemoryDeviceSettings>();
		Services.AddSingleton<CurrentRideState>();
	}

	[Fact]
	public void Body_IsRenderedByTheLayout()
	{
		WireServices();

		IRenderedComponent<MainLayout> component = Render<MainLayout>(parameters => parameters
			.Add(p => p.Body, (RenderFragment)(builder => builder.AddMarkupContent(0, "<p>routed page</p>"))));

		component.WaitForAssertion(() =>
			component.Markup.Contains("routed page", StringComparison.Ordinal).ShouldBeTrue(
				"the layout's Body slot must render whatever the router pushes into it."),
			timeout: TimeSpan.FromSeconds(3));
	}
}
