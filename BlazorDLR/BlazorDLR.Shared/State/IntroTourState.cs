using System.Globalization;

using BlazorDLR.Shared.Services;

namespace BlazorDLR.Shared.State;

/// <summary>
/// Whether this device has been shown the introduction (§18.6).
/// <para>
/// <strong>Device-local, and never the account's.</strong> A rider who installs the app on a
/// second phone is a rider looking at an unfamiliar screen on an unfamiliar device, so the
/// introduction is worth showing again there. It goes in <see cref="IDeviceSettings"/> like
/// every other preference that describes the machine rather than the person, which also means
/// it costs no round trip on launch and works on a phone with no signal — the two things a
/// first-run gate must not depend on.
/// </para>
/// <para>
/// <strong>The stored value is a deck version, not a flag.</strong> See
/// <see cref="IntroTour.Version"/>: a materially rewritten introduction can then be shown once
/// to devices that had already seen the old one, and a device that has seen the current one is
/// never interrupted again.
/// </para>
/// <para>
/// <strong>Nothing stored, or something unreadable, both mean "not seen yet".</strong> That is
/// the safe direction: the cost of being wrong is one skippable screen, where the other way
/// round is a rider who is never told what the app is. It is also what a browser with site data
/// blocked and a prerender pass with no device at all answer, and neither of those is a state
/// worth a branch of its own.
/// </para>
/// </summary>
public sealed class IntroTourState
{
	/// <summary>
	/// The <see cref="IDeviceSettings"/> key. Namespaced like <c>dlr.current-ride</c> and
	/// <c>dlr.route-style</c>.
	/// <para>
	/// The value is the <see cref="IntroTour.Version"/> that was finished, written as an
	/// invariant integer. It carries no leading <c>1|</c> format marker because there is one
	/// field: a value this cannot parse reads back as "not seen", which is the same answer a
	/// device that has never run the app gets.
	/// </para>
	/// </summary>
	public const string StorageKey = "dlr.intro-seen";

	private readonly IDeviceSettings _settings;
	private int _seenVersion;
	private bool _loaded;

	/// <summary>Creates the state over a host's device store.</summary>
	/// <param name="settings">Where the seen-version is persisted, so it outlives the process.</param>
	public IntroTourState(IDeviceSettings settings) => _settings = settings;

	/// <summary>Whether the device store has been read yet.</summary>
	public bool IsLoaded => _loaded;

	/// <summary>
	/// Whether this device has finished the introduction that is currently shipping. <c>false</c>
	/// until <see cref="LoadAsync"/> has run — see the type's remarks on why that is the safe way
	/// round.
	/// </summary>
	public bool HasSeenCurrent => _seenVersion >= IntroTour.Version;

	/// <summary>
	/// Reads the persisted version. Idempotent, so the layout can call it on every launch without
	/// coordinating with anything else.
	/// <para>
	/// Callers must run this <em>after</em> first render on the web: the browser store is reached
	/// through JS interop, which does not exist during the prerender pass.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancels the read.</param>
	public async Task LoadAsync(CancellationToken cancellationToken = default)
	{
		if (_loaded) return;

		// Set before the read so a layout that renders twice does not start two round trips.
		_loaded = true;

		string? stored = await _settings.GetAsync(StorageKey, cancellationToken);
		_seenVersion = int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
			? parsed
			: 0;
	}

	/// <summary>
	/// Whether the introduction should open by itself on this launch: reads the device if it has
	/// not been read, then answers.
	/// <para>
	/// One call rather than the caller doing <see cref="LoadAsync"/> and then
	/// <see cref="HasSeenCurrent"/>, because the launch path is the one place where getting that
	/// order wrong shows a rider the deck they have already dismissed.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancels the read.</param>
	public async Task<bool> ShouldShowAsync(CancellationToken cancellationToken = default)
	{
		await LoadAsync(cancellationToken);
		return !HasSeenCurrent;
	}

	/// <summary>
	/// Records that this device has been through the introduction, so the next launch goes
	/// straight into the app.
	/// <para>
	/// Written when the deck is <em>skipped</em> as well as when it is finished. A skip is a
	/// rider saying they do not want this screen, and asking again on the next launch answers
	/// them with the same screen.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancels the write.</param>
	public async Task MarkSeenAsync(CancellationToken cancellationToken = default)
	{
		_loaded = true;

		if (_seenVersion == IntroTour.Version) return;

		_seenVersion = IntroTour.Version;
		await _settings.SetAsync(StorageKey, IntroTour.Version.ToString(CultureInfo.InvariantCulture), cancellationToken);
	}

	/// <summary>
	/// Forgets that the introduction was ever shown, so it opens by itself again on the next
	/// launch. What a "show this next time I open the app" control writes.
	/// <para>
	/// Removes the key rather than storing a zero — see <see cref="IDeviceSettings.RemoveAsync"/>.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancels the removal.</param>
	public async Task ResetAsync(CancellationToken cancellationToken = default)
	{
		_loaded = true;
		_seenVersion = 0;
		await _settings.RemoveAsync(StorageKey, cancellationToken);
	}
}
