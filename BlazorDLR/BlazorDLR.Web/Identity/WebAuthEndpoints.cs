using DLR.Core.Contracts.Identity;
using DLR.Server.Data.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DLR.Server.Identity;

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
public static class WebAuthEndpoints
{
	/// <summary>Route name for the browser sign-in.</summary>
	public const string LoginRouteName = "WebLogin";

	/// <summary>Route name for the browser sign-out.</summary>
	public const string LogoutRouteName = "WebLogout";

	/// <summary>Route name for the browser registration.</summary>
	public const string RegisterRouteName = "WebRegister";

	/// <summary>Route name for the cookie-to-access-token exchange.</summary>
	public const string TokenRouteName = "WebToken";

	/// <summary>Route name for the antiforgery token the WASM client fetches once at start-up.</summary>
	public const string AntiforgeryRouteName = "WebAntiforgery";
}

/// <summary>
/// The browser's half of §7.4 (§7.5, §18.5).
/// <para>
/// <strong>These exist because a cookie cannot be set from inside an already-running WASM client.</strong>
/// §7.5 is explicit about it, and about how it fails: the sign-in appears to work and the next
/// request is anonymous. So sign-in, sign-out and registration are ordinary endpoints the browser
/// reaches directly — form posts from a static page, or <c>fetch</c> from a page that has not
/// booted the client yet — and only the token exchange is called from running WASM.
/// </para>
/// <para>
/// Everything else is §7.4 unchanged: same rotation, same reuse detection, same device list, same
/// revocation. The only differences are where the refresh token lives and how long it lasts.
/// </para>
/// </summary>
[ApiController]
public sealed class WebAuthController : ControllerBase
{
	/// <summary>
	/// The CSRF pair. A <c>GET</c>, because it establishes state rather than acting on any.
	/// </summary>
	[HttpGet("/api/v1/auth/web/antiforgery", Name = WebAuthEndpoints.AntiforgeryRouteName)]
	[AllowAnonymous]
	[EndpointSummary("The request token the client must send back on the token endpoint.")]
	public IActionResult Antiforgery([FromServices] IAntiforgery antiforgery)
	{
		AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(HttpContext);

		return Ok(new AntiforgeryToken(tokens.HeaderName!, tokens.RequestToken!));
	}

	// Antiforgery is off on the login and register endpoints, deliberately, and §7.5 scopes it
	// that way: the cost of choosing a cookie is "CSRF exposure on exactly one endpoint — the
	// token endpoint". These two carry credentials in the body, so there is nothing to forge
	// without already knowing the password; what is left is login-CSRF, which forces a victim
	// into the *attacker's* account and is refused by the SameSite=Strict cookie in any case.
	[HttpPost("/api/v1/auth/web/login", Name = WebAuthEndpoints.LoginRouteName)]
	[AllowAnonymous]
	[IgnoreAntiforgeryToken]
	[EndpointSummary("Signs a browser in and sets the HttpOnly refresh cookie.")]
	public async Task<IActionResult> LoginAsync(
		[FromForm] string userName,
		[FromForm] string password,
		[FromServices] UserManager<AppUser> users,
		[FromServices] SessionFactory sessions,
		[FromServices] DummyPasswordVerifier dummy,
		[FromServices] WebSessionCookie cookie,
		[FromServices] RequestThrottle throttle,
		[FromServices] IOptions<RateLimitOptions> limits)
	{
		// §7.8's two rows, unchanged. A browser is not a reason to relax either — non-short-
		// circuiting `&` so both counters record the attempt, exactly as the password grant does.
		bool withinLimits =
			throttle.TryAcquire(
				$"login-ip:{HttpContext.Connection.RemoteIpAddress}",
				limits.Value.LoginPerMinutePerAddress,
				TimeSpan.FromMinutes(1))
			& throttle.TryAcquire(
				$"login-user:{(userName ?? string.Empty).ToUpperInvariant()}",
				limits.Value.LoginPerHourPerUserName,
				TimeSpan.FromHours(1));

		if (!withinLimits)
		{
			return StatusCode(StatusCodes.Status429TooManyRequests);
		}

		AppUser? user = string.IsNullOrWhiteSpace(userName)
			? null
			: await users.FindByNameAsync(userName);

		if (user is null)
		{
			// The same timing defence §7.4 already applies. A browser endpoint that answered an
			// unknown username faster would reintroduce the enumeration oracle on a new route.
			dummy.BurnTime(password ?? string.Empty);

			return Unauthorised(TokenEndpoints.InvalidCredentials);
		}

		if (await users.IsLockedOutAsync(user))
		{
			return Unauthorised("Too many failed sign-in attempts. Try again in a few minutes.");
		}

		if (!await users.CheckPasswordAsync(user, password ?? string.Empty))
		{
			await users.AccessFailedAsync(user);

			return Unauthorised(TokenEndpoints.InvalidCredentials);
		}

		await users.ResetAccessFailedCountAsync(user);

		return await IssueAsync(user, sessions, cookie);
	}

