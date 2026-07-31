using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace DLR.Server.Identity;

/// <summary>Accepts the tokens <see cref="AccessTokenIssuer"/> mints (§7.4).</summary>
public static class JwtBearerRegistration
{
	/// <summary>Adds bearer authentication configured from <see cref="JwtOptions"/>.</summary>
	/// <param name="services">The container.</param>
	public static IServiceCollection AddDlrJwtBearer(this IServiceCollection services)
	{
		services
			.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
			.AddJwtBearer();

		// Configured through the options system rather than in the AddJwtBearer callback, so
		// the validation parameters are the ones JwtOptions builds — the same object the
		// issuer signs against. Two hand-written copies of a key list is how a rotation ends
		// with tokens that verify in a test and not in production.
		services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureFromJwtOptions>();

		return services;
	}

	private sealed class ConfigureFromJwtOptions(IOptions<JwtOptions> options, TimeProvider clock)
		: IConfigureNamedOptions<JwtBearerOptions>
	{
		public void Configure(JwtBearerOptions bearer) =>
			Configure(JwtBearerDefaults.AuthenticationScheme, bearer);

		public void Configure(string? name, JwtBearerOptions bearer)
		{
			if (name != JwtBearerDefaults.AuthenticationScheme)
			{
				return;
			}

			bearer.TokenValidationParameters = options.Value.ValidationParameters(clock);

			// The token travels in a header. §7.6 lifts it from the query string for the ride
			// hub and *only* for the ride hub — accepting it globally would scatter
			// credentials through access logs and referrer headers.
			bearer.MapInboundClaims = false;
		}
	}
}
