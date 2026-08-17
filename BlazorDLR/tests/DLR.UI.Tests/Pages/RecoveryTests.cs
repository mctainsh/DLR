using BlazorDLR.Shared.Pages;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Identity;
using DLR.UI.Tests.Components;
using DLR.UI.Tests.Fakes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The three screens the recovery emails need on the other end (§7.7, §7.14). Without them the
/// server sends links that land on nothing, which is the same as an account that cannot be
/// recovered — the exact outcome registration warns about.
/// <list type="bullet">
///   <item><c>ForgotPassword</c> — asks for a link, and says the same thing whether or not the
///     address belongs to anyone (§7.8).</item>
///   <item><c>ResetPassword</c> — the emailed <c>userId</c> + <c>token</c> pair, a new password,
///     and the sign-out on every device that a reset means.</item>
///   <item><c>ConfirmEmail</c> — follows the link on arrival and adopts the session it answers
///     with, so the fresh claims land rather than waiting out the old token.</item>
/// </list>
/// </summary>
[Collection(SourceOfferFooterCollection.Name)]
public sealed class RecoveryTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private FakeApiClient _api = null!;
	private FakeTokenStore _tokens = null!;
	private AuthState _auth = null!;

	private FakeApiClient Wire()
	{
		_api = new FakeApiClient();
		_tokens = new FakeTokenStore();
		FakeTimeProvider clock = new(FixedInstant);
		_auth = new AuthState(_api, _tokens, clock);

		Services.AddSingleton<IApiClient>(_api);
		Services.AddSingleton<ITokenStore>(_tokens);
		Services.AddSingleton<TimeProvider>(clock);
		Services.AddSingleton(_auth);
		Services.AddSingleton<AuthenticationStateProvider>(_auth);
		Services.AddRealAuthorizationPipeline();
		this.CascadeAuthenticationState(_auth);

		return _api;
	}

	/// <summary>
	/// Puts the browser on the emailed link before rendering, so the page reads its two halves
	/// out of the query the way a real arrival does. The URL shape mirrors
	/// <c>AccountEmails.Link</c> — <c>?userId=…&amp;token=…</c>.
	/// </summary>
	private void ArriveAt(string route, Guid userId, string token) =>
		Services.GetRequiredService<NavigationManager>()
			.NavigateTo($"{route}?userId={userId}&token={Uri.EscapeDataString(token)}");

	// ---------- ForgotPassword (§7.7, §7.8) ----------

	[Fact]
	public async Task Forgot_SendsTheTrimmedAddress()
	{
		FakeApiClient api = Wire();

		IRenderedComponent<ForgotPassword> component = Render<ForgotPassword>();

		await component.InvokeAsync(() => component.Find("input[type=email]").Change("  dave@example.com  "));
		await component.InvokeAsync(() => component.Find("form").Submit());

		component.WaitForAssertion(() => api.LastForgotPasswordRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		api.LastForgotPasswordRequest!.Email.ShouldBe("dave@example.com");
	}

	/// <summary>
	/// The enumeration rule, on screen (§7.8). The endpoint answers 202 for an address that
	/// belongs to nobody, so this screen must not have a second message it could show instead —
	/// a page that said "sent" only for real addresses would be a membership test for any
	/// mailbox somebody cared to type.
	/// </summary>
	[Fact]
	public async Task Forgot_SaysTheSameThingWhoeverTheAddressBelongsTo()
	{
		Wire();

		IRenderedComponent<ForgotPassword> component = Render<ForgotPassword>();

		await component.InvokeAsync(() => component.Find("input[type=email]").Change("nobody@example.com"));
		await component.InvokeAsync(() => component.Find("form").Submit());

		component.WaitForAssertion(() =>
		{
			string markup = component.Markup;

			markup.Contains("If that address belongs to an account", StringComparison.Ordinal).ShouldBeTrue(
				"§7.8: the answer is conditional on screen because it is conditional on the wire.");
			markup.Contains("one hour", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
				"the link's life is stated — somebody who comes back tomorrow needs to know why it failed.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Forgot_TransportFailure_IsSurfaced_NotSwallowedAsSuccess()
	{
		FakeApiClient api = Wire();
		api.ForgotPasswordException = new ApiException(new ApiError(
			StatusCode: System.Net.HttpStatusCode.ServiceUnavailable,
			Title: "Could not reach the server.",
			Messages: Array.Empty<string>()));

		IRenderedComponent<ForgotPassword> component = Render<ForgotPassword>();

		await component.InvokeAsync(() => component.Find("input[type=email]").Change("dave@example.com"));
		await component.InvokeAsync(() => component.Find("form").Submit());

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Could not reach the server.", StringComparison.Ordinal).ShouldBeTrue(
				"a failure to ask is not the same as an address that does not exist, and the traveller can act on it.");
			component.Markup.Contains("a reset link is on its way", StringComparison.Ordinal).ShouldBeFalse(
				"claiming a link was sent when the request never landed strands somebody waiting for mail.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	// ---------- ResetPassword (§7.7) ----------

	[Fact]
	public async Task Reset_SendsTheLinksUserIdAndToken_WithTheNewPassword()
	{
		FakeApiClient api = Wire();
		Guid userId = Guid.NewGuid();

		ArriveAt("/reset-password", userId, "reset-token+with/base64");

		IRenderedComponent<ResetPassword> component = Render<ResetPassword>();

		await component.InvokeAsync(() => component.Find("input[type=password]").Input("NewPass9"));
		await component.InvokeAsync(() => component.Find("form").Submit());

		component.WaitForAssertion(() => api.LastResetPasswordRequest.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		ResetPasswordRequest sent = api.LastResetPasswordRequest!;
		sent.UserId.ShouldBe(userId, "the link's userId is what the endpoint resets against.");
		sent.Token.ShouldBe("reset-token+with/base64",
			"the token survives the query round trip — Identity's tokens carry '+' and '/', and a " +
			"mangled one is indistinguishable from an expired link.");
		sent.NewPassword.ShouldBe("NewPass9");
	}

	/// <summary>
	/// A reset revokes every refresh token on the account (§7.7), so the session this device was
	/// holding is already dead when the call returns. Keeping it would leave the app carrying a
	/// token it cannot refresh — and a relaunch adopting the remembered account behind it.
	/// </summary>
	[Fact]
	public async Task Reset_SignsThisDeviceOut_AndSaysEveryOtherOneToo()
	{
		Wire();

		// Signed in on this device when the reset happens — the case where it matters.
		await _auth.ApplySessionAsync(new TokenResponse(
			"access", 900, "refresh", new AuthenticatedUser(Guid.NewGuid(), "Dave", true, true)));

		ArriveAt("/reset-password", Guid.NewGuid(), "token");

		IRenderedComponent<ResetPassword> component = Render<ResetPassword>();

		await component.InvokeAsync(() => component.Find("input[type=password]").Input("NewPass9"));
		await component.InvokeAsync(() => component.Find("form").Submit());

		component.WaitForAssertion(() =>
		{
			_tokens.StoredToken.ShouldBeNull("the refresh token the server just revoked must not be kept.");
			_auth.UserId.ShouldBeNull("the local session ends with the remote ones.");
			component.Markup.Contains("signed out", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
				"§7.7: the sign-out is deliberate, so the screen says it happened rather than leaving " +
				"somebody to discover it on their other phone.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void Reset_IncompleteLink_OffersAFreshOne_AndAsksTheServerNothing()
	{
		FakeApiClient api = Wire();

		// No query at all — a mail client that broke the URL over a line break.
		IRenderedComponent<ResetPassword> component = Render<ResetPassword>();

		component.Markup.Contains("not complete", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
		component.FindAll("a[href='/forgot-password']").ShouldNotBeEmpty(
			"the only useful action on a broken link is asking for another one.");
		component.FindAll("input[type=password]").ShouldBeEmpty(
			"there is nothing to submit — a form here would fail on send for a reason the traveller cannot see.");
		api.Calls.ShouldNotContain(nameof(IApiClient.ResetPasswordAsync));
	}

	/// <summary>
	/// The server tells a refused password and a stale link apart on purpose, because they need
	/// different things from the person reading. Throwing that away here would send somebody
	/// hunting for a new link when all they had to do was choose a longer password.
	/// </summary>
	[Fact]
	public async Task Reset_RefusedPassword_ShowsPerRuleMessages_AndKeepsTheForm()
	{
		FakeApiClient api = Wire();
		api.ResetPasswordException = new ApiException(new ApiError(
			StatusCode: System.Net.HttpStatusCode.BadRequest,
			Title: "One or more validation errors occurred.",
			Messages: new[] { "Passwords must be at least 6 characters." }));

		ArriveAt("/reset-password", Guid.NewGuid(), "token");

		IRenderedComponent<ResetPassword> component = Render<ResetPassword>();

		await component.InvokeAsync(() => component.Find("input[type=password]").Input("short"));
		await component.InvokeAsync(() => component.Find("form").Submit());

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Passwords must be at least 6 characters.", StringComparison.Ordinal)
				.ShouldBeTrue("§18.2: the rule that was broken is the whole answer.");
			component.FindAll("input[type=password]").ShouldNotBeEmpty(
				"the link is still good for an hour — the traveller tries again here, not from a new email.");
			component.FindAll("a[href='/forgot-password']").ShouldBeEmpty(
				"asking for a new link would be wrong advice: nothing is wrong with this one.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Reset_StaleLink_OffersAFreshOne()
	{
		FakeApiClient api = Wire();
		api.ResetPasswordException = new ApiException(new ApiError(
			StatusCode: System.Net.HttpStatusCode.BadRequest,
			Title: "Link not valid",
			Messages: new[] { "That link may have expired, or already been used." }));

		ArriveAt("/reset-password", Guid.NewGuid(), "stale");

		IRenderedComponent<ResetPassword> component = Render<ResetPassword>();

		await component.InvokeAsync(() => component.Find("input[type=password]").Input("NewPass9"));
		await component.InvokeAsync(() => component.Find("form").Submit());

		component.WaitForAssertion(() =>
			component.FindAll("a[href='/forgot-password']").ShouldNotBeEmpty(
				"an expired link is only fixed by a new one, so the screen has to offer it."),
			timeout: TimeSpan.FromSeconds(3));
	}

	// ---------- ConfirmEmail (§7.14) ----------

	[Fact]
	public void Confirm_FollowsTheLinkOnArrival_AndAdoptsTheSessionItAnswersWith()
	{
		FakeApiClient api = Wire();
		Guid userId = Guid.NewGuid();

		ArriveAt("/confirm-email", userId, "confirm-token+slash/and+plus");

		IRenderedComponent<ConfirmEmail> component = Render<ConfirmEmail>();

		component.WaitForAssertion(() =>
		{
			api.LastConfirmEmailRequest.ShouldNotBeNull(
				"the traveller already decided when they tapped the link — a second confirm button is a step to get wrong.");
			api.LastConfirmEmailRequest!.UserId.ShouldBe(userId);
			api.LastConfirmEmailRequest.Token.ShouldBe("confirm-token+slash/and+plus");

			// §7.8's ladder drops the `rst` claim on confirmation, so the fresh session is the
			// point rather than a side effect — the old access token would stay restricted.
			_auth.UserId.ShouldBe(userId);
			component.Markup.Contains("Confirmed", StringComparison.Ordinal).ShouldBeTrue();
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void Confirm_IncompleteLink_AsksTheServerNothing()
	{
		FakeApiClient api = Wire();

		IRenderedComponent<ConfirmEmail> component = Render<ConfirmEmail>();

		component.Markup.Contains("not complete", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
		api.Calls.ShouldNotContain(nameof(IApiClient.ConfirmEmailAsync));
	}

	/// <summary>
	/// The link is single-use, so the commonest failure here is somebody opening it twice — and
	/// on that path nothing is actually wrong. The screen offers both ways out rather than
	/// guessing, because it cannot tell a spent link from a forged one without asking about an
	/// account it may not be signed in to.
	/// </summary>
	[Fact]
	public void Confirm_SpentLink_ExplainsItselfAndOffersBothWaysOut()
	{
		FakeApiClient api = Wire();
		api.ConfirmEmailException = new ApiException(new ApiError(
			StatusCode: System.Net.HttpStatusCode.BadRequest,
			Title: "Link not valid",
			Messages: new[] { "That confirmation link is not valid. It may have expired, or already been used." }));

		ArriveAt("/confirm-email", Guid.NewGuid(), "spent");

		IRenderedComponent<ConfirmEmail> component = Render<ConfirmEmail>();

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("already been used", StringComparison.Ordinal).ShouldBeTrue(
				"§18.2: the server's own words, not a friendlier invention that drops the reason.");
			component.FindAll("a[href='/welcome']").ShouldNotBeEmpty();
			component.FindAll("a[href='/settings/account#email']").ShouldNotBeEmpty(
				"the fresh link comes from Settings → Account, which is where the resend button is.");

			_auth.UserId.ShouldBeNull("a refused link must not sign anybody in.");
		}, timeout: TimeSpan.FromSeconds(3));
	}
}