	[HttpPost("/api/v1/auth/web/register", Name = WebAuthEndpoints.RegisterRouteName)]
	[AllowAnonymous]
	[IgnoreAntiforgeryToken]
	[EndpointSummary("Registers from a browser and sets the HttpOnly refresh cookie.")]
	public async Task<IActionResult> RegisterAsync(
		[FromForm] string userName,
		[FromForm] string password,
		[FromForm] string? email,
		[FromServices] RegistrationService registrations,
		[FromServices] SessionFactory sessions,
		[FromServices] WebSessionCookie cookie)
	{
		RegistrationOutcome outcome = await registrations.RegisterAsync(
			new RegisterRequest(userName, password, email),
			HttpContext);

		if (outcome.Problem is { } problem)
		{
			return problem;
		}

		return await IssueAsync(outcome.User!, sessions, cookie);
	}

	/// <summary>
	/// The one call a running WASM client makes, and the one that needs antiforgery (§7.5).
	/// </summary>
	[HttpPost("/api/v1/auth/web/token", Name = WebAuthEndpoints.TokenRouteName)]
	[AllowAnonymous]
	[EndpointSummary("Exchanges the refresh cookie for an access token, rotating both.")]
	public async Task<IActionResult> TokenAsync(
		[FromServices] IAntiforgery antiforgery,
		[FromServices] RefreshTokenService refresh,
		[FromServices] UserManager<AppUser> users,
		[FromServices] SessionFactory sessions,
		[FromServices] ActivityTracker activity,
		[FromServices] WebSessionCookie cookie)
	{
		// The cost of putting the token in a cookie is that the browser attaches it to any request
		// a third-party page can cause. SameSite=Strict already refuses those, but it is one
		// attribute in one place, so the endpoint checks as well rather than relying on it alone.
		try
		{
			await antiforgery.ValidateRequestAsync(HttpContext);
		}
		catch (AntiforgeryValidationException)
		{
			return Problem(
				StatusCodes.Status400BadRequest,
				"Antiforgery check failed",
				"Fetch a request token from /api/v1/auth/web/antiforgery and send it back.");
		}

		if (WebSessionCookie.Read(HttpContext.Request) is not { } presented)
		{
			return Unauthorised("This browser has no session.");
		}

		RefreshOutcome outcome = await refresh.RedeemAsync(presented);

		if (outcome.Status is not RefreshStatus.Rotated || outcome.RefreshToken is null)
		{
			// The cookie is cleared on every refusal, whatever the reason. Leaving a value the
			// server will never accept again means the browser presents it on every start-up and
			// the tab looks broken rather than signed out.
			cookie.Clear(HttpContext.Response, HttpContext.Request.IsHttps);

			return outcome.Status switch
			{
				RefreshStatus.FamilyRevoked => Unauthorised(
					"This session was ended because its refresh token was used twice. Sign in again."),

				RefreshStatus.AccountDeleted => Unauthorised(
					"This account was deleted after 180 days without use."),

				_ => Unauthorised("This session has expired. Sign in again."),
			};
		}

		AppUser? user = await users.FindByIdAsync(outcome.UserId.ToString());

		if (user is null)
		{
			cookie.Clear(HttpContext.Response, HttpContext.Request.IsHttps);

			return Unauthorised("This account no longer exists.");
		}

		await activity.RecordAsync(user.Id, outcome.DeviceId);

		TokenResponse session = sessions.Continue(user, outcome.DeviceId, outcome.RefreshToken);

		cookie.Write(HttpContext.Response, outcome.RefreshToken, HttpContext.Request.IsHttps);

		return Ok(Strip(session));
	}

