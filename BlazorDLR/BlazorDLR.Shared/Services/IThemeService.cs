namespace BlazorDLR.Shared.Services;

/// <summary>
/// The user's colour-scheme preference (§18.6).
/// <para>
/// Three values — <c>System</c>, <c>Dark</c>, <c>Light</c> — because "let me choose" is a
/// legitimate answer and the browser / OS default is the third. A boolean would force a
/// user who set the app to dark on a light device to change the answer whenever they
/// changed the device; a tri-state avoids that.
/// </para>
/// <para>
/// The persistence layer differs per host (localStorage on the web, MAUI <c>Preferences</c>
/// on the phone) but the read/write shape does not. §18.6 keeps the abstraction here and
/// the concrete implementation in each host project.
/// </para>
/// </summary>
public enum AppTheme
{
	/// <summary>Follow the OS / browser preference. The default on first launch is <see cref="Dark"/>,
	/// but a user who explicitly picks <c>System</c> gets the OS answer from there on.</summary>
	System = 0,

	/// <summary>The design's default palette (§18.6). Dark navy body, muted tile panels, white text.</summary>
	Dark = 1,

	/// <summary>A high-contrast light palette. Same layout, inverted surfaces.</summary>
	Light = 2,
}

/// <summary>
/// Reads and writes the user's <see cref="AppTheme"/> preference. Host-specific — the
/// web binding uses browser localStorage, the mobile binding uses MAUI <c>Preferences</c>.
/// </summary>
public interface IThemeService
{
	/// <summary>Reads the persisted preference, or <see cref="AppTheme.Dark"/> if none is set.</summary>
	Task<AppTheme> GetAsync(CancellationToken cancellationToken = default);

	/// <summary>Persists the preference so the next launch reads it back.</summary>
	Task SetAsync(AppTheme theme, CancellationToken cancellationToken = default);
}
