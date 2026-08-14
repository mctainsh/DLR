using BlazorDLR.Shared.Services;

namespace BlazorDLR.Shared.State;

/// <summary>
/// The <see cref="RouteStyle"/> in force right now, plus a broadcast so the map repaints the
/// moment somebody moves the width slider on the ride's info page.
/// <para>
/// One instance per app (scoped in each host), the same shape as <see cref="PrivateAreaState"/>:
/// the persisted value is read once through <see cref="IDeviceSettings"/>, kept in memory
/// afterwards, and every change fires <see cref="Changed"/>. The alternative — each map frame
/// reading <c>localStorage</c> — is a JS interop call per repaint on a surface that repaints
/// on every pan.
/// </para>
/// </summary>
public sealed class RouteStyleState
{
	/// <summary>
	/// The <see cref="IDeviceSettings"/> key. Namespaced like <c>dlr.private-area</c>, and
	/// versioned inside the value rather than in the key so an added field does not orphan
	/// what a device already stored.
	/// </summary>
	public const string StorageKey = "dlr.route-style";

	/// <summary>
	/// The <see cref="IDeviceSettings"/> key for per-route colours. Its own key, so a device
	/// that has never pinned a colour carries nothing for it — see <see cref="RouteColourMap"/>.
	/// </summary>
	public const string ColoursStorageKey = "dlr.route-colours";

	/// <summary>
	/// The <see cref="IDeviceSettings"/> key for reversed routes. Its own key for the same reason
	/// <see cref="ColoursStorageKey"/> is — a device that has never reversed anything carries
	/// nothing for it — see <see cref="RouteDirectionMap"/>.
	/// </summary>
	public const string ReversedStorageKey = "dlr.route-reversed";

	private readonly IDeviceSettings _settings;
	private RouteStyle _style = RouteStyle.Default;
	private IReadOnlyDictionary<Guid, string> _routeColours = RouteColourMap.Empty;
	private IReadOnlySet<Guid> _reversedRoutes = RouteDirectionMap.Empty;
	private bool _loaded;

	/// <summary>Creates the state over a host's device store.</summary>
	/// <param name="settings">Where the encoded style is persisted.</param>
	public RouteStyleState(IDeviceSettings settings) => _settings = settings;

	/// <summary>Fired after <see cref="LoadAsync"/> first resolves and after every <see cref="SetAsync"/> or <see cref="ResetAsync"/>.</summary>
	public event Action? Changed;

	/// <summary>How routes are drawn on this device. <see cref="RouteStyle.Default"/> until <see cref="LoadAsync"/> has run.</summary>
	public RouteStyle Style => _style;

	/// <summary>Colours pinned to individual tracks, keyed on track id (§5.4). Empty until <see cref="LoadAsync"/> has run.</summary>
	public IReadOnlyDictionary<Guid, string> RouteColours => _routeColours;

	/// <summary>Tracks this rider has reversed (§5.4). Empty until <see cref="LoadAsync"/> has run.</summary>
	public IReadOnlySet<Guid> ReversedRoutes => _reversedRoutes;

	/// <summary>Whether this device has changed anything at all, which is what makes "reset" worth offering.</summary>
	public bool IsCustomised =>
		_style != RouteStyle.Default || _routeColours.Count > 0 || _reversedRoutes.Count > 0;

	/// <summary>
	/// The colour a route is actually drawn in, resolving the three answers most-specific first:
	/// <list type="number">
	///   <item>a colour pinned to this track, if the rider has set one;</item>
	///   <item><see cref="RouteStyle.FillColour"/>, when they have asked for one colour across every route;</item>
	///   <item><paramref name="paletteColour"/> — what <see cref="RoutePalette"/> assigned by position (§5.4).</item>
	/// </list>
	/// <para>
	/// Most-specific-wins is the only ordering that makes both controls usable: picking a colour
	/// for one route while "same colour for every route" is on has to change that route, or the
	/// per-route picker would silently do nothing.
	/// </para>
	/// <para>
	/// One method rather than a chain each caller assembles, because the map overlay and the
	/// swatch beside the route's name both ask — and a swatch that claims a colour the line is
	/// not drawn in is worse than no swatch.
	/// </para>
	/// </summary>
	/// <param name="trackId">The route's track, or <c>null</c> for a line that is not a saved track (the editor's working copy).</param>
	/// <param name="paletteColour">The colour assigned by position, normally from <see cref="RoutePalette.At"/>.</param>
	public string ColourFor(Guid? trackId, string paletteColour) =>
		trackId is { } id && _routeColours.TryGetValue(id, out string? pinned)
			? pinned
			: _style.EffectiveFill(paletteColour);

