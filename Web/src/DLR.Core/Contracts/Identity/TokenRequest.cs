namespace DLR.Core.Contracts.Identity;

/// <summary>The grant types <c>POST /api/v1/auth/token</c> understands (§7.4).</summary>
public static class GrantTypes
{
	/// <summary>Username and password, in exchange for a token pair.</summary>
	public const string Password = "password";

	/// <summary>An existing refresh token, in exchange for its successor.</summary>
	public const string Refresh = "refresh";
}

/// <summary>
/// <c>POST /api/v1/auth/token</c> (§7.4, §7.14).
/// <para>
/// One endpoint with a grant discriminator rather than two endpoints, because the two flows
/// return the same thing and a client's token-refresh path should not be a different URL from
/// its login path.
/// </para>
/// </summary>
/// <param name="GrantType">One of <see cref="GrantTypes"/>.</param>
/// <param name="UserName">Password grant only.</param>
/// <param name="Password">Password grant only.</param>
/// <param name="RefreshToken">Refresh grant only.</param>
/// <param name="DeviceName">
/// What the rider would recognise this installation as — "iPhone 15" (§7.10). Never verified;
/// it exists so somebody can pick the right row in the session list.
/// </param>
/// <param name="DeviceId">
/// The client's stable per-install identifier, which becomes the token's <c>dev</c> claim.
/// Optional while there is nothing for it to point at; §7.10 gives it a row and a session list.
/// </param>
public sealed record TokenRequest(
	string GrantType,
	string? UserName = null,
	string? Password = null,
	string? RefreshToken = null,
	Guid? DeviceId = null,
	string? DeviceName = null);
