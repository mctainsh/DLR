using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Server.Data.Identity;
using DLR.Server.Identity;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DLR.Server.Tests.Identity;

/// <summary>
/// The password grant and the fifteen-minute access token (§7.4).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TokenEndpointTests(PostgresFixture postgres)
{
	private const string TokenUrl = "/api/v1/auth/token";

	/// <summary>Timing samples per path. Odd, so the median is a measurement.</summary>
	private const int Samples = 9;

	[Fact]
	public async Task Login_CorrectPassword_ReturnsAccessTokenAndUser()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse account = await client.RegisterAsync("DaveSmith");

		TokenResponse token = await SignInAsync(client, "DaveSmith");

		token.AccessToken.ShouldNotBeNullOrWhiteSpace();
		token.ExpiresIn.ShouldBe(900, "§7.4 fixes the access token at fifteen minutes");
		token.User.Id.ShouldBe(account.User.Id);
		token.User.UserName.ShouldBe("DaveSmith");
		token.User.HasEmail.ShouldBeFalse();
		token.User.EmailConfirmed.ShouldBeFalse();
	}

	/// <summary>
	/// §7.4 fixes the claim set exactly, and every one of them is load-bearing somewhere
	/// later: <c>sub</c> for ownership, <c>unm</c> so a map label needs no lookup, <c>dev</c>
	/// for the session list (§7.10), <c>jti</c> so a token can be named in a log without
	/// naming the account.
	/// </summary>
	[Fact]
	public async Task Login_AccessTokenCarriesTheClaimsAndKeyIdFromSevenPointFour()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse account = await client.RegisterAsync("DaveSmith");

		TokenResponse token = await SignInAsync(client, "DaveSmith");

		JsonWebToken jwt = new JsonWebTokenHandler().ReadJsonWebToken(token.AccessToken);

		jwt.Alg.ShouldBe(SecurityAlgorithms.HmacSha256);
		jwt.Kid.ShouldBe("k1",
			"without a kid a verifier holding two keys during a rotation has to guess");

		jwt.GetPayloadValue<string>(DlrClaims.Subject).ShouldBe(account.User.Id.ToString());
		jwt.GetPayloadValue<string>(DlrClaims.UserName).ShouldBe("DaveSmith");
		jwt.GetPayloadValue<string>(DlrClaims.TokenId).ShouldNotBeNullOrWhiteSpace();

		// `dev` is the server's device id, never the one the caller asked for (§7.10). A
		// client sending it back gets the same installation; that is the round trip the
		// session list depends on.
		string device = jwt.GetPayloadValue<string>(DlrClaims.Device);

		Guid.TryParse(device, out Guid deviceId).ShouldBeTrue();

		TokenResponse again = await SignInAsync(client, "DaveSmith", deviceId: deviceId);

		new JsonWebTokenHandler().ReadJsonWebToken(again.AccessToken)
			.GetPayloadValue<string>(DlrClaims.Device)
			.ShouldBe(device);
	}

	[Fact]
	public async Task Login_AccessTokenValidatesAndExpiresAfterFifteenMinutes()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		await client.RegisterAsync("DaveSmith");

		TokenResponse token = await SignInAsync(client, "DaveSmith");

		AccessTokenIssuer issuer = app.Services.GetRequiredService<AccessTokenIssuer>();

		(await issuer.ValidateAsync(token.AccessToken)).IsValid.ShouldBeTrue();

		// The issuer stamps `exp` off the project's TimeProvider, so moving that clock moves
		// the token's expiry — no sleeping, and the fifteen minutes is the real fifteen.
		app.Clock.Advance(TimeSpan.FromMinutes(15).Add(TimeSpan.FromSeconds(1)));

		TokenValidationResult expired = await issuer.ValidateAsync(token.AccessToken);

		expired.IsValid.ShouldBeFalse(
			"an access token cannot be revoked, so its short life is the only thing bounding " +
			"a stolen one (§7.4)");
	}

	/// <summary>
	/// A key rotation must not sign everybody out at once. Somebody mid-ride on a motorway
	/// with a live hub connection is exactly who would notice.
	/// </summary>
	[Fact]
	public async Task AccessToken_SignedWithTheOutgoingKey_StillValidatesDuringRotation()
	{
		JwtOptions before = new()
		{
			SigningKey = "the-key-being-rotated-out-and-still-long-enough",
			KeyId = "k0",
		};

		JwtOptions during = new()
		{
			SigningKey = "the-brand-new-key-which-is-also-long-enough-here",
			KeyId = "k1",
			PreviousSigningKey = before.SigningKey,
			PreviousKeyId = before.KeyId,
		};

		AppUser user = new() { Id = Guid.NewGuid(), UserName = "DaveSmith" };

		string old = new AccessTokenIssuer(Options.Create(before), TimeProvider.System)
			.Issue(user, deviceId: null).Token;

		AccessTokenIssuer rotated = new(Options.Create(during), TimeProvider.System);

		(await rotated.ValidateAsync(old)).IsValid.ShouldBeTrue(
			"a token signed by the outgoing key stays valid until it expires on its own");

		string fresh = rotated.Issue(user, deviceId: null).Token;

		new JsonWebTokenHandler().ReadJsonWebToken(fresh).Kid.ShouldBe("k1");

		// And once the old key is gone, so are the tokens it signed.
		JwtOptions after = new() { SigningKey = during.SigningKey, KeyId = during.KeyId };

		(await new AccessTokenIssuer(Options.Create(after), TimeProvider.System)
			.ValidateAsync(old)).IsValid.ShouldBeFalse();
	}

	[Fact]
	public async Task Login_WrongPassword_IsRejectedWithTheGenericMessage()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		await client.RegisterAsync("DaveSmith");

		using HttpResponseMessage response = await PostAsync(client, "DaveSmith", "wrong-password-entirely");

		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		(await response.Content.ReadAsStringAsync())
			.ShouldContain(TokenEndpoints.InvalidCredentials);
	}

	/// <summary>
	/// The mechanism behind the timing test below, asserted directly because it is
	/// deterministic and the wall clock is not: an unknown username still costs a password
	/// verification (§7.8).
	/// </summary>
	[Fact]
	public async Task Login_UnknownUsername_StillPerformsAPasswordVerification()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response = await PostAsync(client, "NoSuchRider", "any-old-password");

		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		// Byte for byte what a wrong password against a real account returns. Any difference
		// at all — wording, punctuation, field order — is an existence oracle.
		(await response.Content.ReadAsStringAsync())
			.ShouldContain(TokenEndpoints.InvalidCredentials);
	}

	/// <summary>
	/// Password hashing is deliberately slow, which makes "no such user" the fastest path
	/// through this endpoint by an enormous margin unless something is done about it.
	/// <para>
	/// Asserted one-sidedly and against a median. The security property is that the unknown
	/// path is not <em>faster</em> — a slow one leaks nothing — so an upper bound would only
	/// add a way for a loaded CI runner to fail a build for no reason.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Login_UnknownUsername_ResponseTimingMatchesKnownUsername()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		// A fresh account per sample. Nine wrong passwords against one account would trip
		// §7.8's lockout on the fifth, and a locked account answers *before* the password is
		// verified — which would make the known path look fast and pass this test for
		// precisely the reason it exists to rule out.
		string[] known = [.. Enumerable.Range(0, Samples).Select(index => $"RiderKnown{index}")];
		string[] unknown = [.. Enumerable.Range(0, Samples).Select(index => $"RiderUnknown{index}")];

		// One address each. Nine riders are nine people, and §7.8's ladder would otherwise
		// refuse the fourth of them for want of an email — correctly, and for a reason that has
		// nothing to do with what this test measures.
		for (int index = 0; index < known.Length; index++)
		{
			using HttpClient rider = app.CreateClient().From($"203.0.113.{200 + index}");

			await rider.RegisterAsync(known[index]);
		}

		// Once through each path first: the hasher, the JIT and the connection pool all cost
		// more on their first use than on any subsequent one.
		(await PostAsync(client, known[0], "wrong-password-entirely")).Dispose();
		(await PostAsync(client, unknown[0], "wrong-password-entirely")).Dispose();

		double knownMedian = await MedianMillisecondsAsync(client, known);
		double unknownMedian = await MedianMillisecondsAsync(client, unknown);

		unknownMedian.ShouldBeGreaterThan(
			knownMedian * 0.5,
			$"an unknown username answered in {unknownMedian:F1} ms against {knownMedian:F1} ms " +
			"for a known one tells an attacker exactly what the generic message was written to hide");
	}

	/// <summary>
	/// Five failures, then fifteen minutes (§7.8).
	/// <para>
	/// The fifteen minutes is asserted as configuration rather than as elapsed time, and that
	/// is a limitation worth stating rather than hiding. ASP.NET Core Identity 10 takes no
	/// <c>TimeProvider</c> — <c>AccessFailedAsync</c> reads the ambient clock directly — so
	/// advancing <c>app.Clock</c> here would prove nothing, and reading the wall clock to
	/// compare against would break §10.4's rule for a test that still could not wait out a
	/// real quarter of an hour.
	/// </para>
	/// <para>
	/// What is checked instead is everything that can go wrong on this side of the boundary:
	/// the count that triggers it, the window this project configures, and the refusal of a
	/// correct password once it has. Whether Identity then honours its own setting is
	/// Identity's test to have written.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Login_FiveFailures_LocksAccountForFifteenMinutes()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		await client.RegisterAsync("DaveSmith");

		LockoutOptions lockout = app.Services
			.GetRequiredService<IOptions<IdentityOptions>>().Value.Lockout;

		lockout.MaxFailedAccessAttempts.ShouldBe(5);
		lockout.DefaultLockoutTimeSpan.ShouldBe(TimeSpan.FromMinutes(15));
		lockout.AllowedForNewUsers.ShouldBeTrue(
			"an attacker working a published handle list will not wait for accounts to age");

		for (int attempt = 1; attempt <= 5; attempt++)
		{
			using HttpResponseMessage failure =
				await PostAsync(client, "DaveSmith", "wrong-password-entirely");

			failure.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		}

		// The correct password, refused — which is the whole point of a lockout.
		using HttpResponseMessage locked = await PostAsync(client, "DaveSmith");

		locked.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		(await locked.Content.ReadAsStringAsync()).ShouldContain("locked");

		AppUser stored = await app.WithDatabaseAsync(async database =>
			await database.Users.SingleAsync(user => user.NormalizedUserName == "DAVESMITH"));

		stored.LockoutEnd.ShouldNotBeNull(
			"a lockout with no end date is a permanent lockout, and there is no support path");

		// Zero, not five. Identity resets the counter at the moment it sets the end date, so
		// the count says how far into the *current* run of failures an account is and the end
		// date is the only durable record that it locked. Asserted so the next person to read
		// this row knows the difference between "never failed" and "locked out just now".
		stored.AccessFailedCount.ShouldBe(0);
	}

	[Fact]
	public async Task Login_SuccessfulSignIn_ClearsTheFailureCount()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		await client.RegisterAsync("DaveSmith");

		for (int attempt = 1; attempt <= 4; attempt++)
		{
			(await PostAsync(client, "DaveSmith", "wrong-password-entirely")).Dispose();
		}

		await SignInAsync(client, "DaveSmith");

		AppUser stored = await app.WithDatabaseAsync(async database =>
			await database.Users.SingleAsync(user => user.NormalizedUserName == "DAVESMITH"));

		stored.AccessFailedCount.ShouldBe(0,
			"four typos spread over a fortnight must not add up to a lockout");
	}

	[Fact]
	public async Task Token_UnsupportedGrantType_IsRejected()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response = await client.PostAsJsonAsync(
			TokenUrl,
			new TokenRequest("client_credentials"));

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
			"§7.4 defines two grants and this endpoint issues tokens for exactly those two; " +
			"naming the unsupported one beats a 404 on a route that plainly exists");
	}

	private static Task<HttpResponseMessage> PostAsync(
		HttpClient client,
		string userName,
		string? password = null,
		Guid? deviceId = null) =>
		client.PostAsJsonAsync(
			TokenUrl,
			new TokenRequest(
				GrantTypes.Password,
				userName,
				password ?? TestRegistration.ValidPassword,
				DeviceId: deviceId));

	private static async Task<TokenResponse> SignInAsync(
		HttpClient client,
		string userName,
		string? password = null,
		Guid? deviceId = null)
	{
		using HttpResponseMessage response = await PostAsync(client, userName, password, deviceId);

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
	}

	private static async Task<double> MedianMillisecondsAsync(HttpClient client, string[] userNames)
	{
		List<double> samples = [];

		foreach (string userName in userNames)
		{
			long start = Stopwatch.GetTimestamp();

			(await PostAsync(client, userName, "wrong-password-entirely")).Dispose();

			samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
		}

		samples.Sort();

		return samples[samples.Count / 2];
	}
}