	/// <summary>Whether this track carries a colour of its own, which is what makes "back to automatic" worth offering on its row.</summary>
	/// <param name="trackId">The route's track.</param>
	public bool HasRouteColour(Guid trackId) => _routeColours.ContainsKey(trackId);

	/// <summary>
	/// Whether this route is drawn — and measured along — back to front (§5.4).
	/// <para>
	/// Everything that reads a route's <em>order</em> has to ask: the chevrons the overlay spaces
	/// along the line, and the gap list, which defines the leader as whoever is furthest along it.
	/// A screen that reversed only one of the two would draw arrows disagreeing with the ranking
	/// beside them, which is worse than not offering the choice.
	/// </para>
	/// </summary>
	/// <param name="trackId">The route's track, or <c>null</c> for a line that is not a saved track (the editor's working copy), which is never reversed.</param>
	public bool IsReversed(Guid? trackId) => trackId is { } id && _reversedRoutes.Contains(id);

	/// <summary>
	/// Turns one route back to front, or puts it the way its GPX was recorded.
	/// <para>
	/// Nothing is rewritten and nothing is sent anywhere: this records a direction to <em>read</em>
	/// the stored points in. The track keeps its own order, every other ride it is attached to is
	/// untouched, and an export is still the file that was imported.
	/// </para>
	/// </summary>
	/// <param name="trackId">The route's track.</param>
	/// <param name="reversed">True to draw and measure it end to start.</param>
	/// <param name="cancellationToken">Cancels the write.</param>
	public Task SetReversedAsync(Guid trackId, bool reversed, CancellationToken cancellationToken = default)
	{
		if (_reversedRoutes.Contains(trackId) == reversed)
		{
			// Already the way round it is being asked for. Writing anyway would fire Changed and
			// cost every map on screen a full repaint to draw the identical frame.
			return Task.CompletedTask;
		}

		HashSet<Guid> updated = [.. _reversedRoutes];

		if (reversed)
		{
			updated.Add(trackId);
		}
		else
		{
			updated.Remove(trackId);
		}

		return WriteReversedAsync(updated, cancellationToken);
	}

	/// <summary>Flips one route's direction — what the button on the ride's info page calls.</summary>
	/// <param name="trackId">The route's track.</param>
	/// <param name="cancellationToken">Cancels the write.</param>
	public Task ToggleReversedAsync(Guid trackId, CancellationToken cancellationToken = default) =>
		SetReversedAsync(trackId, !_reversedRoutes.Contains(trackId), cancellationToken);

	private async Task WriteReversedAsync(IReadOnlySet<Guid> reversed, CancellationToken cancellationToken)
	{
		_reversedRoutes = reversed;
		_loaded = true;

		// In memory, then the event, then the store — same ordering and same reason as SetAsync.
		Changed?.Invoke();

		if (reversed.Count == 0)
		{
			// Nothing left to remember. Removing beats storing an empty set: it leaves no key
			// behind on a device that ends up back where it started.
			await _settings.RemoveAsync(ReversedStorageKey, cancellationToken);
			return;
		}

		await _settings.SetAsync(ReversedStorageKey, RouteDirectionMap.Encode(reversed), cancellationToken);
	}

	/// <summary>
	/// Reads the persisted style. Idempotent — the map overlay and the settings panel both
	/// call it without coordinating, and whichever renders first pays for the read.
	/// <para>
	/// Callers must run this <em>after</em> first render on the web: the browser store is
	/// reached through JS interop, which is not available during the prerender pass.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancels the read.</param>
	public async Task LoadAsync(CancellationToken cancellationToken = default)
	{
		if (_loaded)
		{
			return;
		}

		// Set before the read so a second caller arriving while this one awaits does not
		// start a second round trip. A failed read leaves the defaults, which is the answer
		// a device with nothing stored gets anyway.
		_loaded = true;

		_style = RouteStyle.Decode(await _settings.GetAsync(StorageKey, cancellationToken));
		_routeColours = RouteColourMap.Decode(await _settings.GetAsync(ColoursStorageKey, cancellationToken));
		_reversedRoutes = RouteDirectionMap.Decode(await _settings.GetAsync(ReversedStorageKey, cancellationToken));

		// One event for all three reads: the canvas repaints from whatever is in memory when it
		// runs, and firing per key would cost a full repaint each to show the same frame.
		Changed?.Invoke();
	}

