namespace BlazorDLR.Shared.Services.Platform;

/// <summary>
/// An <see cref="IThemeService"/> that lives as long as the process — bound by the SSR pass,
/// which has no browser <c>localStorage</c> and no MAUI <c>Preferences</c> to read from, and
/// used by bUnit tests so a test can set a theme and read it back without a browser.
/// <para>
/// Reads answer <see cref="AppTheme.Dark"/> — the design default (§18.6) — so the prerender
/// paints the shipped theme and the WASM client re-resolves against real storage on boot.
/// </para>
/// </summary>
public sealed class InMemoryThemeService : IThemeService
{
	private AppTheme _theme = AppTheme.Dark;

	/// <inheritdoc />
	public Task<AppTheme> GetAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult(_theme);

	/// <inheritdoc />
	public Task SetAsync(AppTheme theme, CancellationToken cancellationToken = default)
	{
		_theme = theme;
		return Task.CompletedTask;
	}
}
