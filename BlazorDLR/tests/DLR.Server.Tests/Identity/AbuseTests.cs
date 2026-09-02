using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using DLR.Core.Contracts.Identity;
using DLR.Server.Data.Identity;
using DLR.Server.Identity;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.Server.Tests.Identity;

/// <summary>
/// The IP ladder, the rate-limit table and the forwarded headers everything else rests on
/// (§7.8).
/// </summary>
public sealed class AbuseTests(PostgresFixture postgres)
{
	private const string RegisterUrl = "/api/v1/auth/register";
	private const string TokenUrl = "/api/v1/auth/token";
	private const string ForgotUrl = "/api/v1/auth/forgot-password";

	private static readonly Dictionary<string, string?> RealLimits = new()
	{
		["RateLimits:LoginPerMinutePerAddress"] = "5",
		["RateLimits:LoginPerHourPerUserName"] = "10",
		["RateLimits:RegisterPerHourPerAddress"] = "10",
		["RateLimits:ForgotPerHourPerEmail"] = "3",
		["RateLimits:ForgotPerHourPerAddress"] = "10",
		["RateLimits:RefreshPerMinutePerDevice"] = "30",
	};

	/// <summary>
	/// First for a reason (§7.8). Get this wrong and every request looks like it came from
	/// Caddy, so the ladder sees <em>all</em> signups as one address and asks the fourth user
	/// the service ever has for an email. It does not weaken rate limiting; it breaks
	/// registration for everybody.
	/// </summary>
	[Fact]
	public async Task Registration_LadderUsesForwardedClientIp()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient first = app.CreateClient().From("203.0.113.10");
		using HttpClient second = app.CreateClient().From("203.0.113.11");

		await first.RegisterAsync("RiderA");
		await first.RegisterAsync("RiderB");
		await first.RegisterAsync("RiderC");

		// A different address, so the ladder must not have counted these together.
		using HttpResponseMessage elsewhere = await second.PostRegisterAsync("RiderD");

		elsewhere.StatusCode.ShouldBe(HttpStatusCode.Created,
			"if this is a 400 then the forwarded header is being ignored and every signup " +
			"shares one bucket");

		IReadOnlyList<IPAddress?> recorded = await app.WithDatabaseAsync(async database =>
			(IReadOnlyList<IPAddress?>)await database.Users
				.OrderBy(user => user.UserName)
				.Select(user => user.CreatedByIp)
				.ToListAsync());

