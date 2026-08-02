namespace BlazorDLR.Shared.Services;

/// <summary>
/// Where the refresh token lives (§7.4, §18.5).
/// <para>
/// <strong>Mobile:</strong> the token is in <c>SecureStorage</c> → iOS Keychain / Android
/// Keystore, and this interface reads and writes it directly.
/// <strong>Web:</strong> the token is in an <c>HttpOnly</c>, <c>Secure</c>, <c>SameSite=Strict</c>,
/// <c>__Host-</c>-prefixed cookie set by the token endpoint. Script cannot read it and must not
/// try to, so the web implementation of this interface is a no-op — the point of the shape is
/// that shared code that persists a token compiles against both hosts and does the right thing
/// on each, rather than each host branching on a runtime flag.
/// </para>
/// </summary>
public interface ITokenStore
{
	/// <summary>Read the stored refresh token, or <c>null</c> when none is present.</summary>
	ValueTask<string?> ReadRefreshTokenAsync(CancellationToken cancellationToken = default);

	/// <summary>Persist a refresh token. The web implementation is a no-op — the cookie is authoritative.</summary>
	ValueTask WriteRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);

	/// <summary>Delete the stored token, if any.</summary>
	ValueTask ClearAsync(CancellationToken cancellationToken = default);
}
