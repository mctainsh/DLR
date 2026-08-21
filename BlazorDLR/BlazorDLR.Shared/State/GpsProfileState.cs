using BlazorDLR.Shared.Services;

namespace BlazorDLR.Shared.State;

/// <summary>
/// The <see cref="AccuracyProfile"/> this device records and publishes at (§4.2, §18.6).
/// <para>
/// Device-local like <see cref="RouteStyleState"/>, and for a reason particular to this one: the
/// profile is a battery decision, and the battery belongs to the phone, not to the account. A
/// rider with a hard-wired bike mount and a rider on a three-day tour want different answers on
/// the same day, and neither is wrong. That is exactly the argument
/// <see cref="PrivateAreaState"/> fails — where somebody lives is the same on every handset they
/// own, which is why that one moved to the account (§10.1) and this one did not.
/// </para>
/// <para>
/// Read once, held in memory, <see cref="Changed"/> on every write — the recorder asks for the
/// profile every time it starts a watch, and on the phone that must not be a round trip to
/// platform storage.
/// </para>
/// </summary>
public sealed class GpsProfileState
{
	/// <summary>The <see cref="IDeviceSettings"/> key. Namespaced like <c>dlr.route-style</c>.</summary>
	public const string StorageKey = "dlr.gps-profile";

	/// <summary>
	/// What a device that has never chosen gets. §4.2's own default, and the one that does not
	/// need explaining to somebody who has not read the settings screen.
	/// </summary>
	public const AccuracyProfile Default = AccuracyProfile.Balanced;

	private readonly IDeviceSettings _settings;
	private AccuracyProfile _profile = Default;
	private bool _loaded;

	/// <summary>Creates the state over a host's device store.</summary>
	/// <param name="settings">Where the chosen profile is persisted.</param>
	public GpsProfileState(IDeviceSettings settings) => _settings = settings;

	/// <summary>Fired after <see cref="LoadAsync"/> first resolves and after every <see cref="SetAsync"/>.</summary>
	public event Action? Changed;

	/// <summary>The profile in force. <see cref="Default"/> until <see cref="LoadAsync"/> has run.</summary>
	public AccuracyProfile Profile => _profile;

	/// <summary>Whether the device store has been read yet.</summary>
	public bool IsLoaded => _loaded;

	/// <summary>How often this profile publishes, for the settings screen's copy.</summary>
	public TimeSpan MinInterval => PositionGate.MinInterval(_profile);

	/// <summary>How far this profile moves before publishing early, for the settings screen's copy.</summary>
	public double MinDistanceM => PositionGate.MinDistanceM(_profile);

	/// <summary>
	/// Reads the persisted profile. Idempotent — the settings screen and the recorder both call it
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

		_profile = Decode(await _settings.GetAsync(StorageKey, cancellationToken));

		Changed?.Invoke();
	}

	/// <summary>
	/// Chooses a profile on this device.
	/// <para>
	/// Takes effect on the next watch rather than mid-stream: the platform's cadence is set when
	/// the location request is made, so the recorder restarts its watch on <see cref="Changed"/>.
	/// </para>
	/// </summary>
	/// <param name="profile">The rider's choice.</param>
	/// <param name="cancellationToken">Cancels the write.</param>
	public async Task SetAsync(AccuracyProfile profile, CancellationToken cancellationToken = default)
	{
		_loaded = true;

		if (_profile == profile)
		{
			return;
		}

		_profile = profile;

		// In memory, then the event, then the store — the same ordering as RouteStyleState. The
		// event is what restarts the watch, and it must not queue behind a platform write.
		Changed?.Invoke();
		await _settings.SetAsync(StorageKey, profile.ToString(), cancellationToken);
	}

	/// <summary>
	/// Reads back what <see cref="SetAsync"/> wrote. Anything unreadable is <see cref="Default"/>,
	/// the same posture <see cref="RouteStyle.Decode"/> takes: a stored value nobody can parse is a
	/// device that has not chosen.
	/// </summary>
	/// <param name="stored">The stored string, or <c>null</c> on a device that has never chosen.</param>
	public static AccuracyProfile Decode(string? stored) =>
		Enum.TryParse(stored, ignoreCase: true, out AccuracyProfile parsed) && Enum.IsDefined(parsed)
			? parsed
			: Default;
}
