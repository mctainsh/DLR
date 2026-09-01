using BlazorDLR.Shared.Services;

namespace BlazorDLR.Services;

/// <summary>
/// The mobile <see cref="ITokenStore"/> - refresh token in MAUI <see cref="SecureStorage"/> →
/// iOS Keychain / Android Keystore (§7.4, §18.5).
/// <para>
/// Three platform realities this handles rather than discovers:
/// </para>
/// <list type="bullet">
/// <item>
/// <strong>This-device-only accessibility.</strong> A device restored from another phone's
/// backup must not carry a working permanent token onto new hardware. MAUI's
/// <see cref="SecureStorage"/> uses <c>kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly</c>
/// under the hood, so this is the platform default rather than a switch - worth stating so
/// the property is not accidentally regressed by a wrapper.
/// </item>
/// <item>
/// <strong>Decrypt failure is signed-out, not a crash.</strong> Android
/// <see cref="SecureStorage"/> can throw on a decrypt after a backup/restore or a key
/// invalidation (§7.4). Every read wraps this and returns null; §7.9 rules out treating
/// a 401 on the network as signed-out, but a corrupt local vault is a real one.
/// </item>
/// <item>
/// <strong>One key, no fallback.</strong> The refresh token lives at a single
/// <see cref="TokenKey"/>. There is no legacy-key migration in v1 because there is no v0.
/// </item>
/// </list>
/// </summary>
public sealed class SecureStorageTokenStore : ITokenStore
{
	/// <summary>
	/// The SecureStorage key. Constant, and the only one this class reads - Phase 1's
	/// storage layout is deliberately as narrow as it can be.
	/// </summary>
	private const string TokenKey = "dlr.refresh-token";

	/// <inheritdoc />
	public async ValueTask<string?> ReadRefreshTokenAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			return await SecureStorage.Default.GetAsync(TokenKey);
		}
		catch
		{
			// A decrypt failure looks like theft to the token endpoint, and reissuing from a
			// broken vault would be an interminable sign-in loop. Report "no token": the app
			// falls back to the Welcome screen and asks for a password, which is the only
			// remaining recovery path when SecureStorage has said the value is not readable.
			return null;
		}
	}

	/// <inheritdoc />
	public async ValueTask WriteRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrEmpty(refreshToken))
		{
			await ClearAsync(cancellationToken);
			return;
		}

		await SecureStorage.Default.SetAsync(TokenKey, refreshToken);
	}

	/// <inheritdoc />
	public ValueTask ClearAsync(CancellationToken cancellationToken = default)
	{
		SecureStorage.Default.Remove(TokenKey);
		return ValueTask.CompletedTask;
	}
}
