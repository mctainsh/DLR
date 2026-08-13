using BlazorDLR.Shared.Services;

namespace BlazorDLR.Shared.State;

/// <summary>
/// Which tiles this device puts under its maps (§4.5), plus a broadcast so an open map can swap
/// its style the moment the setting changes.
/// <para>
/// One instance per app (scoped in each host), the same shape as <see cref="RouteStyleState"/>:
/// the persisted value is read once through <see cref="IDeviceSettings"/>, kept in memory
/// afterwards, and every change fires <see cref="Changed"/>. A map must never be reading the
/// device store to decide what to draw.
/// </para>
/// <para>
/// <strong><see cref="Chosen"/> and <see cref="Effective"/> are deliberately two things.</strong>
/// The settings screen renders what the rider picked; the map draws what can actually be drawn.
/// They differ whenever an offline source is stored on a device that cannot serve it — a browser,
/// which has no pack store at all (§18.6), or a phone whose pack has been deleted. Collapsing them
/// would mean either a settings screen that silently forgets the rider's choice, or a map with
/// nothing under it.
/// </para>
/// </summary>
public sealed class MapSourceState
{
	private readonly IDeviceSettings _settings;
	private readonly IMapPackStore _packs;

	private MapSource _chosen = MapSource.Default;
	private bool _loaded;
	private bool _written;

	/// <summary>Creates the state over a host's device store.</summary>
	/// <param name="settings">Where the choice is persisted.</param>
	/// <param name="packs">
	/// Where downloaded archives live. Answers whether this host can hold one at all — the browser
	/// hosts bind a store that holds nothing (§18.6), which is what makes <see cref="CanUseOffline"/>
	/// false there.
	/// </param>
	public MapSourceState(IDeviceSettings settings, IMapPackStore packs)
	{
		_settings = settings;
		_packs = packs;
	}

	/// <summary>Fired after the first <see cref="LoadAsync"/> resolves and after every <see cref="SetAsync"/>.</summary>
	public event Action? Changed;

	/// <summary>What the rider picked, whether or not this device can honour it.</summary>
	public MapSource Chosen => _chosen;

	/// <summary>
	/// What a map should actually draw with: <see cref="Chosen"/>, or <see cref="MapSource.Default"/>
	/// when this device cannot serve it.
	/// <para>
	/// The fallback is OSM rather than a stated error because a base map is not the point of any
	/// screen it appears on — the routes, the pins and the rider positions are, and they are all
	/// drawn by the overlay on top. A working map under them beats an explanation.
	/// </para>
	/// </summary>
	public MapSource Effective =>
		_chosen.Kind == MapSourceKind.Offline && !CanUseOffline
			? MapSource.Default
			: _chosen;

	/// <summary>
	/// Whether this host can hold a downloaded archive — false on both browser hosts (§18.6).
	/// <para>
	/// This says nothing about whether one has actually been <em>downloaded</em>. A device that
	/// could hold a pack but holds none still resolves an offline source to OSM, because
	/// <c>IMapPackServer</c> answers no URL for a pack that is not there — see
	/// <c>MapLibreInterop.DescribeAsync</c>, which is where that fallback happens.
	/// </para>
	/// </summary>
	public bool CanUseOffline => _packs.IsSupported;

	/// <summary>Whether the device store has been read yet.</summary>
	public bool IsLoaded => _loaded;

	/// <summary>
	/// Reads the persisted choice. Idempotent — every map calls it on first render and none of them
	/// has to coordinate with the others.
	/// <para>
	/// Callers must run this <em>after</em> first render on the web: the browser store is reached
	/// through JS interop, which does not exist during the prerender pass.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancels the read.</param>
	public async Task LoadAsync(CancellationToken cancellationToken = default)
	{
		if (_loaded)
		{
			return;
		}

		// Set before the read so two maps rendering at once do not start two round trips.
		_loaded = true;

		MapSource stored = MapSource.Decode(await _settings.GetAsync(MapSource.StorageKey, cancellationToken))
			?? MapSource.Default;

		if (_written)
		{
			// The setting was changed while this read was in flight. What is in memory is newer.
			return;
		}

		_chosen = stored;
		Changed?.Invoke();
	}

	/// <summary>
	/// Stores a new choice and broadcasts it, so any map already on screen restyles without a
	/// reload.
	/// </summary>
	/// <param name="source">The rider's choice. Refused if it cannot draw a map.</param>
	/// <param name="cancellationToken">Cancels the write.</param>
	/// <exception cref="ArgumentException"><paramref name="source"/> does not describe a usable map (see <c>MapSource.Normalised</c>).</exception>
	public async Task SetAsync(MapSource source, CancellationToken cancellationToken = default)
	{
		MapSource usable = source.Normalised()
			?? throw new ArgumentException("That map source cannot draw a map.", nameof(source));

		_loaded = true;
		_written = true;

		if (_chosen == usable)
		{
			return;
		}

		_chosen = usable;

		// In memory, then the event, then the store — the same ordering as RouteStyleState. The map
		// is what the rider is waiting to see, and it must not queue behind a device write.
		Changed?.Invoke();
		await _settings.SetAsync(MapSource.StorageKey, usable.Encode(), cancellationToken);
	}

	/// <summary>
	/// Goes back to the shipped default. Removes the key rather than storing the current default —
	/// see <see cref="IDeviceSettings.RemoveAsync"/>, which is the same reasoning every other
	/// device setting uses.
	/// </summary>
	/// <param name="cancellationToken">Cancels the removal.</param>
	public async Task ResetAsync(CancellationToken cancellationToken = default)
	{
		_loaded = true;
		_written = true;

		if (_chosen == MapSource.Default)
		{
			return;
		}

		_chosen = MapSource.Default;

		Changed?.Invoke();
		await _settings.RemoveAsync(MapSource.StorageKey, cancellationToken);
	}
}
