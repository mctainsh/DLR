using BlazorDLR.Shared.Layout;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using Bunit.TestDoubles;
using DLR.UI.Tests.Fakes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Layout;

/// <summary>
/// The one layout every page composes with. What matters here is the <c>@Body</c> slot —
/// the layout hands the routed page a place to render, and its own chrome (nav rail, confirm
/// modal) sits around it. The AGPL source-offer footer lives on the
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

		Services.AddSingleton<ConfirmService>();

		// The rail this layout mounts carries the current-ride globe (§18.6), which reads the
		// device store for the ride it points at. In-memory here — no localStorage, no MAUI
		// preferences, so a render never has to hit JS.
		Services.AddSingleton<IDeviceSettings, InMemoryDeviceSettings>();
		Services.AddSingleton<CurrentRideState>();

		// The layout is also where the first-run introduction is decided (§18.6) — it is the one
		// component above both Home and Welcome, so it is the only place that can own "the first
		// thing you ever see". In-memory here, which reads as "never seen": see
		// FirstRun_OpensTheIntroduction below.
		Services.AddSingleton<IntroTourState>();

		// The layout is where CommentNotifier gets its lifetime (§17.6): injecting it is what
		// constructs it, and constructing it is what subscribes it to the hub. So these have to
		// resolve here even though this suite is about the Body slot — a layout that cannot be
		// built is a layout no page renders inside.
		Services.AddSingleton<IRideHubClient>(new FakeRideHubClient());
		Services.AddSingleton<INotificationService, NoopNotificationService>();
		Services.AddSingleton<NotificationRouting>();
		Services.AddSingleton<CommentNotifier>();
	}

	[Fact]
	public void FirstRun_OpensTheIntroduction()
	{
		WireServices();
		BunitNavigationManager nav = Services.GetRequiredService<NavigationManager>() as BunitNavigationManager
			?? throw new InvalidOperationException("bUnit did not register a BunitNavigationManager.");

		IRenderedComponent<MainLayout> component = Render<MainLayout>(parameters => parameters
			.Add(p => p.Body, (RenderFragment)(builder => builder.AddMarkupContent(0, "<p>routed page</p>"))));

		// The device store is empty, which is what a phone that has just been installed on looks
		// like — and the one launch where a rider has no idea what this app is.
		component.WaitForAssertion(() =>
			nav.Uri.Contains("/intro", StringComparison.Ordinal).ShouldBeTrue(
				$"§18.6: a first run must open the introduction; got '{nav.Uri}'."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task ADeviceThatHasSeenTheIntroduction_IsTakenStraightIntoTheApp()
	{
		WireServices();
		BunitNavigationManager nav = Services.GetRequiredService<NavigationManager>() as BunitNavigationManager
			?? throw new InvalidOperationException("bUnit did not register a BunitNavigationManager.");
		string before = nav.Uri;

		// Marked seen before the layout is built — the second launch on a device, which is every
		// launch but one.
		await Services.GetRequiredService<IntroTourState>().MarkSeenAsync();

		IRenderedComponent<MainLayout> component = Render<MainLayout>(parameters => parameters
			.Add(p => p.Body, (RenderFragment)(builder => builder.AddMarkupContent(0, "<p>routed page</p>"))));

		component.WaitForAssertion(() =>
			component.Markup.Contains("routed page", StringComparison.Ordinal).ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		nav.Uri.ShouldBe(before, "a rider who has been through the deck must never be shown it again by itself.");
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
