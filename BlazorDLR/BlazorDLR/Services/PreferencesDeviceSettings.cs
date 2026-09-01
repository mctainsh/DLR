using BlazorDLR.Shared.Services;

namespace BlazorDLR.Services;

/// <summary>
/// The mobile binding for <see cref="IDeviceSettings"/> - MAUI <c>Preferences</c>, which is
/// <c>NSUserDefaults</c> on iOS and <c>SharedPreferences</c> on Android. Survives app
/// restarts, is scoped to the install, and never leaves the handset.
/// <para>
/// Not <c>SecureStorage</c>: that is for the refresh token (<see cref="ITokenStore"/>), it is
/// backed by the Keychain / Keystore, and it is slow enough that reading a display preference
/// through it would be felt. A line colour is not a secret.
/// </para>
/// </summary>
public sealed class PreferencesDeviceSettings : IDeviceSettings
{
	/// <inheritdoc />
	public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(Preferences.Default.ContainsKey(key) ? Preferences.Default.Get<string?>(key, null) : null);

	/// <inheritdoc />
	public ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default)
	{
		Preferences.Default.Set(key, value);
		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
	{
		Preferences.Default.Remove(key);
		return ValueTask.CompletedTask;
	}
}
