using BlazorDLR.Shared.Services;

namespace BlazorDLR.Shared.State;

/// <summary>
/// The current active theme, in memory, plus a broadcast so a component can re-render
/// on a change without polling the persisted preference every render.
/// <para>
/// One instance per app (scoped in each host). The layout reads the effective value on
/// startup, then subscribes to <see cref="Changed"/>. Setting via
/// <see cref="SetAsync"/> persists through <see cref="IThemeService"/> and fires the event.
/// </para>
/// </summary>
public sealed class ThemeState
{
	private readonly IThemeService _service;
	private AppTheme _preference = AppTheme.Dark;
	private bool _loaded;

	public ThemeState(IThemeService service) => _service = service;

	/// <summary>Fired after <see cref="SetAsync"/> or the initial <see cref="LoadAsync"/>.</summary>
	public event Action? Changed;

	/// <summary>The current stored preference: System / Dark / Light.</summary>
	public AppTheme Preference => _preference;

	/// <summary>
	/// Reads the preference from the host store on first render. Idempotent — a second
	/// call after the initial load is a no-op, so a page that also calls it is safe.
	/// </summary>
	public async Task LoadAsync(CancellationToken cancellationToken = default)
	{
		if (_loaded)
		{
			return;
		}

		_preference = await _service.GetAsync(cancellationToken);
		_loaded = true;
		Changed?.Invoke();
	}

	/// <summary>Persists a new preference and broadcasts the change.</summary>
	public async Task SetAsync(AppTheme theme, CancellationToken cancellationToken = default)
	{
		_preference = theme;
		_loaded = true;
		await _service.SetAsync(theme, cancellationToken);
		Changed?.Invoke();
	}

	/// <summary>
	/// Maps the tri-state preference onto the two attribute values the CSS reads.
	/// <c>System</c> falls back to <see cref="AppTheme.Dark"/> until the host wires
	/// a <c>prefers-color-scheme</c> read — dark is the design default (§18.6).
	/// </summary>
	public string DataAttributeValue => _preference switch
	{
		AppTheme.Light => "light",
		_ => "dark",
	};
}
