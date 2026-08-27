using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Server.Data.Identity;
using DLR.Server.Identity;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Identity;

/// <summary>
/// The browser's session (§7.5, §18.5).
/// <para>
/// Two rules, and they pull in opposite directions from §7.4. A refresh token JavaScript cannot
/// read, because §7.4 makes them effectively permanent and an XSS bug in a browser build would
/// otherwise hand over the account outright. And a session that <em>does</em> expire, because
/// "sign in once, never again" was reasoned about a phone in a pocket behind a passcode, and a
/// browser is frequently a shared computer.
/// </para>
/// </summary>
public sealed class WebSessionTests(PostgresFixture postgres)
{
	private const string LoginUrl = "/api/v1/auth/web/login";
	private const string TokenUrl = "/api/v1/auth/web/token";
	private const string LogoutUrl = "/api/v1/auth/web/logout";

	/// <summary>
	/// The first test, and the whole reason the cookie exists. Two halves, and the second is the
	/// one that is easy to get wrong: <c>HttpOnly</c> is worth nothing if the same value is also in
	/// the JSON body the client just parsed, because that is exactly where an XSS would read it.
	/// </summary>
	[Fact]
	public async Task WebAuth_RefreshTokenIsNotReadableFromJavaScript()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient browser = app.CreateClient().From("203.0.113.20");

		await browser.PostRegisterAsync("DaveSmith");

		using HttpResponseMessage signedIn = await SignInAsync(browser, "DaveSmith");

		signedIn.StatusCode.ShouldBe(HttpStatusCode.OK, await signedIn.Content.ReadAsStringAsync());

		string setCookie = signedIn.Headers
			.GetValues("Set-Cookie")
			.Single(header => header.StartsWith(WebSessionCookie.Name, StringComparison.Ordinal));

		setCookie.ShouldContain("httponly", Case.Insensitive, "JavaScript must not be able to read it");
		setCookie.ShouldContain("samesite=strict", Case.Insensitive);
		setCookie.ShouldContain("path=/", Case.Insensitive, "__Host- requires it");
		setCookie.ShouldNotContain("domain=", Case.Insensitive, "__Host- forbids it");

		// The other half. A cookie the script cannot read, beside the same token in a body the
		// script just parsed, is a cookie that has protected nothing.
		string body = await signedIn.Content.ReadAsStringAsync();

		string token = CookieValue(setCookie);

		token.ShouldNotBeNullOrEmpty();
		body.ShouldNotContain(token, Case.Sensitive, "the refresh token is never in the response body");

		TokenResponse session = (await signedIn.Content.ReadFromJsonAsync<TokenResponse>())!;

