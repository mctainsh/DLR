using Microsoft.Extensions.Options;

namespace DLR.Server.Identity;

/// <summary>
/// The browser's refresh token, and the four attributes that make it worth having (§7.5, §18.5).
/// <para>
/// <strong>The whole point is that JavaScript cannot read it.</strong> §7.4 makes refresh tokens
/// effectively permanent, so an XSS bug in a browser build that could reach one would hand over the
/// account outright rather than a session's worth of damage. <c>HttpOnly</c> is the difference
/// between a bad day and an unrecoverable one, and it is the reason this project accepts the CSRF
/// exposure that comes with a cookie — on exactly one endpoint, which is antiforgery-protected.
/// </para>
/// </summary>
/// <param name="options">Where the session length comes from.</param>
public sealed class WebSessionCookie(IOptions<JwtOptions> options)
{
	/// <summary>The cookie's name. Prefixed, so a browser enforces the rest of this for us.</summary>
	/// <remarks>
	/// The <c>__Host-</c> prefix is not decoration: a conforming browser refuses to store the cookie
	/// at all unless it is <c>Secure</c>, has no <c>Domain</c> and is pathed at <c>/</c>. That makes
	/// the attributes below unforgeable by a subdomain — which is the attack a plain name leaves
	/// open, since any host under the registrable domain can set a cookie the parent will send.
	/// </remarks>
	public const string Name = "__Host-dlr-refresh";

	/// <summary>Writes the refresh token into the response.</summary>
	/// <param name="response">The response to set it on.</param>
	/// <param name="refreshToken">The token as issued — its only trip outside the database.</param>
	/// <param name="isHttps">
	/// Whether the request arrived over TLS. <c>Secure</c> is unconditional in production and would
	/// make the cookie unusable over the plain-HTTP loopback a test host and a local `dotnet run`
	/// both use, so it follows the request rather than being hard-coded on.
	/// </param>
	public void Write(HttpResponse response, string refreshToken, bool isHttps) =>
		response.Cookies.Append(Name, refreshToken, Options(isHttps, expire: false));

	/// <summary>Removes it — sign-out, and any refusal that ends the session.</summary>
	/// <param name="response">The response to clear it on.</param>
	/// <param name="isHttps">Whether the request arrived over TLS.</param>
	/// <remarks>
	/// The attributes have to match the ones it was written with or the browser keeps the original,
	/// which is the classic "logout does nothing" bug. Expiring rather than deleting for the same
	/// reason: the delete is the expiry.
	/// </remarks>
	public void Clear(HttpResponse response, bool isHttps) =>
		response.Cookies.Append(Name, string.Empty, Options(isHttps, expire: true));

	/// <summary>Reads it back, or null if the caller is not a browser with a session.</summary>
	/// <param name="request">The incoming request.</param>
	public static string? Read(HttpRequest request) =>
		request.Cookies.TryGetValue(Name, out string? value) && !string.IsNullOrEmpty(value)
			? value
			: null;

	private CookieOptions Options(bool isHttps, bool expire) => new()
	{
		HttpOnly = true,
		Secure = isHttps,

		// Strict, not Lax. Lax would send the cookie on a top-level GET navigation from another
		// site, and this cookie is only ever presented to a POST that mints an access token — there
		// is no cross-site flow it needs to survive, so nothing is lost by refusing all of them.
		SameSite = SameSiteMode.Strict,

		// __Host- requires both of these, and a browser silently drops the cookie if either is
		// wrong. Path "/" because the WASM client is served from the root.
		Path = "/",
		Domain = null,

		// Max-Age rather than Expires, and that is not only style: Expires is an absolute instant,
		// which would need a clock here — and the project's clock is a fake one in tests, so a
		// cookie stamped from it would be dated 2026 while the browser's own clock said otherwise.
		// Max-Age is a duration, which is what "sliding" actually means, and it is rewritten on
		// every rotation. The row is what really decides; this only stops the browser sending a
		// value the server would refuse anyway (§18.5).
		MaxAge = expire ? TimeSpan.Zero : TimeSpan.FromDays(options.Value.WebSessionDays),
	};
}
