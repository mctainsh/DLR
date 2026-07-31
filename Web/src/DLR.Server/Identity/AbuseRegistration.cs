using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace DLR.Server.Identity;

/// <summary>The §7.8 rate-limit table, in configuration (§14.5).</summary>
public sealed class RateLimitOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "RateLimits";

	/// <summary>Password-grant attempts per minute, per client address.</summary>
	public int LoginPerMinutePerAddress { get; set; } = 5;

	/// <summary>Password-grant attempts per hour, per username.</summary>
	public int LoginPerHourPerUserName { get; set; } = 10;

	/// <summary>Registrations per hour, per address — a ceiling above the ladder, not a substitute.</summary>
	public int RegisterPerHourPerAddress { get; set; } = 10;

	/// <summary>Reset requests per hour, per address submitted.</summary>
	public int ForgotPerHourPerEmail { get; set; } = 3;

	/// <summary>Reset requests per hour, per client address.</summary>
	public int ForgotPerHourPerAddress { get; set; } = 10;

	/// <summary>Refresh-grant calls per minute, per device.</summary>
	public int RefreshPerMinutePerDevice { get; set; } = 30;
}

/// <summary>Authorization policy names.</summary>
public static class AuthorizationPolicies
{
	/// <summary>
	/// An account not held back by §7.8's ladder. Guards ride creation and joining — the
	/// social surface, which is what abuse would be after. Recording a solo ride is not
	/// guarded, because restricting that would punish the wrong people for nothing.
	/// </summary>
	public const string NotRestricted = "NotRestricted";
}

/// <summary>Forwarded headers, the throttle and the restriction policy (§7.8).</summary>
public static class AbuseRegistration
{
	/// <summary>Registers everything §7.8 needs.</summary>
	/// <param name="services">The container.</param>
	/// <param name="configuration">Where the thresholds and <c>KnownProxies</c> come from.</param>
	public static IServiceCollection AddDlrAbuseControls(
		this IServiceCollection services,
		IConfiguration configuration)
	{
		services.Configure<AbuseOptions>(configuration.GetSection(AbuseOptions.Section));
		services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.Section));

		ConfigureForwardedHeaders(services, configuration);

		services.AddSingleton<RequestThrottle>();
		services.AddScoped<RegistrationLadder>();

		services.AddAuthorizationBuilder()
			.AddPolicy(AuthorizationPolicies.NotRestricted, policy => policy
				.RequireAuthenticatedUser()

				// Absence is the permissive state, deliberately. A policy that had to read a
				// value could be defeated by a token that simply omits it; this one can only
				// be defeated by forging a token without the claim, which is the signature.
				.RequireAssertion(context =>
					!context.User.HasClaim(claim => claim.Type == DlrClaims.Restricted)));

		return services;
	}

	/// <summary>
	/// Trusts the reverse proxy, and only the reverse proxy.
	/// <para>
	/// This is the gotcha with teeth. Every per-address rule in §7.8 — the ladder above all —
	/// reads <c>RemoteIpAddress</c>, and without this middleware every request appears to come
	/// from Caddy. The ladder would then see <em>all</em> signups as one address and demand an
	/// email from the fourth user the service ever had. It does not merely weaken rate
	/// limiting; it breaks registration for everybody.
	/// </para>
	/// <para>
	/// <c>KnownProxies</c> is the other half, and the reason this is not simply switched on.
	/// Honouring <c>X-Forwarded-For</c> from anyone lets a caller choose their own bucket by
	/// setting a header — which is worse than not reading it at all, because the limits then
	/// look enforced and are optional.
	/// </para>
	/// </summary>
	private static void ConfigureForwardedHeaders(IServiceCollection services, IConfiguration configuration)
	{
		services.Configure<ForwardedHeadersOptions>(options =>
		{
			options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

			// The defaults trust one hop from loopback. Cleared so that the configured list is
			// the whole list rather than an addition to something invisible.
			options.KnownProxies.Clear();
			options.KnownIPNetworks.Clear();

			string[] proxies = configuration
				.GetSection("ForwardedHeaders:KnownProxies")
				.Get<string[]>() ?? [];

			foreach (string entry in proxies)
			{
				if (IPAddress.TryParse(entry, out IPAddress? proxy))
				{
					options.KnownProxies.Add(proxy);
				}
			}

			// One hop: Caddy. A longer chain would let whatever is in front of it write the
			// address this server believes.
			options.ForwardLimit = 1;
		});
	}
}
