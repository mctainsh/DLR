using BlazorDLR.Shared.Layout;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Identity;
using DLR.UI.Tests.Fakes;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Layout;

/// <summary>
/// The one navigation surface both hosts share. §7.9 says the unauthenticated landing
/// is Welcome — everything else redirects there — so the nav's job is to expose the
/// signed-in surface only when the user is signed in, and to expose Welcome when the
/// user is not. Getting this wrong on either side would either give an anonymous
/// caller broken links or hide the app from a signed-in one.
/// </summary>
public sealed class NavMenuTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private AuthState WireAuth()
	{
		FakeApiClient api = new();
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
		return auth;
	}

	[Fact]
	public void Anonymous_ShowsOnlyWelcomeLink()
	{
		WireAuth();

		IRenderedComponent<NavMenu> component = Render<NavMenu>();

		component.WaitForAssertion(() =>
		{
			component.FindAll("a[href='welcome']").ShouldNotBeEmpty(
				"§7.9: an anonymous nav must lead to Welcome — that is the only signed-out destination.");
			component.FindAll("a[href='rides']").Count.ShouldBe(0,
				"§7.9: the signed-in surface must not appear on an anonymous nav — its links are dead until sign-in.");
			component.FindAll("a[href='settings']").Count.ShouldBe(0);
			component.FindAll("a[href='group-rides']").Count.ShouldBe(0);
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Authenticated_ShowsFullSignedInSurface_NotWelcome()
	{
		AuthState auth = WireAuth();

		// Sign in a synthetic session so AuthorizeView reaches the Authorized branch.
		await auth.ApplySessionAsync(new TokenResponse(
			AccessToken: "access",
			ExpiresIn: 900,
			RefreshToken: "refresh",
			User: new AuthenticatedUser(Guid.NewGuid(), "DaveSmith", HasEmail: true, EmailConfirmed: true)));

		// Refresh the cascaded state before rendering.
		this.CascadeAuthenticationState(auth);

		IRenderedComponent<NavMenu> component = Render<NavMenu>();

		component.WaitForAssertion(() =>
		{
			// Every signed-in destination is present.
			component.FindAll("a[href='']").ShouldNotBeEmpty("Home link (href='') is the root the signed-in nav opens with.");
			component.FindAll("a[href='rides']").ShouldNotBeEmpty("My rides is one of the signed-in links.");
			component.FindAll("a[href='group-rides']").ShouldNotBeEmpty();
			component.FindAll("a[href='import']").ShouldNotBeEmpty();
			component.FindAll("a[href='settings']").ShouldNotBeEmpty();

			// And the Welcome link is gone — a signed-in user has no reason for it.
			component.FindAll("a[href='welcome']").Count.ShouldBe(0,
				"§7.9: the Welcome link is the signed-out entry point and disappears once the user signs in.");
		}, timeout: TimeSpan.FromSeconds(3));
	}
}
