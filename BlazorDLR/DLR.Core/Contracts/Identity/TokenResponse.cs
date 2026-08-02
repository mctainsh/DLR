namespace DLR.Core.Contracts.Identity;

/// <summary>A signed-in session (§7.4).</summary>
/// <param name="AccessToken">JWT, HS256, held in memory and never written to storage.</param>
/// <param name="ExpiresIn">Seconds until <paramref name="AccessToken"/> is no longer valid.</param>
/// <param name="RefreshToken">
/// Opaque, 256 bits of randomness, and effectively permanent. Goes to <c>SecureStorage</c>;
/// the server keeps only its SHA-256. Not a JWT on purpose — it has to be revocable, it is
/// only ever presented to one endpoint, and a self-describing credential is a worse thing to
/// find in a log.
/// </param>
/// <param name="User">Who the caller turned out to be.</param>
public sealed record TokenResponse(
	string AccessToken,
	int ExpiresIn,
	string RefreshToken,
	AuthenticatedUser User);

/// <summary>
/// The account behind a session, as the client needs it to render a first screen.
/// </summary>
/// <param name="Id">The account's identifier; the token's <c>sub</c> claim.</param>
/// <param name="UserName">Permanent, and the map label (§7.2).</param>
/// <param name="HasEmail">Whether a recovery address exists at all.</param>
/// <param name="EmailConfirmed">
/// Whether it has been confirmed. Never a gate on signing in — the only thing that reads it is
/// §7.8's ladder.
/// </param>
public sealed record AuthenticatedUser(
	Guid Id,
	string UserName,
	bool HasEmail,
	bool EmailConfirmed);