	[HttpPost("/api/v1/auth/web/logout", Name = WebAuthEndpoints.LogoutRouteName)]
	[AllowAnonymous]
	[EndpointSummary("Ends the browser session and clears the cookie.")]
	public async Task<IActionResult> LogoutAsync(
		[FromServices] RefreshTokenService refresh,
		[FromServices] WebSessionCookie cookie)
	{
		if (WebSessionCookie.Read(HttpContext.Request) is { } presented)
		{
			// Revoked server-side, not merely forgotten. Clearing the cookie alone would leave a
			// working token in whatever else has a copy of it — which on a shared computer is the
			// only scenario that made web sessions expire in the first place (§18.5).
			await refresh.RevokeByTokenAsync(presented, RevocationReasons.SignedOut);
		}

		cookie.Clear(HttpContext.Response, HttpContext.Request.IsHttps);

		return NoContent();
	}

	private async Task<IActionResult> IssueAsync(
		AppUser user,
		SessionFactory sessions,
		WebSessionCookie cookie)
	{
		// DeviceKind.Web is decided here, by which endpoint was reached — never taken from the
		// request. A browser that could ask for a mobile session would have talked its way out of
		// the thirty-day window this whole file exists to impose.
		TokenResponse session = await sessions.BeginAsync(
			user,
			claimedDeviceId: null,
			deviceName: BrowserName(HttpContext),
			kind: DeviceKind.Web);

		cookie.Write(HttpContext.Response, session.RefreshToken, HttpContext.Request.IsHttps);

		return Ok(Strip(session));
	}

	/// <summary>
	/// The response a browser gets: everything except the refresh token.
	/// <para>
	/// <strong>This is the test.</strong> A cookie the JavaScript cannot read is worth nothing if
	/// the same value is also in the JSON body the client just parsed — the XSS that §7.5 is
	/// guarding against would read it straight out of there.
	/// </para>
	/// </summary>
	private static TokenResponse Strip(TokenResponse session) =>
		session with { RefreshToken = string.Empty };

	/// <summary>
	/// A name the rider will recognise in the §7.10 session list. Coarse on purpose — the browser
	/// family and nothing else; a full user-agent string is a fingerprint stored for no purpose.
	/// </summary>
	private static string BrowserName(HttpContext http)
	{
		string agent = http.Request.Headers.UserAgent.ToString();

		return agent switch
		{
			_ when agent.Contains("Firefox", StringComparison.OrdinalIgnoreCase) => "Firefox",
			_ when agent.Contains("Edg", StringComparison.Ordinal) => "Edge",
			_ when agent.Contains("Chrome", StringComparison.Ordinal) => "Chrome",
			_ when agent.Contains("Safari", StringComparison.Ordinal) => "Safari",
			_ => "Web browser",
		};
	}

	private static ObjectResult Unauthorised(string detail) =>
		Problem(StatusCodes.Status401Unauthorized, "Sign-in failed", detail);

	private static ObjectResult Problem(int status, string title, string detail) =>
		new(new ProblemDetails { Status = status, Title = title, Detail = detail })
		{
			StatusCode = status,
			ContentTypes = { "application/problem+json" },
		};
}

/// <summary>
/// The antiforgery pair a browser client needs (§7.5).
/// </summary>
/// <param name="HeaderName">Which header to send the token back in.</param>
/// <param name="RequestToken">The token itself. Its partner is a cookie the browser holds.</param>
public sealed record AntiforgeryToken(string HeaderName, string RequestToken);
