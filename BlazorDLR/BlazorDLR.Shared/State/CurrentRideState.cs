using BlazorDLR.Shared.Services;

namespace BlazorDLR.Shared.State;

/// <summary>
/// Which group ride this device is on - the one the nav rail's globe goes back to (§18.6).
/// <para>
/// <strong>Why the rail needs one at all.</strong> A rider mid-ride leaves the live map for the
/// info page, the marker composer or the ride thread, and every one of those is a trip they make
/// repeatedly at the side of a road. Without this, the way back is Group rides → find the ride in
/// the list → open it, which is three taps and a read for the destination they were on ten seconds
/// ago. With it the globe is one tap, from anywhere in the app.
/// </para>
/// <para>
/// <strong>It survives a restart, which is the point.</strong> A phone that ran out of battery, an
/// app the OS reclaimed mid-ride, a WebView reload - every one of those puts the rider back on the
/// home screen with the ride still running. The id is persisted through
/// <see cref="IDeviceSettings"/> like every other device-local preference, so the globe still leads
/// back to the ride the next time the app opens.
/// </para>
/// <para>
/// Same shape as <see cref="RouteStyleState"/> and <see cref="PrivateAreaState"/>: read once, held
/// in memory, <see cref="Changed"/> on every write. The rail renders on every page, so it must
/// never be reading <c>localStorage</c> to decide where one of its anchors points.
/// </para>
/// <para>
/// <strong>Nothing is stored when nothing has been opened</strong>, and that state has an answer of
/// its own rather than a missing item on the rail: <see cref="Href"/> falls back to the group rides
/// list, which is where a rider with no ride goes to pick one.
/// </para>
/// <para>
/// <strong>Why the routes read <c>group-rides/{what}/{id}</c> and not <c>group-rides/{id}/{what}</c>.</strong>
/// <c>NavLink</c> marks itself active on a <em>prefix</em> match, and with the id first the live
/// map's href is a prefix of the traveller list's and the thread's - so the globe lit up on all
/// three, and the rail could never say which of them the rider was actually on. Putting the verb
/// in front makes the three siblings rather than an ancestor and its descendants, and exactly one
/// of them matches at a time. A screen added to the rail has to keep that shape.
/// </para>
/// </summary>
public sealed class CurrentRideState
{
	/// <summary>
	/// The <see cref="IDeviceSettings"/> key. Namespaced like <c>dlr.route-style</c> and
	/// <c>dlr.live-map-view</c>.
	/// <para>
	/// The value is a bare ride id, with none of the leading <c>1|</c> version marker the
	/// multi-field settings carry: there is one field, and a format that ever changes would fail
	/// <see cref="Guid.TryParse(string, out Guid)"/> and read back as "no ride", which is the same
	/// answer a device that has never opened one gets.
	/// </para>
	/// </summary>
	public const string StorageKey = "dlr.current-ride";

	/// <summary>
	/// Where the globe points on a device that has not opened a ride yet: the list, so the rider
	/// picks one. Relative, like every other href in <c>NavMenu</c> - the hosts all serve the app
	/// from <c>&lt;base href="/"&gt;</c>.
	/// </summary>
	public const string PickRideHref = "group-rides";

	private readonly IDeviceSettings _settings;
	private Guid? _rideId;
	private bool _loaded;
	private bool _written;

	/// <summary>The device read, held so a second caller on the same render joins it rather than
	/// being told it has already happened. Null until <see cref="LoadAsync"/> starts one.</summary>
	private Task? _reading;

	/// <summary>Creates the state over a host's device store.</summary>
	/// <param name="settings">Where the ride id is persisted, so it outlives the process.</param>
	public CurrentRideState(IDeviceSettings settings) => _settings = settings;

	/// <summary>Fired after <see cref="LoadAsync"/> first resolves and after every <see cref="SetAsync"/> or <see cref="ClearAsync"/>.</summary>
	public event Action? Changed;

	/// <summary>
	/// The ride last opened on this device, or <c>null</c> when there is none. <c>null</c> until
	/// <see cref="LoadAsync"/> has run, which is the same answer and the same destination - so a
	/// rail rendered before the read is not wrong, only less specific.
	/// </summary>
	public Guid? RideId => _rideId;

	/// <summary>Whether the device store has been read yet.</summary>
	public bool IsLoaded => _loaded;

	/// <summary>
	/// Where the rail's globe goes: the remembered ride, or the list when there is nothing to go
	/// back to. One property rather than the caller assembling a route out of <see cref="RideId"/>,
	/// because the two states are one destination - "the ride you are on" - and only this type
	/// knows which of them is in force.
	/// </summary>
	public string Href => _rideId is { } id ? $"{PickRideHref}/live/{id}" : PickRideHref;

	/// <summary>
	/// Where the rail's rider list goes: the members of the ride you are on, or the list when there
	/// is no ride to have members (§18.6).
	/// <para>
	/// Its own property rather than the rail assembling one out of <see cref="Href"/>, which on a
	/// device that has not opened a ride would produce a route with no id in it - something that
	/// matches nothing.
	/// </para>
	/// </summary>
	public string MembersHref => _rideId is { } id ? $"{PickRideHref}/members/{id}" : PickRideHref;