		session.AccessToken.ShouldNotBeNullOrEmpty("the access token still comes back — in memory only");
		session.RefreshToken.ShouldBeEmpty();
	}

	/// <summary>
	/// <c>Secure</c> follows the request rather than being hard-coded on, because a cookie marked
	/// Secure over the plain-HTTP loopback that a test host and a local <c>dotnet run</c> both use
	/// is a cookie the browser discards — the sign-in appears to work and the next request is
	/// anonymous, which is §7.5's named failure mode arriving by a different door.
	/// </summary>
	[Fact]
	public async Task WebAuth_OverHttps_MarksTheCookieSecure()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient browser = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
		{
			BaseAddress = new Uri("https://localhost"),
		});

		browser.From("203.0.113.21");

		await browser.PostRegisterAsync("DaveSmith");

		using HttpResponseMessage signedIn = await SignInAsync(browser, "DaveSmith");

		signedIn.Headers
			.GetValues("Set-Cookie")
			.Single(header => header.StartsWith(WebSessionCookie.Name, StringComparison.Ordinal))
			.ShouldContain("secure", Case.Insensitive);
	}

	/// <summary>
	/// §18.5's thirty sliding days. The row is what decides — a cookie's own expiry is the client's
	/// to ignore — so the test advances past it and asks the server.
	/// </summary>
	[Fact]
	public async Task WebAuth_SessionExpiresAfterConfiguredDays()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient browser = app.CreateClient().From("203.0.113.22");

		await browser.PostRegisterAsync("DaveSmith");

		using HttpResponseMessage signedIn = await SignInAsync(browser, "DaveSmith");

		string cookie = CookieValue(SetCookie(signedIn));

		// Comfortably inside the window: still good.
		app.Clock.Advance(TimeSpan.FromDays(29));

		(await ExchangeAsync(app, browser, cookie)).StatusCode.ShouldBe(HttpStatusCode.OK);

		// The successor the exchange just issued, so this is genuinely the sliding window being
		// measured from the last use rather than from the sign-in.
		string next = await LatestCookieAsync(app, browser, cookie);

		app.Clock.Advance(TimeSpan.FromDays(31));

		using HttpResponseMessage expired = await ExchangeAsync(app, browser, next);

		expired.StatusCode.ShouldBe(
			HttpStatusCode.Unauthorized,
			"a browser left alone for a month signs in again (§18.5)");

		// And the browser is told to forget it, rather than left presenting a value the server
		// will never accept — which would make every start-up look broken instead of signed out.
		SetCookie(expired).ShouldContain("max-age=0", Case.Insensitive);
	}

	/// <summary>
	/// The counterpart, and the reason the two are one test each rather than one assertion. §7.4's
	/// permanence is not withdrawn — it is scoped to the device it was reasoned about.
	/// </summary>
	[Fact]
	public async Task MobileAuth_SessionStillNeverExpires()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient phone = app.CreateClient().From("203.0.113.23");

		TokenResponse session = await phone.RegisterAsync("DaveSmith");

		// Far past any browser window, and past the year SRV-09 already tested.
		app.Clock.Advance(TimeSpan.FromDays(400));

		using HttpResponseMessage refreshed = await phone.PostAsJsonAsync(
			"/api/v1/auth/token",
			new TokenRequest(GrantTypes.Refresh, RefreshToken: session.RefreshToken));

		refreshed.StatusCode.ShouldBe(
			HttpStatusCode.OK,
			"a phone is a personal device behind a passcode; the web rule does not reach it");
	}

	/// <summary>
	/// The cost of putting a credential in a cookie is that the browser attaches it to requests
	/// other sites can cause. <c>SameSite=Strict</c> refuses those, but it is one attribute in one
	/// place — §7.5 asks for antiforgery on this endpoint specifically, and this is it.
	/// </summary>
	[Fact]
	public async Task WebAuth_TokenExchangeWithoutAnAntiforgeryToken_IsRefused()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient browser = app.CreateClient().From("203.0.113.24");

		await browser.PostRegisterAsync("DaveSmith");

		using HttpResponseMessage signedIn = await SignInAsync(browser, "DaveSmith");

		string cookie = CookieValue(SetCookie(signedIn));

		using HttpRequestMessage bare = new(HttpMethod.Post, TokenUrl);

		bare.Headers.Add("Cookie", $"{WebSessionCookie.Name}={cookie}");

		using HttpResponseMessage refused = await browser.SendAsync(bare);

		refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}

	/// <summary>
	/// Signing out revokes server-side rather than merely forgetting. Clearing the cookie alone
	/// would leave a working token in whatever else holds a copy — which, on the shared computer
	/// that made web sessions expire at all, is the entire scenario.
	/// </summary>
	[Fact]
	public async Task WebAuth_Logout_RevokesTheTokenAndNotOnlyTheCookie()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient browser = app.CreateClient().From("203.0.113.25");

		await browser.PostRegisterAsync("DaveSmith");

		using HttpResponseMessage signedIn = await SignInAsync(browser, "DaveSmith");

		string cookie = CookieValue(SetCookie(signedIn));

		using HttpRequestMessage logout = new(HttpMethod.Post, LogoutUrl);

		logout.Headers.Add("Cookie", $"{WebSessionCookie.Name}={cookie}");

		using HttpResponseMessage out1 = await browser.SendAsync(logout);

		out1.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		SetCookie(out1).ShouldContain("max-age=0", Case.Insensitive);

		// The token a copy still holds is dead too, which is the half a cookie clear cannot do.
		using HttpResponseMessage replayed = await ExchangeAsync(app, browser, cookie);

		replayed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	/// <summary>
	/// A browser is a device like any other in §7.10's list — it just does not get §7.4's
	/// permanence. The kind is server-decided, from the endpoint reached, never from the request.
	/// </summary>
	[Fact]
	public async Task WebAuth_SignInCreatesAWebDeviceNotAMobileOne()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient browser = app.CreateClient().From("203.0.113.26");

		// Registering through the mobile endpoint first, so the account already has a phone.
		await browser.RegisterAsync("DaveSmith");

		using HttpResponseMessage signedIn = await SignInAsync(browser, "DaveSmith");

		signedIn.StatusCode.ShouldBe(HttpStatusCode.OK);

		List<DeviceKind> kinds = await app.WithDatabaseAsync(database =>
			database.Set<Device>().Select(device => device.Kind).ToListAsync());

		kinds.Count.ShouldBe(2, "a browser is its own device row, not an adoption of the phone's");
		kinds.ShouldContain(DeviceKind.Mobile);
		kinds.ShouldContain(DeviceKind.Web);
	}

	/// <summary>
	/// §7.8's login limits are not relaxed because the caller is a browser — a new route is a new
	/// place for the same attack, not a new attack.
	/// </summary>
	[Fact]
	public async Task WebAuth_UnknownUsername_IsRefusedGenerically()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient browser = app.CreateClient().From("203.0.113.27");

		using HttpResponseMessage refused = await SignInAsync(browser, "NobodyHere");

		refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		(await refused.Content.ReadAsStringAsync())
			.ShouldContain(TokenEndpoints.InvalidCredentials);

		refused.Headers.Contains("Set-Cookie").ShouldBeFalse("nothing was signed in");
	}

	private static Task<HttpResponseMessage> SignInAsync(HttpClient client, string userName) =>
		client.PostAsync(
			LoginUrl,
			new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["userName"] = userName,
				["password"] = TestRegistration.ValidPassword,
			}));

	/// <summary>Runs the exchange properly: antiforgery pair, then the cookie.</summary>
	private static async Task<HttpResponseMessage> ExchangeAsync(
		DlrWebApplicationFactory app,
		HttpClient client,
		string refreshCookie)
	{
		_ = app;

		using HttpResponseMessage issued = await client.GetAsync("/api/v1/auth/web/antiforgery");

		issued.EnsureSuccessStatusCode();

		AntiforgeryToken pair = (await issued.Content.ReadFromJsonAsync<AntiforgeryToken>())!;

		string antiforgeryCookie = issued.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? headers)
			? string.Join("; ", headers.Select(CookiePair))
			: string.Empty;

		using HttpRequestMessage request = new(HttpMethod.Post, TokenUrl);

		request.Headers.Add(pair.HeaderName, pair.RequestToken);
		request.Headers.Add(
			"Cookie",
			$"{WebSessionCookie.Name}={refreshCookie}; {antiforgeryCookie}");

		return await client.SendAsync(request);
	}

	/// <summary>The successor cookie a successful exchange handed back.</summary>
	private static async Task<string> LatestCookieAsync(
		DlrWebApplicationFactory app,
		HttpClient client,
		string current)
	{
		using HttpResponseMessage rotated = await ExchangeAsync(app, client, current);

		rotated.EnsureSuccessStatusCode();

		return CookieValue(SetCookie(rotated));
	}

	private static string SetCookie(HttpResponseMessage response) =>
		response.Headers
			.GetValues("Set-Cookie")
			.Single(header => header.StartsWith(WebSessionCookie.Name, StringComparison.Ordinal));

	private static string CookieValue(string setCookieHeader) =>
		setCookieHeader.Split(';')[0].Split('=', 2)[1];

	private static string CookiePair(string setCookieHeader) => setCookieHeader.Split(';')[0];
}
