using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Identity;

namespace BlazorDLR.Shared.State;

/// <summary>
/// The <see cref="PrivateArea"/> in force for this account, and the one gate that decides whether
/// a fix may be broadcast — and, at save time, whether a recorded one may be uploaded (§10.1,
/// §18.6).
/// <para>
/// <strong>The account is the source of truth; the device store is a cache of it.</strong> This
/// is the reversal of the original design, and the reason is that the original one lost people
/// their private area: it lived only in <see cref="IDeviceSettings"/>, so an app update, a
/// reinstall, a cleared browser store or a new phone wiped it without saying so. A rider who
/// believes they have a circle around their house and does not is in a worse position than one
/// whose server knows where it is. So the circle is read from and written to
/// <c>/api/v1/me/private-area</c>, and mirrored onto the device on the way past.
/// </para>
/// <para>
/// The cache is not an optimisation, it is what keeps the gate answerable with no network. The
/// caller with the tightest budget is a recorder asking "may I send this?" once a second, and it
/// must not make a round trip — of any kind — to find out; and a phone that starts in a tunnel
/// still has to know where not to broadcast from.
/// </para>
/// <para>
/// <strong><see cref="HidesLocation(double, double)"/> answers <c>true</c> until an answer is in
/// hand.</strong> Before <see cref="LoadAsync"/> has either reached the server or found a cached
/// answer on this device, this object genuinely does not know whether the rider drew a circle
/// around their house, and the two ways of being wrong are not equivalent: a few suppressed
/// fixes at startup cost a rider a moment of being unplaced on somebody's map, while a few
/// published ones cost them the thing the feature exists to protect. On a device that has never
/// been told and cannot reach the server the gate simply stays shut — which costs nothing real,
/// because publishing a position needs the same network the read just failed on.
/// </para>
/// <para>
/// Hosts call <see cref="LoadAsync"/> at startup and the settings screen calls it again; it is
/// idempotent once the account has answered, and retries while it has not.
/// </para>
/// </summary>
public sealed class PrivateAreaState
{
	/// <summary>
	/// The <see cref="IDeviceSettings"/> key the account's answer is cached under. Namespaced
	/// like <c>dlr.current-ride</c> and <c>dlr.route-style</c>, and versioned inside the value
	/// rather than in the key.
	/// </summary>
	public const string StorageKey = "dlr.private-area";

	private readonly IDeviceSettings _settings;
	private readonly IApiClient _api;
	private PrivateArea? _area;
	private bool _loaded;
	private bool _fromAccount;
	private bool _cacheRead;
	private string? _syncError;

	/// <summary>Creates the state over the account and this device's cache of it.</summary>
	/// <param name="settings">Where the account's answer is mirrored so the gate survives an offline start.</param>
	/// <param name="api">The account. The circle is stored on the rider's profile (§10.1).</param>
	public PrivateAreaState(IDeviceSettings settings, IApiClient api)
	{
		_settings = settings;
		_api = api;
	}

	/// <summary>Fired after <see cref="LoadAsync"/> resolves and after every <see cref="SetAsync"/> or <see cref="ClearAsync"/>.</summary>
	public event Action? Changed;

	/// <summary>The area in force, or <c>null</c> when there is none. <c>null</c> until <see cref="LoadAsync"/> has run.</summary>
	public PrivateArea? Area => _area;

	/// <summary>Whether an area is set — what the settings screen offers "remove" against.</summary>
	public bool IsSet => _area is not null;

	/// <summary>
	/// Whether there is an answer to give. A recorder should await <see cref="LoadAsync"/> before
	/// its first fix rather than lean on this, but a caller that wants to assert the ordering can.
	/// </summary>
	public bool IsLoaded => _loaded;

	/// <summary>
	/// Whether the answer in hand came from the account rather than from this device's cache.
	/// <para>
	/// The settings screen reads it to say which of the two it is showing: a cached circle is
	/// still the one being enforced here, but it may not be the one the rider's other phone is
	/// enforcing, and a screen that cannot reach the server must not imply that a save landed.
	/// </para>
	/// </summary>
	public bool IsFromAccount => _fromAccount;

	/// <summary>
	/// Why the last attempt to reach the account failed, or <c>null</c> when it did not. Copy for
	/// the settings screen, not a control-flow signal.
	/// </summary>
	public string? SyncError => _syncError;

