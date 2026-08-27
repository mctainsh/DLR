using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Server.Data.Identity;
using DLR.Server.Identity;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.Server.Tests.Identity;

/// <summary>
/// Confirmation, reset and change — and the two lifespans that cannot come from one setting
/// (§7.7, §7.12).
/// </summary>
public sealed class EmailAndPasswordTests(PostgresFixture postgres)
{
	private const string SetEmailUrl = "/api/v1/auth/email";
	private const string ConfirmUrl = "/api/v1/auth/confirm-email";
	private const string ForgotUrl = "/api/v1/auth/forgot-password";
	private const string ResetUrl = "/api/v1/auth/reset-password";
	private const string ChangeUrl = "/api/v1/auth/change-password";
	private const string TokenUrl = "/api/v1/auth/token";

	// Distinct from TestRegistration.ValidPassword and, like it, satisfying the composition rules
	// the operator turned on in §7.2's revision — uppercase, lowercase, digit. None of the tests
	// below are about the policy (ChangePassword_NewPasswordBelowPolicy_IsRejected is); the
	// password is fixture data, and it only has to be accepted so the reset and revocation
	// assertions are the ones that can fail.
	private const string NewPassword = "An-Entirely-Different-Passphrase-7";

	/// <summary>
	/// The first test in §7.7, and it exists to catch a refactor rather than a bug.
	/// <para>
	/// Identity's <c>DataProtectionTokenProviderOptions.TokenLifespan</c> is <em>global</em>:
	/// it governs every <c>DataProtectorTokenProvider</c> at once. Setting it to one hour for
	/// reset silently drops confirmation to one hour too, and nothing warns you. Two providers
	/// with their own lifespans is the only way the two numbers stay two numbers.
	/// </para>
	/// </summary>
	[Fact]
	public async Task ResetPassword_LifespanIsIndependentOfConfirmationLifespan()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		PasswordResetTokenProvider reset =
			app.Services.GetRequiredService<PasswordResetTokenProvider>();

		EmailConfirmationTokenProvider confirmation =
			app.Services.GetRequiredService<EmailConfirmationTokenProvider>();

		reset.Lifespan.ShouldBe(TimeSpan.FromHours(1));
		confirmation.Lifespan.ShouldBe(TimeSpan.FromHours(24));

		reset.Lifespan.ShouldNotBe(confirmation.Lifespan,
			"one setting governing both is the bug this test was written for");

