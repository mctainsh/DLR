using BlazorDLR.Shared.Layout;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.UI.Tests.Fakes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Layout;

/// <summary>
/// The one layout every page composes with. Two properties that matter:
/// <list type="bullet">
///   <item>The AGPL §13 source-offer footer is rendered on <em>every</em> page (§14.6.2),
///     so it lives in the layout — not in individual pages, where a missed include
///     would silently ship an AGPL-non-compliant build.</item>
///   <item>The layout emits a <c>@Body</c> region for the routed page to fill in.</item>
/// </list>
/// </summary>
public sealed class MainLayoutTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private void WireServices()
	{
		FakeApiClient api = new()
		{
			AboutResult = new AboutInfo("AGPL-3.0-only", "https://github.com/dumbluckrides/dlr",
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
	}

	[Fact]
	public void SourceOfferFooter_IsRenderedByTheLayout()
	{
		WireServices();

		IRenderedComponent<MainLayout> component = Render<MainLayout>(parameters => parameters
			.Add(p => p.Body, (RenderFragment)(builder => builder.AddMarkupContent(0, "<p>routed page</p>"))));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("routed page", StringComparison.Ordinal).ShouldBeTrue(
				"the layout's Body slot must render whatever the router pushes into it.");
			component.Markup.Contains("AGPL-3.0-only", StringComparison.Ordinal).ShouldBeTrue(
				"§14.6.2: the AGPL §13 source-offer footer must appear on every page — placing it in the layout ensures that.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void ErrorUi_Element_IsEmittedForBlazorRuntime()
	{
		WireServices();

		IRenderedComponent<MainLayout> component = Render<MainLayout>(parameters => parameters
			.Add(p => p.Body, (RenderFragment)(builder => { })));

		// blazor-error-ui is the well-known id the Blazor runtime toggles on an unhandled
		// exception. Missing it is silent — errors would happen but no in-page toast.
		component.FindAll("#blazor-error-ui").Count.ShouldBe(1,
			"the Blazor runtime looks for #blazor-error-ui to surface unhandled errors — it must be in the layout.");
	}
}
