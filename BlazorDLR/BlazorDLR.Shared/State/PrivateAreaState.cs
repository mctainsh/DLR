using BlazorDLR.Shared.Services;

namespace BlazorDLR.Shared.State;

/// <summary>
/// The <see cref="PrivateArea"/> in force on this device, and the one gate that decides whether a
/// fix may be broadcast — and, at save time, whether a recorded one may be uploaded (§10.1, §18.6).
/// <para>
/// Same shape as <see cref="RouteStyleState"/> — read once through <see cref="IDeviceSettings"/>,
/// held in memory, <see cref="Changed"/> on every write — because the caller with the tightest
/// budget is the same one: a recorder asking "may I send this?" once a second must not make a
/// JS interop round trip to find out.
/// </para>
/// <para>
/// <strong><see cref="HidesLocation(double, double)"/> answers <c>true</c> until the setting has
/// been read.</strong> Before <see cref="LoadAsync"/> resolves, this object genuinely does not
/// know whether the rider drew a circle around their house, and the two ways of being wrong are
/// not equivalent: a few suppressed fixes at startup cost a rider a moment of being unplaced on
/// somebody's map, while a few published ones cost them the thing the feature exists to protect.
/// Hosts call <see cref="LoadAsync"/> at startup and the settings screen calls it again; it is
/// idempotent, so whoever arrives first pays for the read.
/// </para>
/// </summary>
public sealed class PrivateAreaState
{
	/// <summary>
	/// The <see cref="IDeviceSettings"/> key. Namespaced like <c>dlr.current-ride</c> and
	/// <c>dlr.route-style</c>, and versioned inside the value rather than in the key.
	/// </summary>
	public const string StorageKey = "dlr.private-area";

	private readonly IDeviceSettings _settings;
	private PrivateArea? _area;
	private bool _loaded;

	/// <summary>Creates the state over a host's device store.</summary>
	/// <param name="settings">Where the encoded area is persisted. Never the server.</param>
	public PrivateAreaState(IDeviceSettings settings) => _settings = settings;

	/// <summary>Fired after <see cref="LoadAsync"/> first resolves and after every <see cref="SetAsync"/> or <see cref="ClearAsync"/>.</summary>
	public event Action? Changed;

	/// <summary>The area this device holds, or <c>null</c> when there is none. <c>null</c> until <see cref="LoadAsync"/> has run.</summary>
	public PrivateArea? Area => _area;

	/// <summary>Whether an area is set — what the settings screen offers "remove" against.</summary>
	public bool IsSet => _area is not null;

	/// <summary>
	/// Whether the device store has been read yet. A recorder should await <see cref="LoadAsync"/>
	/// before its first fix rather than lean on this, but a caller that wants to assert the
	/// ordering can.
	/// </summary>
	public bool IsLoaded => _loaded;

	/// <summary>
	/// Whether a position must not be sent — the question the publisher asks about every fix, and
	/// the only place that decision is made.
	/// <para>
	/// The recorder does not ask this: it keeps the fix on the device and
	/// <see cref="Services.TrackRecording.WithoutPrivateArea"/> runs against <see cref="Area"/> on
	/// the one path that takes a track off the phone. See the remarks on <see cref="PrivateArea"/>.
	/// </para>
	/// <para>
	/// Answers <c>true</c> before the setting has been read. See the type's remarks: "I do not yet
	/// know" and "there is no area" are different states, and only one of them is safe to publish
	/// from.
	/// </para>
	/// </summary>
	/// <param name="latitudeDeg">The fix's latitude in decimal degrees.</param>
	/// <param name="longitudeDeg">The fix's longitude in decimal degrees.</param>
	public bool HidesLocation(double latitudeDeg, double longitudeDeg) =>
		!_loaded || _area?.Contains(latitudeDeg, longitudeDeg) == true;

	/// <summary>Whether a fix from the platform falls inside the area.</summary>
	/// <param name="fix">The fix as <see cref="ILocationProvider"/> reported it.</param>
	public bool HidesLocation(LocationFix fix) => HidesLocation(fix.Latitude, fix.Longitude);

	/// <summary>
	/// Reads the persisted area. Idempotent — the host's startup and the settings screen both call
	/// it without coordinating.
	/// <para>
	/// Callers must run this <em>after</em> first render on the web: the browser store is reached
	/// through JS interop, which is not available during the prerender pass.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancels the read.</param>
	public async Task LoadAsync(CancellationToken cancellationToken = default)
	{
		if (_loaded)
		{
			return;
		}

		_area = PrivateArea.Decode(await _settings.GetAsync(StorageKey, cancellationToken));

		// Set after the read, unlike RouteStyleState: until this flips, HidesLocation suppresses.
		// A second caller arriving mid-read therefore starts a second round trip and gets the same
		// answer — a wasted localStorage read is a cheaper mistake than a window where the gate is
		// open because a flag was set before the value behind it arrived.
		_loaded = true;

		Changed?.Invoke();
	}

	/// <summary>Places or moves the area on this device, and tells whatever is drawing it.</summary>
	/// <param name="area">The new area. Normalised before it is stored, so a radius typed outside the offered range is clamped rather than rejected.</param>
	/// <param name="cancellationToken">Cancels the write.</param>
	/// <exception cref="ArgumentException">The centre is not a point on the earth.</exception>
	public async Task SetAsync(PrivateArea area, CancellationToken cancellationToken = default)
	{
		PrivateArea safe = area.Normalised()
			?? throw new ArgumentException("A private area needs a centre on the earth.", nameof(area));

		_area = safe;
		_loaded = true;

		// In memory, then the event, then the store — the same ordering as RouteStyleState, and
		// here it also closes the gate before the write it is waiting on can be observed.
		Changed?.Invoke();
		await _settings.SetAsync(StorageKey, safe.Encode(), cancellationToken);
	}

	/// <summary>
	/// Forgets the area, so this device shares from everywhere again. Removes the key rather than
	/// storing a "none" marker — see <see cref="IDeviceSettings.RemoveAsync"/>.
	/// </summary>
	/// <param name="cancellationToken">Cancels the removal.</param>
	public async Task ClearAsync(CancellationToken cancellationToken = default)
	{
		_area = null;
		_loaded = true;

		Changed?.Invoke();
		await _settings.RemoveAsync(StorageKey, cancellationToken);
	}
}