		reset.Purpose.ShouldNotBe(confirmation.Purpose,
			"separate purposes are what stop a confirmation link being spent as a reset link");
	}

	/// <summary>
	/// The other half of the separation, and the one that matters most: a 24-hour confirmation
	/// token must not open a password reset. Purposes are checked, not just lifespans.
	/// </summary>
	[Fact]
	public async Task ConfirmationToken_CannotBeSpentAsAPasswordReset()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		string confirmation = await ConfirmationTokenAsync(app, client, session, "dave@example.com");

		using HttpResponseMessage response = await client.PostAsJsonAsync(
			ResetUrl,
			new ResetPasswordRequest(session.User.Id, confirmation, NewPassword));

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		// And the password really did not change.
		await SignInAsync(client, "DaveSmith", TestRegistration.ValidPassword);
	}

	[Fact]
	public async Task ConfirmEmail_TokenJustUnder24Hours_IsAccepted()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		string token = await ConfirmationTokenAsync(app, client, session, "dave@example.com");

		app.Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1));

		using HttpResponseMessage response = await client.PostAsJsonAsync(
			ConfirmUrl,
			new ConfirmEmailRequest(session.User.Id, token));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		TokenResponse fresh = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;

		fresh.User.EmailConfirmed.ShouldBeTrue(
			"confirming changes what the account may do (§7.8), so the caller gets tokens that " +
			"say so rather than staying restricted for another fifteen minutes");
	}

	[Fact]
	public async Task ConfirmEmail_TokenPast24Hours_IsRejected()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		string token = await ConfirmationTokenAsync(app, client, session, "dave@example.com");

		app.Clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));

		using HttpResponseMessage response = await client.PostAsJsonAsync(
			ConfirmUrl,
			new ConfirmEmailRequest(session.User.Id, token));

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		bool confirmed = await app.WithDatabaseAsync(async database =>
			await database.Users
				.Where(user => user.Id == session.User.Id)
				.Select(user => user.EmailConfirmed)
				.SingleAsync());

		confirmed.ShouldBeFalse();
	}

	[Fact]
	public async Task ResetPassword_TokenPast1Hour_IsRejected()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		await ConfirmAsync(app, client, session, "dave@example.com");

		string token = await ResetTokenAsync(app, client, "dave@example.com");

		app.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));

		using HttpResponseMessage response = await client.PostAsJsonAsync(
			ResetUrl,
			new ResetPasswordRequest(session.User.Id, token, NewPassword));

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		// Still the old password, which is the assertion that matters.
		await SignInAsync(client, "DaveSmith", TestRegistration.ValidPassword);
	}

	[Fact]
	public async Task ResetPassword_TokenJustUnderAnHour_IsAccepted()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		await ConfirmAsync(app, client, session, "dave@example.com");

		string token = await ResetTokenAsync(app, client, "dave@example.com");

		app.Clock.Advance(TimeSpan.FromMinutes(59));

		using HttpResponseMessage response = await client.PostAsJsonAsync(
			ResetUrl,
			new ResetPasswordRequest(session.User.Id, token, NewPassword));

		response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());

		await SignInAsync(client, "DaveSmith", NewPassword);
	}

	/// <summary>
	/// The trade-off §7.2 puts on the registration screen, made real: no address, no way back
	/// in. Nothing here is a bug to be fixed later — it is the cost of an account that needs
	/// nothing but a name and a password.
	/// </summary>
	[Fact]
	public async Task ResetPassword_AccountWithoutEmail_HasNoRecoveryPath()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		await client.RegisterAsync("NoEmailNed");

		app.Emails.Clear();

		using HttpResponseMessage response = await client.PostAsJsonAsync(
			ForgotUrl,
			new ForgotPasswordRequest("ned@example.com"));

		response.StatusCode.ShouldBe(HttpStatusCode.Accepted,
			"202 whether or not the address exists — an address is a private identifier, " +
			"unlike a username (§7.8)");

		app.Emails.Sent.ShouldBeEmpty();
	}

	/// <summary>
	/// An address that has been typed but not confirmed is not a recovery path either. It may
	/// belong to somebody who mistyped it, or to somebody else entirely, and honouring it would
	/// turn a typo into an account takeover.
	/// </summary>
	[Fact]
	public async Task ForgotPassword_UnconfirmedAddress_SendsNothingAndStillReturns202()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		(await authed.PostAsJsonAsync(SetEmailUrl, new SetEmailRequest("dave@example.com"))).Dispose();

		app.Emails.Clear();

		using HttpResponseMessage response = await client.PostAsJsonAsync(
			ForgotUrl,
			new ForgotPasswordRequest("dave@example.com"));

		response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
		app.Emails.Sent.ShouldBeEmpty();
	}

	/// <summary>
	/// The one place permanent sessions are deliberately broken (§7.7). If the reset was
	/// triggered by a compromise, leaving the other sessions alive defeats the point of it.
	/// </summary>
	[Fact]
	public async Task ResetPassword_Success_RevokesAllRefreshTokenFamilies()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		await ConfirmAsync(app, client, session, "dave@example.com");

		TokenResponse otherDevice = await SignInAsync(client, "DaveSmith", TestRegistration.ValidPassword);

		string token = await ResetTokenAsync(app, client, "dave@example.com");

		using HttpResponseMessage reset = await client.PostAsJsonAsync(
			ResetUrl,
			new ResetPasswordRequest(session.User.Id, token, NewPassword));

		reset.StatusCode.ShouldBe(HttpStatusCode.NoContent, await reset.Content.ReadAsStringAsync());

		foreach (string refreshToken in new[] { session.RefreshToken, otherDevice.RefreshToken })
		{
			using HttpResponseMessage refused = await client.PostAsJsonAsync(
				TokenUrl,
				new TokenRequest(GrantTypes.Refresh, RefreshToken: refreshToken));

			refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
				"every device signs in again, including the one that did the reset");
		}

		IReadOnlyList<RefreshToken> tokens = await app.WithDatabaseAsync(async database =>
			(IReadOnlyList<RefreshToken>)await database.Set<RefreshToken>().ToListAsync());

		tokens.ShouldAllBe(row => row.RevokedReason == RevocationReasons.PasswordReset);
	}

	/// <summary>
	/// Changing a password in Settings is a safe habit, and signing somebody out of the phone
	/// in their hand for doing it is how a safe habit becomes one people avoid.
	/// </summary>
	[Fact]
	public async Task ChangePassword_Success_KeepsCurrentDeviceSignedIn()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse current = await client.RegisterAsync("DaveSmith");
		TokenResponse elsewhere = await SignInAsync(client, "DaveSmith", TestRegistration.ValidPassword);

		using HttpClient authed = app.CreateClient().Authenticated(current);

		using HttpResponseMessage changed = await authed.PostAsJsonAsync(
			ChangeUrl,
			new ChangePasswordRequest(TestRegistration.ValidPassword, NewPassword));

		changed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await changed.Content.ReadAsStringAsync());

		using HttpResponseMessage stillHere = await client.PostAsJsonAsync(
			TokenUrl,
			new TokenRequest(GrantTypes.Refresh, RefreshToken: current.RefreshToken));

		stillHere.StatusCode.ShouldBe(HttpStatusCode.OK, "this device asked for the change");

		using HttpResponseMessage other = await client.PostAsJsonAsync(
			TokenUrl,
			new TokenRequest(GrantTypes.Refresh, RefreshToken: elsewhere.RefreshToken));

		other.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "every other device signs in again");

		await SignInAsync(client, "DaveSmith", NewPassword);
	}

	[Fact]
	public async Task ChangePassword_WrongCurrentPassword_IsRejectedAndRevokesNothing()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		using HttpResponseMessage response = await authed.PostAsJsonAsync(
			ChangeUrl,
			new ChangePasswordRequest("not-the-current-password", NewPassword));

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		using HttpResponseMessage stillHere = await client.PostAsJsonAsync(
			TokenUrl,
			new TokenRequest(GrantTypes.Refresh, RefreshToken: session.RefreshToken));

		stillHere.StatusCode.ShouldBe(HttpStatusCode.OK);
	}

	/// <summary>A new password is held to the same §7.2 policy as a first one.</summary>
	[Fact]
	public async Task ChangePassword_NewPasswordBelowPolicy_IsRejected()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		using HttpResponseMessage response = await authed.PostAsJsonAsync(
			ChangeUrl,
			new ChangePasswordRequest(TestRegistration.ValidPassword, "short"));

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task SetEmail_StoresItUnconfirmedAndSendsTheLink()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		using HttpResponseMessage response =
			await authed.PostAsJsonAsync(SetEmailUrl, new SetEmailRequest("dave@example.com"));

		response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

		AppUser stored = await app.WithDatabaseAsync(async database =>
			await database.Users.SingleAsync(user => user.Id == session.User.Id));

		stored.Email.ShouldBe("dave@example.com");
		stored.EmailConfirmed.ShouldBeFalse(
			"recovery is enabled by confirming, never by typing — otherwise somebody else's " +
			"address becomes a path into this account");

		app.Emails.To("dave@example.com").ShouldHaveSingleItem();
	}

	[Fact]
	public async Task SetEmail_WithoutAToken_IsRejected()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response =
			await client.PostAsJsonAsync(SetEmailUrl, new SetEmailRequest("dave@example.com"));

		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	/// <summary>
	/// Tokens are stateless: nothing marks one used. What invalidates an outstanding link early
	/// is the security stamp rolling, which a completed reset does (§7.7).
	/// </summary>
	[Fact]
	public async Task ResetPassword_TokenCannotBeUsedTwice()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		await ConfirmAsync(app, client, session, "dave@example.com");

		string token = await ResetTokenAsync(app, client, "dave@example.com");

		using HttpResponseMessage first = await client.PostAsJsonAsync(
			ResetUrl,
			new ResetPasswordRequest(session.User.Id, token, NewPassword));

		first.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		using HttpResponseMessage second = await client.PostAsJsonAsync(
			ResetUrl,
			new ResetPasswordRequest(session.User.Id, token, "a-third-password-entirely"));

		second.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
			"there is no used flag; the security stamp moved and took the link with it");
	}

	private static async Task<string> ConfirmationTokenAsync(
		DlrWebApplicationFactory app,
		HttpClient client,
		TokenResponse session,
		string address)
	{
		using HttpClient authed = app.CreateClient().Authenticated(session);

		app.Emails.Clear();

		using HttpResponseMessage response =
			await authed.PostAsJsonAsync(SetEmailUrl, new SetEmailRequest(address));

		response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());

		return TokenFromLink(app.Emails.To(address).ShouldHaveSingleItem().PlainTextBody);
	}

	private static async Task ConfirmAsync(
		DlrWebApplicationFactory app,
		HttpClient client,
		TokenResponse session,
		string address)
	{
		string token = await ConfirmationTokenAsync(app, client, session, address);

		using HttpResponseMessage response = await client.PostAsJsonAsync(
			ConfirmUrl,
			new ConfirmEmailRequest(session.User.Id, token));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
	}

	private static async Task<string> ResetTokenAsync(
		DlrWebApplicationFactory app,
		HttpClient client,
		string address)
	{
		app.Emails.Clear();

		using HttpResponseMessage response =
			await client.PostAsJsonAsync(ForgotUrl, new ForgotPasswordRequest(address));

		response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

		return TokenFromLink(app.Emails.To(address).ShouldHaveSingleItem().PlainTextBody);
	}

	/// <summary>
	/// Pulled out of the link in the email rather than generated directly, so what is tested is
	/// what a rider would actually click — including the URL encoding on the way through.
	/// </summary>
	private static string TokenFromLink(string body)
	{
		int start = body.IndexOf("token=", StringComparison.Ordinal) + "token=".Length;
		int end = body.IndexOfAny([' ', '\n', '\r'], start);

		return Uri.UnescapeDataString(body[start..(end < 0 ? body.Length : end)]);
	}

	private static async Task<TokenResponse> SignInAsync(
		HttpClient client,
		string userName,
		string password)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			TokenUrl,
			new TokenRequest(GrantTypes.Password, userName, password));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
	}
}