	/// <summary>
	/// Pins a colour to one track, which beats both the palette and any "same colour for every
	/// route" choice for that route alone.
	/// </summary>
	/// <param name="trackId">The route's track.</param>
	/// <param name="colour">A <c>#rrggbb</c> colour. Anything else is ignored rather than stored — see <see cref="RouteStyle.NormaliseColour"/>.</param>
	/// <param name="cancellationToken">Cancels the write.</param>
	public Task SetRouteColourAsync(Guid trackId, string colour, CancellationToken cancellationToken = default)
	{
		string safe = RouteStyle.NormaliseColour(colour, "");
		if (safe.Length == 0)
		{
			// A colour the canvas would silently redraw as its fallback blue. Storing it would
			// leave the swatch and the line disagreeing, which is the one thing this must not do.
			return Task.CompletedTask;
		}

		Dictionary<Guid, string> updated = new(_routeColours) { [trackId] = safe };
		return WriteColoursAsync(updated, cancellationToken);
	}

	/// <summary>
	/// Drops one track's pinned colour, so it goes back to the palette (or to the all-routes
	/// colour, if one is set).
	/// </summary>
	/// <param name="trackId">The route's track.</param>
	/// <param name="cancellationToken">Cancels the write.</param>
	public Task ClearRouteColourAsync(Guid trackId, CancellationToken cancellationToken = default)
	{
		if (!_routeColours.ContainsKey(trackId))
		{
			return Task.CompletedTask;
		}

		Dictionary<Guid, string> updated = new(_routeColours);
		updated.Remove(trackId);
		return WriteColoursAsync(updated, cancellationToken);
	}

	private async Task WriteColoursAsync(IReadOnlyDictionary<Guid, string> colours, CancellationToken cancellationToken)
	{
		_routeColours = colours;
		_loaded = true;

		// In memory, then the event, then the store — same ordering and same reason as SetAsync.
		Changed?.Invoke();

		if (colours.Count == 0)
		{
			// Nothing left to remember. Removing beats storing an empty map: it leaves no key
			// behind on a device that ends up back where it started.
			await _settings.RemoveAsync(ColoursStorageKey, cancellationToken);
			return;
		}

		await _settings.SetAsync(ColoursStorageKey, RouteColourMap.Encode(colours), cancellationToken);
	}

	/// <summary>Persists a new style on this device and broadcasts it to whatever is drawing.</summary>
	/// <param name="style">The new style. Normalised before it is stored, so out-of-range input from a control is clamped rather than rejected.</param>
	/// <param name="cancellationToken">Cancels the write.</param>
	public async Task SetAsync(RouteStyle style, CancellationToken cancellationToken = default)
	{
		_style = style.Normalised();
		_loaded = true;

		// In memory first, then the event, then the store: the repaint is what the user is
		// waiting to see, and it must not queue behind a JS interop round trip.
		Changed?.Invoke();
		await _settings.SetAsync(StorageKey, _style.Encode(), cancellationToken);
	}

	/// <summary>
	/// Forgets everything this device has chosen — the style, every pinned route colour and every
	/// reversed route — and goes back to <see cref="RouteStyle.Default"/>.
	/// Removes the keys rather than storing today's defaults — see <see cref="IDeviceSettings.RemoveAsync"/>.
	/// </summary>
	/// <param name="cancellationToken">Cancels the removal.</param>
	public async Task ResetAsync(CancellationToken cancellationToken = default)
	{
		_style = RouteStyle.Default;
		_routeColours = RouteColourMap.Empty;
		_reversedRoutes = RouteDirectionMap.Empty;
		_loaded = true;

		Changed?.Invoke();
		await _settings.RemoveAsync(StorageKey, cancellationToken);
		await _settings.RemoveAsync(ColoursStorageKey, cancellationToken);
		await _settings.RemoveAsync(ReversedStorageKey, cancellationToken);
	}
}
