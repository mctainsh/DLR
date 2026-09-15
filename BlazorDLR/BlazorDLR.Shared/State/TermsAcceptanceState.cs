using System.Globalization;

using BlazorDLR.Shared.Legal;
using BlazorDLR.Shared.Services;

namespace BlazorDLR.Shared.State;

/// <summary>
/// Whether this device has agreed to the terms that are currently shipping (§10.2).
/// <para>
/// <strong>Device-local, like <see cref="IntroTourState"/> beside it.</strong> The gate has to
/// stand in front of <em>registering</em>, which is before there is an account to hang an
/// acceptance off, and it has to work on a first run with no signal. Both rule out asking the
/// server. The trade-off accepted is that a reinstall asks again, which costs a returning rider
/// one tick and is the safe direction to be wrong in.
/// </para>
/// <para>
/// <strong>The stored value is a version, not a flag</strong>, so
/// <see cref="TermsOfUse.Version"/> can be bumped to re-ask a device that agreed to an earlier
/// edition. Nothing stored, or something unreadable, both mean "has not agreed": a browser with
/// site data blocked and a prerender pass with no device store are the same answer as a phone out
/// of the box, and none of them is a state in which somebody should be taken for having agreed.
/// </para>
/// </summary>
public sealed class TermsAcceptanceState
{
	/// <summary>
	/// The <see cref="IDeviceSettings"/> key. Namespaced like <c>dlr.intro-seen</c>, and holding
	/// the <see cref="TermsOfUse.Version"/> that was agreed to as an invariant integer.
	/// </summary>
	public const string StorageKey = "dlr.terms-accepted";

	private readonly IDeviceSettings _settings;
	private int _acceptedVersion;
	private bool _loaded;

	/// <summary>Creates the state over a host's device store.</summary>
	/// <param name="settings">Where the agreed version is persisted.</param>
	public TermsAcceptanceState(IDeviceSettings settings) => _settings = settings;

	/// <summary>
	/// Reads the persisted version. Idempotent, so a screen that renders twice does not start two
	/// round trips.
	/// </summary>
	/// <param name="cancellationToken">Cancels the read.</param>
	private async Task LoadAsync(CancellationToken cancellationToken = default)
	{
		if (_loaded) return;

		_loaded = true;

		string? stored = await _settings.GetAsync(StorageKey, cancellationToken);
		_acceptedVersion = int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
			? parsed
			: 0;
	}

	/// <summary>
	/// Whether the acceptance control has to be put in front of this rider: reads the device if it
	/// has not been read, then answers. The only way in - everything else here is private.
	/// <para>
	/// Callers must run this <em>after</em> first render on the web: the browser store is reached
	/// through JS interop, which does not exist during the prerender pass.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancels the read.</param>
	public async Task<bool> MustAskAsync(CancellationToken cancellationToken = default)
	{
		await LoadAsync(cancellationToken);

		// Not "has agreed", but "has agreed to the edition shipping now" - see the type's remarks
		// on why an unreadable store answers the same way as a phone out of the box.
		return _acceptedVersion < TermsOfUse.Version;
	}

	/// <summary>
	/// Records that this device has agreed to the current edition.
	/// <para>
	/// Called on the way into register and sign-in, after the box is ticked and before the
	/// credentials go anywhere. Writing it only on a <em>successful</em> sign-in would re-show the
	/// box after every mistyped password, which teaches people to tick it without looking.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancels the write.</param>
	public async Task AcceptAsync(CancellationToken cancellationToken = default)
	{
		_loaded = true;

		if (_acceptedVersion == TermsOfUse.Version) return;

		_acceptedVersion = TermsOfUse.Version;
		await _settings.SetAsync(StorageKey, TermsOfUse.Version.ToString(CultureInfo.InvariantCulture), cancellationToken);
	}
}