	/// <summary>
	/// Whether a position must not be sent — the question the publisher asks about every fix, and
	/// the only place that decision is made.
	/// <para>
	/// The recorder does not ask this: it keeps the fix on the device and
	/// <see cref="Services.TrackRecording.WithoutPrivateArea"/> runs against <see cref="Area"/> on
	/// the one path that takes a track off the phone. See the remarks on <see cref="PrivateArea"/>.
	/// </para>
	/// <para>
	/// Answers <c>true</c> before there is an answer. See the type's remarks: "I do not yet know"
	/// and "there is no area" are different states, and only one of them is safe to publish from.
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
	/// Reads the area: this device's cached copy first, then the account, which wins.
	/// <para>
	/// Idempotent once the account has answered — the host's startup and the settings screen both
	/// call it without coordinating. While it has not, every call retries, because the alternative
	/// is a rider stuck behind a shut gate for the rest of the session over one failed request.
	/// </para>
	/// <para>
	/// The cache is read first and on its own, so a phone with no signal has the circle it had
	/// yesterday rather than none: the server read that follows only ever replaces that answer
	/// with a better one.
	/// </para>
	/// <para>
	/// Callers must run this <em>after</em> first render on the web: the browser store is reached
	/// through JS interop, which is not available during the prerender pass.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancels the read.</param>
	public async Task LoadAsync(CancellationToken cancellationToken = default)
	{
		if (_loaded && _fromAccount)
		{
			return;
		}

		if (!_cacheRead)
		{
			_cacheRead = true;

			// TryDecodeCached, not Decode: "the account has no area" and "this device has never
			// been told" both decode to a null circle and must not be treated alike. Only the
			// first of them opens the gate.
			if (PrivateArea.TryDecodeCached(await _settings.GetAsync(StorageKey, cancellationToken), out PrivateArea? cached))
			{
				_area = cached;
				_loaded = true;
			}
		}

		try
		{
			PrivateAreaResponse response = await _api.GetPrivateAreaAsync(cancellationToken);

			_area = response.Area is { } settings ? PrivateArea.From(settings) : null;
			_fromAccount = true;
			_syncError = null;

			// Set after the read, unlike RouteStyleState: until this flips, HidesLocation
			// suppresses. A wasted round trip is a cheaper mistake than a window where the gate is
			// open because a flag was set before the value behind it arrived.
			_loaded = true;

			await CacheAsync(_area, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			// _loaded is left exactly as the cache left it — true when this device holds an
			// answer, false when it does not, and a false gate is a shut gate.
			_syncError = exception.Message;
		}

		Changed?.Invoke();
	}

	/// <summary>
	/// Places or moves the area on the account, and tells whatever is drawing it.
	/// <para>
	/// The order is gate, then this device, then the account. The gate closes before the write it
	/// is waiting on can be observed; the device cache is written before the upload so a failed
	/// upload still leaves this phone protected across a restart; and the upload throws on failure
	/// rather than being swallowed, because "your other devices do not have this yet" is
	/// something the rider has to be told.
	/// </para>
	/// </summary>
	/// <param name="area">The new area. Normalised before it is stored, so a radius typed outside the offered range is clamped rather than rejected.</param>
	/// <param name="cancellationToken">Cancels the write.</param>
	/// <exception cref="ArgumentException">The centre is not a point on the earth.</exception>
	public async Task SetAsync(PrivateArea area, CancellationToken cancellationToken = default)
	{
		PrivateArea safe = area.Normalised()
			?? throw new ArgumentException("A private area needs a centre on the earth.", nameof(area));

		_area = safe;
		_loaded = true;
		_cacheRead = true;

		Changed?.Invoke();

		await CacheAsync(safe, cancellationToken);

		try
		{
			await _api.SetPrivateAreaAsync(safe.ToSettings(), cancellationToken);
			_fromAccount = true;
			_syncError = null;
		}
		catch (Exception exception)
		{
			_fromAccount = false;
			_syncError = exception.Message;
			throw;
		}
		finally
		{
			Changed?.Invoke();
		}
	}

	/// <summary>
	/// Forgets the area, on the account and on this device, so sharing resumes from everywhere.
	/// <para>
	/// The cache is set to <see cref="PrivateArea.NoneMarker"/> rather than removed. Removing the
	/// key would make a rider who deliberately cleared their area indistinguishable from a device
	/// that has never asked the server — and the gate treats those differently on purpose, so the
	/// first of them would find themselves silently unable to share the next time they opened the
	/// app offline.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancels the removal.</param>
	public async Task ClearAsync(CancellationToken cancellationToken = default)
	{
		_area = null;
		_loaded = true;
		_cacheRead = true;

		Changed?.Invoke();

		await CacheAsync(null, cancellationToken);

		try
		{
			await _api.ClearPrivateAreaAsync(cancellationToken);
			_fromAccount = true;
			_syncError = null;
		}
		catch (Exception exception)
		{
			_fromAccount = false;
			_syncError = exception.Message;
			throw;
		}
		finally
		{
			Changed?.Invoke();
		}
	}

	/// <summary>
	/// Mirrors the account's answer onto this device. Never throws — a device store that cannot
	/// be written is a slower next start, not a failed save (<see cref="IDeviceSettings"/>).
	/// </summary>
	private ValueTask CacheAsync(PrivateArea? area, CancellationToken cancellationToken) =>
		_settings.SetAsync(StorageKey, area?.Encode() ?? PrivateArea.EncodeNone(), cancellationToken);
}
