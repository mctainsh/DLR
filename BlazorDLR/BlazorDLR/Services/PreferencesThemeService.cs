using BlazorDLR.Shared.Services;

// Disambiguate — Microsoft.Maui.ApplicationModel also exports an `AppTheme`, and this
// project transitively imports it via global usings. We want ours.
using AppTheme = BlazorDLR.Shared.Services.AppTheme;

namespace BlazorDLR.Services;

/// <summary>
/// The mobile binding for <see cref="IThemeService"/> — reads/writes MAUI
/// <c>Preferences</c> under the key <c>dlr.theme</c>. Survives app restarts and does
/// not travel over the wire.
/// </summary>
public sealed class PreferencesThemeService : IThemeService
{
	private const string Key = "dlr.theme";

	public Task<AppTheme> GetAsync(CancellationToken cancellationToken = default)
	{
		string value = Preferences.Default.Get(Key, "dark");
		AppTheme theme = value switch
		{
			"light" => AppTheme.Light,
			"system" => AppTheme.System,
			_ => AppTheme.Dark,
		};
		return Task.FromResult(theme);
	}

	public Task SetAsync(AppTheme theme, CancellationToken cancellationToken = default)
	{
		string encoded = theme switch
		{
			AppTheme.Light => "light",
			AppTheme.System => "system",
			_ => "dark",
		};
		Preferences.Default.Set(Key, encoded);
		return Task.CompletedTask;
	}
}
