using BlazorDLR.Shared.Services;

namespace BlazorDLR.Shared.State;

/// <summary>
/// The <see cref="LocationUpdateRate"/> this device publishes at (§4.2, §18.6).
/// <para>
/// Device-local like <see cref="RouteStyleState"/>, and for a reason particular to this one: the
/// rate is a battery decision, and the battery belongs to the phone, not to the account. A rider
/// with a hard-wired bike mount and a rider on a three-day tour want different answers on the same
/// day, and neither is wrong. That is exactly the argument <see cref="PrivateAreaState"/> fails -
/// where somebody lives is the same on every handset they own, which is why that one moved to the
/// account (§10.1) and this one did not.
/// </para>
/// <para>
/// Read once, held in memory, <see cref="Changed"/> on every write - the broadcaster asks for the
/// rate every time it starts a watch, and on the phone that must not be a round trip to platform
/// storage.
/// </para>
/// <para>
/// <strong>One key, three numbers.</strong> Stored as one string rather than three so a half-written
/// setting cannot exist: a device store is not transactional, and a maximum that landed without the
/// minimum beside it would be a rate the app never offered.
/// </para>
/// </summary>
public sealed class LocationUpdateRateState
{
	/// <summary>
	/// The <see cref="IDeviceSettings"/> key. Unchanged from when this held one of three profile
	/// names, so a rider's existing choice is still there to be carried across - see
	/// <see cref="LocationUpdateRate.Decode"/>.
	/// </summary>
	public const string StorageKey = "dlr.gps-profile";

	private readonly IDeviceSettings _settings;
	private LocationUpdateRate _rate = LocationUpdateRate.Default;
	private bool _loaded;

	/// <summary>Creates the state over a host's device store.</summary>
	/// <param name="settings">Where the chosen rate is persisted.</param>
	public LocationUpdateRateState(IDeviceSettings settings) => _settings = settings;

	/// <summary>Fired after <see cref="LoadAsync"/> first resolves and after every <see cref="SetAsync"/>.</summary>
	public event Action? Changed;

	/// <summary>
	/// The rate in force. <see cref="LocationUpdateRate.Default"/> until <see cref="LoadAsync"/>
	/// has run.
	/// </summary>
	public LocationUpdateRate Rate => _rate;

	/// <summary>Whether the device store has been read yet.</summary>
	public bool IsLoaded => _loaded;

	/// <summary>
	/// Reads the persisted rate. Idempotent - the settings screen and the broadcaster both call it
	/// without coordinating.
	/// <para>
	/// Callers must run this <em>after</em> first render on the web: the browser store is behind JS
	/// interop, which does not exist during the prerender pass. The web publishes nothing (§18.6),
	/// so there it only ever feeds the settings screen's copy.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancels the read.</param>
	public async Task LoadAsync(CancellationToken cancellationToken = default)
	{
		if (_loaded)
		{
			return;
		}

		// Set before the read so two callers do not start two round trips. A failed read leaves
		// the default, which is what a device with nothing stored answers anyway.
		_loaded = true;

		_rate = LocationUpdateRate.Decode(await _settings.GetAsync(StorageKey, cancellationToken));

		Changed?.Invoke();
	}

	/// <summary>
	/// Chooses a rate on this device.
	/// <para>
	/// Takes effect on the next watch rather than mid-stream: the platform's request is made when
	/// the watch starts, so the broadcaster restarts its watch on <see cref="Changed"/>.
	/// </para>
	/// </summary>
	/// <param name="rate">The rider's choice. Already normalised by its own constructor.</param>
	/// <param name="cancellationToken">Cancels the write.</param>
	public async Task SetAsync(LocationUpdateRate rate, CancellationToken cancellationToken = default)
	{
		_loaded = true;

		if (_rate == rate)
		{
			return;
		}

		_rate = rate;

		// In memory, then the event, then the store - the same ordering as RouteStyleState. The
		// event is what restarts the watch, and it must not queue behind a platform write.
		Changed?.Invoke();
		await _settings.SetAsync(StorageKey, rate.Encode(), cancellationToken);
	}
}
