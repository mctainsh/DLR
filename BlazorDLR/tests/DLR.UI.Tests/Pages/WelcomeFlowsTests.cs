using BlazorDLR.Shared.Pages;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using Bunit.TestDoubles;
using DLR.Core.Contracts.Identity;
using DLR.UI.Tests.Components;
using DLR.UI.Tests.Fakes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The two happy paths on Welcome: register-and-sign-in, and sign-in. Both must
/// (a) call the right endpoint, (b) hand the returned session to <see cref="AuthState"/>
/// so <c>AuthorizeView</c> re-renders, and (c) navigate to <c>/</c>. Missing any of
/// the three would leave the caller stuck on the sign-in form after a good password.
/// </summary>
[Collection(SourceOfferFooterCollection.Name)]
public sealed class WelcomeFlowsTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private (FakeApiClient api, AuthState auth) WireServices(TokenResponse? tokenResult = null)
	{
		FakeApiClient api = new() { TokenResult = tokenResult };
		FakeTokenStore tokens = new();
		FakeTimeProvider clock = new(FixedInstant);
		AuthState auth = new(api, tokens, clock);
		Services.AddSingleton<IApiClient>(api);
		Services.AddSingleton<ITokenStore>(tokens);
		Services.AddSingleton<TimeProvider>(clock);
		Services.AddSingleton(auth);
		Services.AddSingleton<AuthenticationStateProvider>(auth);
		Services.AddSingleton<IEnumerable<IExternalSignInProvider>>(Array.Empty<IExternalSignInProvider>());
		Services.AddRealAuthorizationPipeline();
		this.CascadeAuthenticationState(auth);
		return (api, auth);
	}

	[Fact]
	public async Task SignIn_HappyPath_AppliesSessionAndNavigatesHome()
	{
		Guid userId = Guid.NewGuid();
		TokenResponse issued = new(
			AccessToken: "access-xyz",
			ExpiresIn: 900,
			RefreshToken: "refresh-xyz",
			User: new AuthenticatedUser(userId, "DaveSmith", HasEmail: true, EmailConfirmed: true));
		(FakeApiClient api, AuthState auth) = WireServices(tokenResult: issued);

		BunitNavigationManager nav = (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();

		IRenderedComponent<Welcome> component = Render<Welcome>();

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement signInTab = component.FindAll("button.tab")
				.First(b => b.TextContent.Contains("Sign in", StringComparison.Ordinal));
			signInTab.Click();
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement user = component.Find("input[autocomplete='username']");
			user.Change("DaveSmith");
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement password = component.Find("input[type=password]");
			password.Change("GoodPass9");
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement form = component.Find("form");
			form.Submit();
		});

		component.WaitForAssertion(() =>
		{
			api.Calls.ShouldContain("TokenAsync",
				"§7.4: sign-in hits the token endpoint (password grant).");
			auth.AccessToken.ShouldBe("access-xyz",
				"the session is applied — AuthState now carries the access token in memory.");
			auth.UserName.ShouldBe("DaveSmith");
			// Base URI is http://localhost/ and Home is /, so the resolved URI is
			// http://localhost/ either way — a URI comparison would not distinguish
			// "did not navigate" from "navigated to /". Assert on the history instead.
			nav.History.Count.ShouldBeGreaterThan(0,
				"§4.1: successful sign-in navigates — the history records at least one navigation.");
			nav.History.Last().Uri.EndsWith("/", StringComparison.Ordinal).ShouldBeTrue(
				"the last navigation was to the app root — Home.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Register_WithoutEmail_AppliesSessionAndNavigatesHome()
	{
		Guid userId = Guid.NewGuid();
		TokenResponse issued = new(
			AccessToken: "access-new",
			ExpiresIn: 900,
			RefreshToken: "refresh-new",
			User: new AuthenticatedUser(userId, "NewJoiner", HasEmail: false, EmailConfirmed: false));
		(FakeApiClient api, AuthState auth) = WireServices(tokenResult: issued);

		BunitNavigationManager nav = (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();

		IRenderedComponent<Welcome> component = Render<Welcome>();

		// Welcome opens on the Sign in tab; the Register form only exists after a click.
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement registerTab = component.FindAll("button.tab")
				.First(b => b.TextContent.Contains("Register", StringComparison.Ordinal));
			registerTab.Click();
		});

		// Username input uses oninput (blur triggers the availability check), so push via Input.
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement user = component.FindAll("input").First(i => i.GetAttribute("placeholder") == "DaveSmith");
			user.Input("NewJoiner");
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement password = component.Find("input[type=password]");
			password.Change("Passw0rd");
		});
		// Leave email blank on purpose — the callout must have been visible, and the
		// account must still be creatable.
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement form = component.Find("form");
			form.Submit();
		});

		component.WaitForAssertion(() =>
		{
			api.Calls.ShouldContain("RegisterAsync",
				"§7.2: register hits the register endpoint even without an email.");
			auth.AccessToken.ShouldBe("access-new");
			auth.UserName.ShouldBe("NewJoiner");
			nav.History.Count.ShouldBeGreaterThan(0,
				"successful registration navigates — the history records at least one navigation.");
			nav.History.Last().Uri.EndsWith("/", StringComparison.Ordinal).ShouldBeTrue();
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// A rider who is already signed in is not asked to confirm it. Landing on Welcome
	/// with a live session goes straight home — no "Continue" to push.
	/// </summary>
	[Fact]
	public async Task AlreadySignedIn_NavigatesHomeWithoutAnyInteraction()
	{
		(_, AuthState auth) = WireServices();
		BunitNavigationManager nav = (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();

		await auth.ApplySessionAsync(new TokenResponse(
			AccessToken: "access-live",
			ExpiresIn: 900,
			RefreshToken: "refresh-live",
			User: new AuthenticatedUser(Guid.NewGuid(), "DaveSmith", HasEmail: true, EmailConfirmed: true)));

		IRenderedComponent<Welcome> component = Render<Welcome>();

		component.WaitForAssertion(() =>
		{
			nav.History.Count.ShouldBeGreaterThan(0,
				"an authenticated caller on /welcome is sent home on arrival.");
			nav.History.Last().Uri.EndsWith("/", StringComparison.Ordinal).ShouldBeTrue();
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// The session can arrive after the page has rendered — a refresh that was still in
	/// flight when <c>RedirectToWelcome</c> bounced the rider here. The broadcast re-renders
	/// <c>AuthorizeView</c> but not the page, so the redirect has to hang off
	/// <c>AuthenticationStateChanged</c>; this test fails if it hangs off a render callback.
	/// </summary>
	[Fact]
	public async Task SessionArrivingAfterRender_NavigatesHome()
	{
		(_, AuthState auth) = WireServices();
		BunitNavigationManager nav = (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();

		IRenderedComponent<Welcome> component = Render<Welcome>();
		nav.History.Count.ShouldBe(0, "an anonymous caller stays on Welcome.");

		await component.InvokeAsync(() => auth.ApplySessionAsync(new TokenResponse(
			AccessToken: "access-late",
			ExpiresIn: 900,
			RefreshToken: "refresh-late",
			User: new AuthenticatedUser(Guid.NewGuid(), "LateArrival", HasEmail: false, EmailConfirmed: false))));

		component.WaitForAssertion(() =>
		{
			nav.History.Count.ShouldBeGreaterThan(0,
				"the session landing after first render still moves the rider off Welcome.");
			nav.History.Last().Uri.EndsWith("/", StringComparison.Ordinal).ShouldBeTrue();
		}, timeout: TimeSpan.FromSeconds(3));
	}
}
