using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace DLR.Server.Identity;

/// <summary>How access tokens are signed and how long they last (§7.4).</summary>
public sealed class JwtOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "Auth";

	/// <summary>
	/// HS256 needs a 256-bit key, and anything shorter is rejected at startup rather than
	/// producing tokens that are cheaper to forge than to verify.
	/// </summary>
	public const int MinimumSigningKeyBytes = 32;

	/// <summary>Who issued the token.</summary>
	public string Issuer { get; set; } = "dumb-luck-rides";

	/// <summary>Who the token is for.</summary>
	public string Audience { get; set; } = "dumb-luck-rides";

	/// <summary>
	/// The current signing secret. Comes from an environment variable or a Docker secret and
	/// never from a file that ships with the code - a rule the startup check enforces rather
	/// than a convention this comment expresses.
	/// </summary>
	public string SigningKey { get; set; } = string.Empty;

	/// <summary>
	/// Names the current key in the JWT header, so a verifier faced with two keys knows which
	/// one signed what.
	/// </summary>
	public string KeyId { get; set; } = "k1";

	/// <summary>
	/// The key being rotated out, still accepted for verification.
	/// <para>
	/// Without this, rotating the signing key invalidates every access token in existence at
	/// once - which, for people mid-ride with a live hub connection, means being signed out on
	/// a motorway because an operator ran a routine key rotation.
	/// </para>
	/// </summary>
	public string? PreviousSigningKey { get; set; }

	/// <summary>Names <see cref="PreviousSigningKey"/> in the header.</summary>
	public string? PreviousKeyId { get; set; }

	/// <summary>
	/// How long an access token lives. Short because it cannot be revoked: revocation happens
	/// on the refresh token, which is a row somebody can delete (§7.4).
	/// </summary>
	public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

	/// <summary>
	/// How long a refresh token lasts (§7.4). Ten years is the design's way of writing
	/// "never": sessions do not expire, and there is no sliding window and no
	/// re-authentication prompt. The column is set rather than nullable so queries and indexes
	/// stay simple, and so a blanket expiry remains possible without a migration.
	/// </summary>
	public int RefreshTokenYears { get; set; } = 10;

	/// <summary>
	/// How long a <em>browser</em> session lasts, sliding (§7.5, §18.5). Thirty days.
	/// <para>
	/// The one place this project declines to apply §7.4's conclusion. "Sign in once, never again"
	/// was reasoned about a personal phone in a pocket behind a device passcode; a browser is
	/// frequently a shared computer, and carrying a conclusion outside the argument that produced
	/// it would be the mistake. Generous for a browser, and materially safer.
	/// </para>
	/// </summary>
	public int WebSessionDays { get; set; } = 30;

	/// <summary>The key tokens are signed with.</summary>
	public SecurityKey CurrentKey() => KeyFrom(SigningKey, KeyId);

	/// <summary>Every key a token may legitimately have been signed with.</summary>
	public IReadOnlyList<SecurityKey> VerificationKeys()
	{
		List<SecurityKey> keys = [CurrentKey()];

		if (!string.IsNullOrWhiteSpace(PreviousSigningKey))
		{
			keys.Add(KeyFrom(PreviousSigningKey, PreviousKeyId ?? "k0"));
		}

		return keys;
	}

	/// <summary>What a verifier has to be told to accept a token from this issuer.</summary>
	/// <param name="clock">
	/// The project's clock (§10.4). Handed in rather than left to the library, which would
	/// otherwise read the ambient one: the issuer stamps <c>exp</c> from this clock, so a
	/// verifier reading a different one disagrees with it about whether a token has expired.
	/// In production the two are the same and this changes nothing. In a test it is the
	/// difference between being able to advance time and not - which is the whole reason
	/// <c>TimeProvider</c> was registered on day one.
	/// </param>
	public TokenValidationParameters ValidationParameters(TimeProvider clock) => new()
	{
		LifetimeValidator = (notBefore, expires, _, _) =>
		{
			DateTime now = clock.GetUtcNow().UtcDateTime;

			return (notBefore is not { } from || now >= from)
				&& (expires is not { } until || now < until);
		},

		ValidateIssuer = true,
		ValidIssuer = Issuer,
		ValidateAudience = true,
		ValidAudience = Audience,
		ValidateIssuerSigningKey = true,
		IssuerSigningKeys = VerificationKeys(),
		ValidateLifetime = true,

		// The default is five minutes, which would keep a fifteen-minute token alive for
		// twenty. Sessions are permanent by refresh (§7.4); the access token's short life is
		// the only thing bounding a stolen one, so it is not padded.
		ClockSkew = TimeSpan.Zero,

		// Claims are read by the names this project writes. The default mapping rewrites
		// `sub` to a WS-Federation URI, which turns every ClaimTypes lookup into a puzzle.
		NameClaimType = DlrClaims.UserName,
	};

	private static SymmetricSecurityKey KeyFrom(string secret, string keyId) =>
		new(Encoding.UTF8.GetBytes(secret)) { KeyId = keyId };
}

/// <summary>The claim names §7.4 specifies, spelled once.</summary>
public static class DlrClaims
{
	/// <summary>Subject - the account id.</summary>
	public const string Subject = "sub";

	/// <summary>Username. Safe to denormalise anywhere, because it can never change (§7.2).</summary>
	public const string UserName = "unm";

	/// <summary>The device this session belongs to (§7.10).</summary>
	public const string Device = "dev";

	/// <summary>Token identifier.</summary>
	public const string TokenId = "jti";

	/// <summary>Present while the account is restricted by §7.8's ladder.</summary>
	public const string Restricted = "rst";
}