	/// <summary>
	/// Where the rail's thread goes: the conversation on the ride you are on, or the list when there
	/// is no ride to have one (§17).
	/// <para>
	/// The same fallback as <see cref="MembersHref"/>, and for the same reason - a device that has
	/// not opened a ride has no thread to open, so the item leads to the chooser rather than to a
	/// route that matches nothing.
	/// </para>
	/// </summary>
	public string ThreadHref => _rideId is { } id ? $"{PickRideHref}/thread/{id}" : PickRideHref;

	/// <summary>
	/// Reads the persisted ride. Idempotent - the rail calls it on first render and nobody else has
	/// to coordinate with that.
	/// <para>
	/// <strong>A second caller waits for the first one's read.</strong> The rail and
	/// <see cref="LaunchRestore"/> both ask on first render, and returning "already loaded" to the
	/// second while the store had not answered yet handed it <see cref="RideId"/> <c>null</c> - a
	/// launch that concluded this device had never opened an adventure while the answer was still
	/// in flight. So the read is held and awaited rather than the flag being the whole story.
	/// </para>
	/// <para>
	/// Callers must run this <em>after</em> first render on the web: the browser store is reached
	/// through JS interop, which is not available during the prerender pass.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancels the read.</param>
	public Task LoadAsync(CancellationToken cancellationToken = default)
	{
		if (_loaded)
		{
			// Either the read below is still running - join it - or SetAsync/ClearAsync has since
			// decided the answer, which is newer than anything a store could say.
			return _reading ?? Task.CompletedTask;
		}

		// Set before the read so a rail that renders twice does not start two round trips.
		_loaded = true;

		return _reading = ReadAsync(cancellationToken);
	}

	private async Task ReadAsync(CancellationToken cancellationToken)
	{
		Guid? stored = Guid.TryParse(await _settings.GetAsync(StorageKey, cancellationToken), out Guid parsed)
			&& parsed != Guid.Empty
				? parsed
				: null;

		if (_written)
		{
			// A ride was opened - or the memory cleared - while this read was in flight. What is
			// already in memory is newer than what the device held, and applying the older value
			// would point the globe at the ride the rider has just left.
			return;
		}

		_rideId = stored;
		Changed?.Invoke();
	}

	/// <summary>
	/// Remembers a ride as the one this device is on, so the rail leads back to it - including
	/// after a restart.
	/// </summary>
	/// <param name="rideId">The ride that was just opened.</param>
	/// <param name="cancellationToken">Cancels the write.</param>
	/// <exception cref="ArgumentException"><paramref name="rideId"/> is <see cref="Guid.Empty"/>, which is not a ride.</exception>
	public async Task SetAsync(Guid rideId, CancellationToken cancellationToken = default)
	{
		if (rideId == Guid.Empty)
		{
			throw new ArgumentException("A current ride needs a ride id.", nameof(rideId));
		}

		_loaded = true;
		_written = true;

		if (_rideId == rideId)
		{
			// The live map re-supplies its parameters more than once per ride, and this would
			// otherwise be a device write - a JS interop round trip on the web - per render for a
			// value that has not moved.
			return;
		}

		_rideId = rideId;

		// In memory, then the event, then the store - the same ordering as RouteStyleState: the
		// rail is what the rider is waiting to see, and it must not queue behind a JS interop call.
		Changed?.Invoke();
		await _settings.SetAsync(StorageKey, rideId.ToString("N"), cancellationToken);
	}

	/// <summary>
	/// Forgets <paramref name="rideId"/> if it is the one being remembered, and does nothing at all
	/// if it is not.
	/// <para>
	/// What a ride that is gone calls: deleted, or one the rider has been removed from or has left
	/// (§5.2). The globe leading somewhere that answers "no such ride" for the rest of the app's
	/// life is the failure this exists to prevent.
	/// </para>
	/// <para>
	/// <strong>The check is the point.</strong> A ride opening and another failing race each other
	/// on a slow connection - a rider taps the globe, changes their mind and opens a second ride,
	/// and the first one's 404 lands afterwards. Clearing outright there would forget the ride they
	/// are now on because of one they are not.
	/// </para>
	/// <para>
	/// Reads the device first when nothing has been read yet: an app launched straight into a ride
	/// that 404s - a stale notification, a shared link to a deleted ride - has to be able to forget
	/// what the store holds before the rail has got round to loading it.
	/// </para>
	/// </summary>
	/// <param name="rideId">The ride that turned out not to be there.</param>
	/// <param name="cancellationToken">Cancels the read and the removal.</param>
	public async Task ForgetAsync(Guid rideId, CancellationToken cancellationToken = default)
	{
		await LoadAsync(cancellationToken);

		if (_rideId == rideId)
		{
			await ClearAsync(cancellationToken);
		}
	}

	/// <summary>
	/// Forgets the ride, so the globe goes back to meaning "pick one". Removes the key rather than
	/// storing an empty id - see <see cref="IDeviceSettings.RemoveAsync"/>.
	/// </summary>
	/// <param name="cancellationToken">Cancels the removal.</param>
	public async Task ClearAsync(CancellationToken cancellationToken = default)
	{
		_loaded = true;
		_written = true;

		if (_rideId is null)
		{
			return;
		}

		_rideId = null;

		Changed?.Invoke();
		await _settings.RemoveAsync(StorageKey, cancellationToken);
	}
}
