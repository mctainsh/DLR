using BlazorDLR.Shared.Pages;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Identity;
using DLR.UI.Tests.Fakes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// Welcome is the one screen an unauthenticated caller lands on (§7.9), and it's where
/// three explicit design decisions from §7.2 have to hold at render time:
/// <list type="bullet">
///   <item>Registration is the first tab — signup is not a hurdle.</item>
///   <item>Empty email always shows the recovery-trade-off callout, so no one can register
///     without seeing what they gave up.</item>
///   <item>The password composition rule is stated in plain text (§7.2 v0.22): 6+ chars,
///     an uppercase letter, a lowercase letter and a digit — no special-character rule.</item>
///   <item>Server-side per-rule password errors (from Identity's <c>ValidationProblemDetails</c>)
///     survive the round trip and render as a list, so a user seeing "Too short" learns
///     what to fix, not "The details you entered were rejected."</item>
/// </list>
/// </summary>
public sealed class WelcomeTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private FakeApiClient WireServices()
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
		Services.AddSingleton<IEnumerable<IExternalSignInProvider>>(Array.Empty<IExternalSignInProvider>());
		Services.AddRealAuthorizationPipeline();
		this.CascadeAuthenticationState(auth);
		return api;
	}

	[Fact]
	public void PasswordCompositionCopy_IsPresent_AndMentionsNoSpecialChar()
	{
		WireServices();

		IRenderedComponent<Welcome> component = Render<Welcome>();

		component.WaitForAssertion(() =>
		{
			string markup = component.Markup;
			markup.Contains("At least 6 characters", StringComparison.Ordinal).ShouldBeTrue(
				"§7.2 v0.22: the copy states the six-character minimum plainly, so a user knows the rule before submit.");
			markup.Contains("uppercase", StringComparison.Ordinal).ShouldBeTrue();
			markup.Contains("lowercase", StringComparison.Ordinal).ShouldBeTrue();
			markup.Contains("digit", StringComparison.Ordinal).ShouldBeTrue();
			markup.Contains("special", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
				"§7.2 v0.22 removed the special-character requirement; the copy must not claim it.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void EmptyEmail_ShowsTheRecoveryTradeOffCallout()
	{
		WireServices();

		IRenderedComponent<Welcome> component = Render<Welcome>();

		component.WaitForAssertion(() =>
		{
			// Default state: email is empty, so the "no recovery" callout is visible.
			component.Markup.Contains("No email means no recovery", StringComparison.Ordinal).ShouldBeTrue(
				"§7.2: the callout is always rendered when email is blank — no dialog nobody reads.");
			component.Markup.Contains("6 months", StringComparison.Ordinal).ShouldBeTrue(
				"the inactivity policy is named on the callout so the user can make an informed choice.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task ServerValidationErrors_RenderAsAList()
	{
		FakeApiClient api = WireServices();

		// Reproduce Identity's per-rule ValidationProblemDetails payload — one Title, three
		// specific messages a user can act on.
		api.TokenException = new ApiException(new ApiError(
			StatusCode: System.Net.HttpStatusCode.BadRequest,
			Title: "The details you entered were rejected — please check and try again.",
			Messages: new[] { "Too short", "No uppercase", "No digit" }));

		IRenderedComponent<Welcome> component = Render<Welcome>();

		// Fill username + password and submit. bUnit v2 needs Change() through InvokeAsync.
		await component.InvokeAsync(() =>
		{
			// The username field binds on oninput (blur triggers the availability check),
			// so we push the value through Input, not Change.
			AngleSharp.Dom.IElement userNameInput = component.FindAll("input").First(i => i.GetAttribute("placeholder") == "DaveSmith");
			userNameInput.Input("DaveSmith");
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement password = component.FindAll("input[type=password]").First();
			password.Change("weak");
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement form = component.Find("form");
			form.Submit();
		});

		component.WaitForAssertion(() =>
		{
			string markup = component.Markup;
			markup.Contains("Too short", StringComparison.Ordinal).ShouldBeTrue(
				"§18.2: the server's per-rule password messages surface in the UI so the user knows what to fix.");
			markup.Contains("No uppercase", StringComparison.Ordinal).ShouldBeTrue();
			markup.Contains("No digit", StringComparison.Ordinal).ShouldBeTrue();
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task UsernameAvailabilityCheck_FiresOnBlur_AndRendersAnAnswer()
	{
		FakeApiClient api = WireServices();
		api.UserNameAvailableResult = false;

		IRenderedComponent<Welcome> component = Render<Welcome>();

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement userNameInput = component.FindAll("input").First(i => i.GetAttribute("placeholder") == "DaveSmith");
			userNameInput.Input("DaveSmith");
			userNameInput.Blur();
		});

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("is taken", StringComparison.Ordinal).ShouldBeTrue(
				"§7.2: the on-blur check reports availability so a rider does not learn the name is taken at submit.");
			api.Calls.ShouldContain("IsUserNameAvailableAsync");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task RegisterTab_IsActiveByDefault_AndSignInTabSwitches()
	{
		WireServices();

		IRenderedComponent<Welcome> component = Render<Welcome>();

		component.WaitForAssertion(() =>
		{
			// The Register tab starts active — signup should not be a hurdle.
			AngleSharp.Dom.IElement registerTab = component.FindAll("button.tab").First(b => b.TextContent.Contains("Register", StringComparison.Ordinal));
			registerTab.ClassList.Contains("active").ShouldBeTrue("§7.2: Register is deliberately the first tab.");
		}, timeout: TimeSpan.FromSeconds(3));

		// Switching to Sign in must remove the recovery callout — that copy is register-only.
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement signInTab = component.FindAll("button.tab").First(b => b.TextContent.Contains("Sign in", StringComparison.Ordinal));
			signInTab.Click();
		});

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("No email means no recovery", StringComparison.Ordinal).ShouldBeFalse(
				"the callout is register-only — showing it under 'Sign in' would be nonsense copy.");
		}, timeout: TimeSpan.FromSeconds(3));
	}
}
