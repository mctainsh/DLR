using System.Security.Cryptography;
using DLR.Core.Contracts.Maps;
using DLR.Server.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DLR.Server.Maps;

/// <summary>
/// Apple's MapKit JS credentials, minted server-side (§4.5).
/// <para>
/// <strong>This is the part most likely to be discovered late, which is why it is a task of its
/// own.</strong> MapKit JS authenticates with a short-lived JWT signed ES256 by a private key
/// Apple issues, and that <c>.p8</c> must never reach a client — leaking it means somebody else
/// bills their map usage to this account, in the same class as an APNs key. So the map on the web
/// is a <em>server</em> dependency, and this endpoint is it.
/// </para>
/// </summary>
public sealed class MapKitOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "Maps:MapKit";

	/// <summary>Apple developer team identifier — the token's <c>iss</c>.</summary>
	public string TeamId { get; set; } = string.Empty;

	/// <summary>The MapKit key's identifier — the token's <c>kid</c>.</summary>
	public string KeyId { get; set; } = string.Empty;

	/// <summary>
	/// The ES256 private key, PEM-encoded (§14.3).
	/// <para>
	/// From user secrets locally and the environment in production, never from
	/// <c>appsettings.json</c> — the same rule and the same reasoning as the JWT signing key
	/// (§7.4), and the <c>.p8</c> is on §14.2's never-commit list.
	/// </para>
	/// </summary>
	public string PrivateKeyPem { get; set; } = string.Empty;

	/// <summary>
	/// The domain the token is scoped to. Apple rejects a token used from anywhere else, which is
	/// what stops one lifted out of a browser being usable elsewhere.
	/// </summary>
	public string? Origin { get; set; }

	/// <summary>
	/// How long a minted token lives. Short, and cached client-side until near expiry — the point
	/// is that a token scraped out of a page stops working quickly.
	/// </summary>
	public int LifetimeMinutes { get; set; } = 30;

	/// <summary>Whether this deployment has been given a key at all.</summary>
	public bool IsConfigured =>
		!string.IsNullOrWhiteSpace(TeamId)
		&& !string.IsNullOrWhiteSpace(KeyId)
		&& !string.IsNullOrWhiteSpace(PrivateKeyPem);
}

/// <summary><c>GET /api/v1/maps/token</c> (§4.5).</summary>
public static class MapKitEndpoints
{
	/// <summary>Route name.</summary>
	public const string RouteName = "MapKitToken";

	/// <summary>
	/// What a caller is told when no key is configured. Stated, and stated the same way every
	/// time, because §4.5 is explicit: <em>a map that cannot get a token shows a stated error, not
	/// an empty grey rectangle</em>. A blank map is a worse failure than an honest one, and a
	/// client cannot draw the honest one from a 500.
	/// </summary>
	public const string NotConfiguredTitle = "Map credentials unavailable";

	/// <summary>Maps the token endpoint.</summary>
	public static IEndpointRouteBuilder MapMapKit(this IEndpointRouteBuilder endpoints)
	{
		endpoints
			.MapGet("/api/v1/maps/token", GetAsync)

			// Authenticated, per §4.5. A public token endpoint is a free map quota for the
			// internet, billed to us.
			.RequireAuthorization()
			.WithName(RouteName)
			.WithSummary("A short-lived MapKit JS token. The signing key never leaves the server.");

		return endpoints;
	}

	private static IResult GetAsync(
		IOptions<MapKitOptions> options,
		MapKitSigningKey keys,
		TimeProvider clock,
		HttpContext http,
		RequestThrottle throttle,
		IOptions<RateLimitOptions> limits,
		ILoggerFactory loggers)
	{
		MapKitOptions settings = options.Value;

		if (!throttle.TryAcquire(
			$"maps-token:{http.Connection.RemoteIpAddress}",
			limits.Value.MapTokenPerHourPerAddress,
			TimeSpan.FromHours(1)))
		{
			return Results.StatusCode(StatusCodes.Status429TooManyRequests);
		}

		if (!settings.IsConfigured)
		{
			// 503 rather than 500: this deployment is not broken, it has not been given a key.
			// The distinction is what lets a client say "maps are not available on this server"
			// instead of "something went wrong".
			return Results.Problem(new ProblemDetails
			{
				Status = StatusCodes.Status503ServiceUnavailable,
				Title = NotConfiguredTitle,
				Detail = "This server has no MapKit key configured, so it cannot mint map tokens.",
			});
		}

		DateTimeOffset now = clock.GetUtcNow();
		DateTimeOffset expires = now.AddMinutes(Math.Max(1, settings.LifetimeMinutes));

		try
		{
			SecurityKey signingKey = keys.Resolve()!;

			Dictionary<string, object> claims = new(StringComparer.Ordinal)
			{
				["iss"] = settings.TeamId,
				["iat"] = now.ToUnixTimeSeconds(),
				["exp"] = expires.ToUnixTimeSeconds(),
			};

			if (!string.IsNullOrWhiteSpace(settings.Origin))
			{
				claims["origin"] = settings.Origin;
			}

			string token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
			{
				Claims = claims,
				SigningCredentials = new SigningCredentials(
					signingKey,
					SecurityAlgorithms.EcdsaSha256),
			});

			return Results.Ok(new MapToken(token, expires));
		}
		catch (Exception exception) when (exception is CryptographicException or ArgumentException)
		{
			// A malformed key is a deployment mistake, and it must not be echoed. Logged in full
			// server-side; the caller gets the same stated error as an unconfigured server,
			// because from a client's point of view they are the same situation.
			loggers
				.CreateLogger(typeof(MapKitEndpoints))
				.LogError(exception, "The configured MapKit private key could not be read.");

			return Results.Problem(new ProblemDetails
			{
				Status = StatusCodes.Status503ServiceUnavailable,
				Title = NotConfiguredTitle,
				Detail = "This server's MapKit key could not be read, so it cannot mint map tokens.",
			});
		}
	}
}
