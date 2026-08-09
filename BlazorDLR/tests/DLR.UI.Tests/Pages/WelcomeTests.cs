using BlazorDLR.Shared.Pages;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.UI.Tests.Components;
using DLR.UI.Tests.Fakes;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// Welcome is the one screen an unauthenticated caller lands on (§7.9). Design decisions
/// from §7.2 that must hold at render time:
/// <list type="bullet">
///   <item>Sign in is the default tab — most visits to Welcome are returning riders.
///     Register is one click away.</item>
///   <item>Empty email on the Register tab always shows the recovery-trade-off callout,
///     so no one can register without seeing what they gave up.</item>
///   <item>The password composition rule is stated in plain text (§7.2 v0.22): 6+ chars,
///     an uppercase letter, a lowercase letter and a digit — no special-character rule.</item>
///   <item>Server-side per-rule password errors (from Identity's <c>ValidationProblemDetails</c>)
///     survive the round trip and render as a list, so a user seeing "Too short" learns
///     what to fix, not "The details you entered were rejected."</item>
/// </list>
/// </summary>
[Collection(SourceOfferFooterCollection.Name)]
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

	/// <summary>
	/// The current design opens Welcome on the Sign in tab. Every test that needs the
	/// Register form has to switch to it first — otherwise it asserts against the sign-in
	/// form, which has no password-composition copy and no recovery callout.
	/// </summary>
	private static async Task ClickRegisterTabAsync(IRenderedComponent<Welcome> component)
	{
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement registerTab = component.FindAll("button.tab")
				.First(b => b.TextContent.Contains("Register", StringComparison.Ordinal));
			registerTab.Click();
		});
	}

	[Fact]
	public async Task PasswordCompositionCopy_IsPresent_AndMentionsNoSpecialChar()
	{
		WireServices();

		IRenderedComponent<Welcome> component = Render<Welcome>();
		await ClickRegisterTabAsync(component);

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
	public async Task EmptyEmail_ShowsTheRecoveryTradeOffCallout()
	{
		WireServices();

		IRenderedComponent<Welcome> component = Render<Welcome>();
		await ClickRegisterTabAsync(component);

		component.WaitForAssertion(() =>
		{
			// Register tab, email is empty, so the "no recovery" callout is visible.
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
		await ClickRegisterTabAsync(component);

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
		await ClickRegisterTabAsync(component);

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

	/// <summary>
	/// The meter is Register-only, and only once something is typed. On Sign in the password
	/// is one the rider already has — grading it there is advice about a decision they cannot
	/// act on from that form.
	/// </summary>
	[Fact]
	public async Task StrengthMeter_AppearsOnRegisterAsTheRiderTypes_AndNeverOnSignIn()
	{
		WireServices();

		IRenderedComponent<Welcome> component = Render<Welcome>();

		// Sign in is the default tab. Even with a password in the box there is no meter.
		await component.InvokeAsync(() => component.Find("input[type=password]").Input("Ride4mountains"));

		component.FindAll(".pw-strength").ShouldBeEmpty(
			"§7.2: sign-in does not grade a password the rider chose long ago.");

		await ClickRegisterTabAsync(component);

		// An empty field draws no meter at all — asserted on the component itself, since a
		// DEBUG build pre-fills this form's password to save typing during development.
		await component.InvokeAsync(() => component.Find("input[type=password]").Input("Ride4mountains"));

		component.WaitForAssertion(() =>
		{
			component.FindAll(".pw-strength").Count.ShouldBe(1);
			component.Markup.Contains("Good", StringComparison.Ordinal).ShouldBeTrue(
				"the meter states its verdict in a word, not colour alone.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// A password that will be refused says so while the rider is still in the field. This is
	/// the only warning left on the way in — v0.23 removed the breach check, so nothing
	/// downstream will second-guess a password that passes the composition rules.
	/// </summary>
	[Fact]
	public async Task StrengthMeter_NamesTheRulesAPasswordStillBreaks()
	{
		WireServices();

		IRenderedComponent<Welcome> component = Render<Welcome>();
		await ClickRegisterTabAsync(component);

		await component.InvokeAsync(() => component.Find("input[type=password]").Input("abcdef"));

		component.WaitForAssertion(() =>
		{
			string markup = component.Markup;
			markup.Contains("Weak", StringComparison.Ordinal).ShouldBeTrue();
			markup.Contains("an uppercase letter", StringComparison.Ordinal).ShouldBeTrue();
			markup.Contains("a digit", StringComparison.Ordinal).ShouldBeTrue();
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// Both forms carry the reveal button. On Register it is how a rider checks the password
	/// they are inventing; on Sign in it is how they find the typo — and on an account with no
	/// email there is no reset path to fall back on when they cannot.
	/// </summary>
	[Fact]
	public async Task ShowPasswordButton_IsOnBothForms_AndTogglesTheField()
	{
		WireServices();

		IRenderedComponent<Welcome> component = Render<Welcome>();

		component.FindAll("button.pw-reveal").Count.ShouldBe(1, "sign in has one password field.");

		await component.InvokeAsync(() => component.Find("button.pw-reveal").Click());

		component.FindAll("input#signin-password[type=text]").Count.ShouldBe(1,
			"the sign-in password is now readable.");

		await ClickRegisterTabAsync(component);

		component.WaitForAssertion(() =>
		{
			// Switching tabs re-masks: the reveal is a deliberate act on the field in front of
			// the rider, not a setting that follows them from one form to the next.
			component.FindAll("input#register-password[type=password]").Count.ShouldBe(1);
			component.Find("button.pw-reveal").GetAttribute("aria-label").ShouldBe("Show password");
		}, timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find("button.pw-reveal").Click());

		component.FindAll("input#register-password[type=text]").Count.ShouldBe(1);
	}

	[Fact]
	public async Task SignInTab_IsActiveByDefault_AndRegisterTabSwitches()
	{
		WireServices();

		IRenderedComponent<Welcome> component = Render<Welcome>();

		component.WaitForAssertion(() =>
		{
			// Welcome opens on Sign in — the common return path for a signed-out rider.
			AngleSharp.Dom.IElement signInTab = component.FindAll("button.tab")
				.First(b => b.TextContent.Contains("Sign in", StringComparison.Ordinal));
			signInTab.ClassList.Contains("active").ShouldBeTrue("Sign in is the default tab.");

			// The recovery callout is register-only; the sign-in form must not carry it.
			component.Markup.Contains("No email means no recovery", StringComparison.Ordinal).ShouldBeFalse(
				"the callout is register-only — the sign-in form has no email field.");
		}, timeout: TimeSpan.FromSeconds(3));

		// Switching to Register brings the callout back — a rider who lands here can still
		// choose to sign up, and needs to see the trade-off before they do.
		await ClickRegisterTabAsync(component);

		component.WaitForAssertion(() =>
		{
			AngleSharp.Dom.IElement registerTab = component.FindAll("button.tab")
				.First(b => b.TextContent.Contains("Register", StringComparison.Ordinal));
			registerTab.ClassList.Contains("active").ShouldBeTrue();
			component.Markup.Contains("No email means no recovery", StringComparison.Ordinal).ShouldBeTrue(
				"§7.2: the recovery-trade-off callout is visible whenever the Register form is showing with an empty email.");
		}, timeout: TimeSpan.FromSeconds(3));
	}
}