		recorded.Take(3).ShouldAllBe(address => Equals(address, IPAddress.Parse("203.0.113.10")));
		recorded[3].ShouldBe(IPAddress.Parse("203.0.113.11"));
	}

	/// <summary>
	/// The other half of the forwarded-header rule, and the half every other test here would
	/// happily let through: a header from somewhere that is <em>not</em> the reverse proxy must
	/// be ignored.
	/// <para>
	/// Honouring <c>X-Forwarded-For</c> from anyone lets a caller pick their own ladder bucket
	/// and their own rate-limit partition by setting a header - which is worse than not reading
	/// it at all, because every limit then looks enforced and is optional. Every other test in
	/// this class asserts the header <em>is</em> honoured, so an over-broad
	/// <c>KnownProxies</c> would pass all of them.
	/// </para>
	/// </summary>
	[Fact]
	public async Task ForwardedHeader_FromAnUntrustedHop_IsIgnored()
	{
		// The test host still connects over loopback; this says loopback is not the proxy.
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: new Dictionary<string, string?>
			{
				["ForwardedHeaders:KnownProxies:0"] = "203.0.113.254",
				["ForwardedHeaders:KnownProxies:1"] = "203.0.113.254",
			});

		using HttpClient pretending = app.CreateClient().From("198.51.100.7");

		await pretending.RegisterAsync("DaveSmith");

		IPAddress? recorded = await app.WithDatabaseAsync(async database =>
			await database.Users
				.Where(user => user.NormalizedUserName == "DAVESMITH")
				.Select(user => user.CreatedByIp)
				.SingleAsync());

		recorded.ShouldNotBe(IPAddress.Parse("198.51.100.7"),
			"a claimed address from an untrusted hop must not become the address of record - " +
			"otherwise the ladder is opt-out by header");
	}

	[Fact]
	public async Task Register_FourthAccountFromSameIpInOneDay_RequiresEmail()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient client = app.CreateClient().From("203.0.113.20");

		for (int account = 1; account <= 3; account++)
		{
			using HttpResponseMessage free = await client.PostRegisterAsync($"Traveller{account}");

			free.StatusCode.ShouldBe(HttpStatusCode.Created,
				"the first three are username and password only");
		}

		using HttpResponseMessage fourth = await client.PostRegisterAsync("Rider4");

		fourth.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		// With an address it goes through - restricted, not refused. There is deliberately no
		// hard cap: carrier-grade NAT means one address can be a whole mobile network, and a
		// flat block would refuse legitimate signups with no path forward.
		using HttpResponseMessage withEmail =
			await client.PostRegisterAsync("Rider4", email: "rider4@example.com");

		withEmail.StatusCode.ShouldBe(HttpStatusCode.Created);

		AppUser stored = await app.WithDatabaseAsync(async database =>
			await database.Users.SingleAsync(user => user.NormalizedUserName == "RIDER4"));

		stored.RequiresEmailConfirmation.ShouldBeTrue();
		stored.IsRestricted.ShouldBeTrue("the address has not been confirmed yet");
	}

	[Fact]
	public async Task Register_FourthAccountFromDifferentIp_DoesNotRequireEmail()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient busy = app.CreateClient().From("203.0.113.30");

		for (int account = 1; account <= 3; account++)
		{
			await busy.RegisterAsync($"Traveller{account}");
		}

		using HttpClient fresh = app.CreateClient().From("203.0.113.31");

		using HttpResponseMessage response = await fresh.PostRegisterAsync("Rider4");

		response.StatusCode.ShouldBe(HttpStatusCode.Created);

		AppUser stored = await app.WithDatabaseAsync(async database =>
			await database.Users.SingleAsync(user => user.NormalizedUserName == "RIDER4"));

		stored.RequiresEmailConfirmation.ShouldBeFalse();
		stored.IsRestricted.ShouldBeFalse();
	}

	[Fact]
	public async Task Register_LadderRollsOffAfterTheWindow()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient client = app.CreateClient().From("203.0.113.40");

		for (int account = 1; account <= 3; account++)
		{
			await client.RegisterAsync($"Traveller{account}");
		}

		app.Clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));

		using HttpResponseMessage response = await client.PostRegisterAsync("Rider4");

		response.StatusCode.ShouldBe(HttpStatusCode.Created,
			"the window is rolling, so yesterday's signups do not hold against today's");
	}

	/// <summary>
	/// The whole reason the ladder counts rows rather than using <c>AddRateLimiter</c> (§7.8).
	/// In-process partitions reset on every deploy and are per-instance, so an attacker just
	/// waits for a restart.
	/// </summary>
	[Fact]
	public async Task Register_LadderCountSurvivesProcessRestart()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using (HttpClient client = app.CreateClient().From("203.0.113.50"))
		{
			for (int account = 1; account <= 3; account++)
			{
				await client.RegisterAsync($"Traveller{account}");
			}
		}

		await using DlrWebApplicationFactory restarted = app.Restart();

		using HttpClient after = restarted.CreateClient().From("203.0.113.50");

		using HttpResponseMessage response = await after.PostRegisterAsync("Rider4");

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
			"a redeploy must not be a way to get three more free accounts");
	}

	[Fact]
	public async Task Register_PastTheThreshold_IssuesATokenCarryingTheRestrictedClaim()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient client = app.CreateClient().From("203.0.113.60");

		for (int account = 1; account <= 3; account++)
		{
			await client.RegisterAsync($"Traveller{account}");
		}

		TokenResponse restricted =
			await client.RegisterAsync("Rider4", email: "rider4@example.com");

		ClaimsOf(restricted).ShouldContain(claim => claim.Type == DlrClaims.Restricted);

		TokenResponse unrestricted = await app.CreateClient().From("203.0.113.61")
			.RegisterAsync("SomebodyElse");

		ClaimsOf(unrestricted).ShouldNotContain(claim => claim.Type == DlrClaims.Restricted);
	}

	/// <summary>
	/// <c>Restricted_UnconfirmedLadderAccount_CanRecordButNotJoinRide</c> and
	/// <c>Restricted_AfterConfirming_CanJoinRide</c> from §7.15, as much of them as exists.
	/// <para>
	/// Rides arrive in SRV-20 and tracks in SRV-16, so there is no endpoint yet to be refused
	/// from or admitted to. What can be asserted now is the thing those tests are really
	/// about: the policy that will guard them, against the tokens the ladder actually issues.
	/// The endpoint-level halves attach in SRV-20.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Restricted_UnconfirmedLadderAccount_IsRefusedByTheNotRestrictedPolicy()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient client = app.CreateClient().From("203.0.113.70");

		for (int account = 1; account <= 3; account++)
		{
			await client.RegisterAsync($"Traveller{account}");
		}

		TokenResponse restricted = await client.RegisterAsync("Rider4", email: "rider4@example.com");

		(await AuthorizeAsync(app, restricted)).Succeeded.ShouldBeFalse(
			"the social surface is what abuse is after, so it is what the ladder shuts");
	}

	[Fact]
	public async Task Restricted_AfterConfirming_IsAdmittedByTheNotRestrictedPolicy()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient client = app.CreateClient().From("203.0.113.80");

		for (int account = 1; account <= 3; account++)
		{
			await client.RegisterAsync($"Traveller{account}");
		}

		TokenResponse restricted = await client.RegisterAsync("Rider4", email: "rider4@example.com");

		// By subject rather than by position: an account can have more than one email waiting.
		string token = TokenFromLink(app.Emails
			.To("rider4@example.com")
			.Single(message => message.Subject.Contains("Confirm", StringComparison.Ordinal))
			.PlainTextBody);

		using HttpResponseMessage confirmed = await client.PostAsJsonAsync(
			"/api/v1/auth/confirm-email",
			new ConfirmEmailRequest(restricted.User.Id, token));

		confirmed.StatusCode.ShouldBe(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());

		TokenResponse now = (await confirmed.Content.ReadFromJsonAsync<TokenResponse>())!;

		ClaimsOf(now).ShouldNotContain(claim => claim.Type == DlrClaims.Restricted);

		(await AuthorizeAsync(app, now)).Succeeded.ShouldBeTrue(
			"one click lifts it for a real person; an abuser needs N working mailboxes");
	}

	[Fact]
	public async Task RateLimit_SixthLoginAttemptInOneMinute_Returns429()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: RealLimits);

		using HttpClient client = app.CreateClient().From("203.0.113.90");

		await client.RegisterAsync("DaveSmith");

		for (int attempt = 1; attempt <= 5; attempt++)
		{
			using HttpResponseMessage allowed = await PostLoginAsync(client, "DaveSmith", "wrong-password");

			allowed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"attempt {attempt} is inside the limit");
		}

		using HttpResponseMessage limited = await PostLoginAsync(client, "DaveSmith", "wrong-password");

		limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

		// A rolling window, not a ban.
		app.Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));

		using HttpResponseMessage afterWindow = await PostLoginAsync(client, "DaveSmith", "wrong-password");

		afterWindow.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task RateLimit_PerIpPartitioning_UsesForwardedClientIp()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: RealLimits);

		using HttpClient noisy = app.CreateClient().From("203.0.113.100");
		using HttpClient quiet = app.CreateClient().From("203.0.113.101");

		await noisy.RegisterAsync("DaveSmith");

		for (int attempt = 1; attempt <= 6; attempt++)
		{
			(await PostLoginAsync(noisy, "DaveSmith", "wrong-password")).Dispose();
		}

		using HttpResponseMessage throttled = await PostLoginAsync(noisy, "DaveSmith", "wrong-password");

		throttled.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

		// Same server, same username, different address - and unaffected. If this is a 429 the
		// partition key is the socket rather than the forwarded address, and one busy mobile
		// network would lock everybody behind it out.
		using HttpResponseMessage elsewhere = await PostLoginAsync(quiet, "DaveSmith", "wrong-password");

		elsewhere.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	/// <summary>
	/// The per-username row of §7.8's table, which the per-address row would never catch: a
	/// distributed attempt on one account comes from a different address every time.
	/// </summary>
	[Fact]
	public async Task RateLimit_EleventhAttemptOnOneUsernameInAnHour_Returns429()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: RealLimits);

		await app.CreateClient().From("203.0.113.110").RegisterAsync("DaveSmith");

		for (int attempt = 1; attempt <= 10; attempt++)
		{
			using HttpClient attacker = app.CreateClient().From($"198.51.100.{attempt}");

			(await PostLoginAsync(attacker, "DaveSmith", "wrong-password")).Dispose();
		}

		using HttpClient another = app.CreateClient().From("198.51.100.200");

		using HttpResponseMessage limited = await PostLoginAsync(another, "DaveSmith", "wrong-password");

		limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests,
			"ten a minute from ten addresses is invisible to a per-address limit");
	}

	[Fact]
	public async Task RateLimit_ForgotPassword_IsCappedPerAddressAndStillAnswers202()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: RealLimits);

		using HttpClient client = app.CreateClient().From("203.0.113.120");

		for (int attempt = 1; attempt <= 5; attempt++)
		{
			using HttpResponseMessage response = await client.PostAsJsonAsync(
				ForgotUrl,
				new ForgotPasswordRequest("someone@example.com"));

			response.StatusCode.ShouldBe(HttpStatusCode.Accepted,
				"a 429 that only appeared for real addresses would undo the whole endpoint");
		}
	}

	/// <summary>
	/// The shipped numbers, asserted separately because the suite runs with them raised out of
	/// the way. Without this the defaults could drift to anything and every test would still
	/// pass.
	/// </summary>
	[Fact]
	public void RateLimits_ShippedDefaultsMatchSectionSevenPointEight()
	{
		RateLimitOptions defaults = new();

		defaults.LoginPerMinutePerAddress.ShouldBe(5);
		defaults.LoginPerHourPerUserName.ShouldBe(10);
		defaults.RegisterPerHourPerAddress.ShouldBe(10);
		defaults.ForgotPerHourPerEmail.ShouldBe(3);
		defaults.ForgotPerHourPerAddress.ShouldBe(10);
		defaults.RefreshPerMinutePerDevice.ShouldBe(30);

		AbuseOptions ladder = new();

		ladder.FreeAccountsPerAddress.ShouldBe(3);
		ladder.LadderWindow.ShouldBe(TimeSpan.FromHours(24));
	}

	/// <summary>
	/// An address someone else holds is refused outright (§7.8), rather than silently dropped
	/// from an account that gets created anyway. The account the caller is asking about already
	/// exists, so the answer that helps is the recovery path to it.
	/// </summary>
	[Fact]
	public async Task Register_DuplicateEmail_IsRefusedAndPointsAtRecovery()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient client = app.CreateClient().From("203.0.113.130");

		TokenResponse owner = await client.RegisterAsync("DaveSmith", email: "dave@example.com");

		app.Emails.Clear();

		using HttpResponseMessage response =
			await client.PostRegisterAsync("SomebodyElse", email: "DAVE@example.com");

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();

		problem.GetProperty("errors").GetProperty(nameof(RegisterRequest.Email))
			.EnumerateArray().ShouldHaveSingleItem()
			.GetString()!.ShouldContain("forgot password");

		// No account, and nothing sent to an address the caller has not proved is theirs.
		bool created = await app.WithDatabaseAsync(database =>
			database.Users.AnyAsync(user => user.NormalizedUserName == "SOMEBODYELSE"));

		created.ShouldBeFalse();
		app.Emails.To("dave@example.com").ShouldBeEmpty();

		AppUser real = await app.WithDatabaseAsync(async database =>
			await database.Users.SingleAsync(user => user.Id == owner.User.Id));

		real.Email.ShouldBe("dave@example.com");
	}

	private static IEnumerable<Claim> ClaimsOf(TokenResponse session) =>
		new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler()
			.ReadJsonWebToken(session.AccessToken)
			.Claims;

	private static async Task<AuthorizationResult> AuthorizeAsync(
		DlrWebApplicationFactory app,
		TokenResponse session)
	{
		IAuthorizationService authorization = app.Services.GetRequiredService<IAuthorizationService>();

		ClaimsPrincipal principal = new(new ClaimsIdentity(ClaimsOf(session), "Bearer"));

		return await authorization.AuthorizeAsync(
			principal,
			resource: null,
			AuthorizationPolicies.NotRestricted);
	}

	private static Task<HttpResponseMessage> PostLoginAsync(
		HttpClient client,
		string userName,
		string password) =>
		client.PostAsJsonAsync(TokenUrl, new TokenRequest(GrantTypes.Password, userName, password));

	private static string TokenFromLink(string body)
	{
		int start = body.IndexOf("token=", StringComparison.Ordinal) + "token=".Length;
		int end = body.IndexOfAny([' ', '\n', '\r'], start);

		return Uri.UnescapeDataString(body[start..(end < 0 ? body.Length : end)]);
	}
}
