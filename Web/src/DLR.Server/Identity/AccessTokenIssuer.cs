using System.Security.Claims;
using DLR.Server.Data.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DLR.Server.Identity;

/// <summary>Mints the fifteen-minute access token described in §7.4.</summary>
/// <param name="options">Signing key, lifetime and the claim audience.</param>
/// <param name="clock">The project's one clock (§10.4), so a test can expire a token.</param>
public sealed class AccessTokenIssuer(IOptions<JwtOptions> options, TimeProvider clock)
{
	private readonly JwtOptions _options = options.Value;

	/// <summary>Issues a token for a signed-in account.</summary>
	/// <param name="user">Who signed in.</param>
	/// <param name="deviceId">The client's install identifier, if it sent one (§7.10).</param>
	public IssuedAccessToken Issue(AppUser user, Guid? deviceId)
	{
		DateTime issuedAt = clock.GetUtcNow().UtcDateTime;
		DateTime expires = issuedAt.Add(_options.AccessTokenLifetime);

		Dictionary<string, object> claims = new(StringComparer.Ordinal)
		{
			[DlrClaims.Subject] = user.Id.ToString(),
			[DlrClaims.UserName] = user.UserName!,

			// Random per token, so a leaked one can be named in a log without naming the
			// account, and so two tokens issued in the same second are distinguishable.
			[DlrClaims.TokenId] = Guid.NewGuid().ToString("N"),
		};

		if (deviceId is { } device)
		{
			claims[DlrClaims.Device] = device.ToString();
		}

		// Present only while the restriction applies (§7.8), so it disappears on the next
		// token a confirmed account is issued. Absence is the permissive state deliberately:
		// a policy that had to read a value could be defeated by a token that omits it.
		if (user.IsRestricted)
		{
			claims[DlrClaims.Restricted] = "1";
		}

		SecurityTokenDescriptor descriptor = new()
		{
			Claims = claims,
			Issuer = _options.Issuer,
			Audience = _options.Audience,
			IssuedAt = issuedAt,
			NotBefore = issuedAt,
			Expires = expires,

			// The key carries its own KeyId, which JsonWebTokenHandler writes into the
			// header as `kid`. That is what makes two-key rotation work: a verifier holding
			// both keys does not have to guess, and a token signed with the outgoing key
			// stays valid until it expires on its own (§7.4).
			SigningCredentials = new SigningCredentials(
				_options.CurrentKey(),
				SecurityAlgorithms.HmacSha256),
		};

		string token = new JsonWebTokenHandler().CreateToken(descriptor);

		return new IssuedAccessToken(token, (int)_options.AccessTokenLifetime.TotalSeconds);
	}

	/// <summary>Reads a token back, for the tests and for anything that has to verify one.</summary>
	/// <param name="token">The compact JWT.</param>
	public async Task<TokenValidationResult> ValidateAsync(string token) =>
		await new JsonWebTokenHandler().ValidateTokenAsync(token, _options.ValidationParameters(clock));
}

/// <summary>A minted token and how long the client may keep using it.</summary>
/// <param name="Token">The compact JWT.</param>
/// <param name="ExpiresInSeconds">Lifetime, in the form the response reports it.</param>
public readonly record struct IssuedAccessToken(string Token, int ExpiresInSeconds);
