namespace BlazorDLR.Shared.Services;

/// <summary>
/// Where this device keeps a copy of what the server told it, so a screen still has something
/// to draw when the server cannot be reached (§4.4, §7.9).
/// <para>
/// <strong>Not <see cref="IDeviceSettings"/>, and the difference is size.</strong> That one is
/// for preferences — a theme, a line colour, a camera — and it is backed by
/// <c>SharedPreferences</c> / <c>NSUserDefaults</c> / <c>localStorage</c>, all of which hold
/// every value they have ever been given in memory for the life of the process. A ride's
/// planned routes are encoded polylines running to tens of kilobytes each (§15.5), and putting
/// those in the preference store would make every unrelated preference read pay for them. This
/// seam is files.
/// </para>
/// <para>
/// <strong>Mobile only, on purpose.</strong> §18.6 keeps offline a property of the thing in the
/// rider's pocket: the phone binds a file-backed store, and both browser hosts bind one that
/// answers "nothing stored" to every read and drops every write. Shared code does not branch on
/// which host it is running in — it asks, gets <c>null</c>, and goes to the network, which is
/// what a browser was going to do anyway.
/// </para>
/// <para>
/// <strong>Reads and writes never throw.</strong> Same posture as
/// <see cref="IDeviceSettings"/>: a full disk, a sandbox that has moved underneath the app and
/// a first run are one answer to a caller — <c>null</c>, meaning "you have no copy". A cache
/// that cannot be written is not worth failing a live map over.
/// </para>
/// </summary>
public interface IOfflineStore
{
	/// <summary>
	/// Whether this host keeps anything at all. False on the browser hosts, where every read
	/// answers <c>null</c> and every write is dropped (§18.6).
	/// <para>
	/// Callers do not need to check it to be correct — the no-op store is safe to use — but a
	/// screen that wants to say "showing your last copy" needs to know whether this device is
	/// the kind that has one.
	/// </para>
	/// </summary>
	bool IsSupported { get; }

	/// <summary>
	/// Reads back what <see cref="WriteAsync"/> stored, or <c>null</c> when this device holds
	/// nothing under that name.
	/// </summary>
	/// <param name="name">
	/// The entry's name. A short slug — letters, digits and hyphens — namespaced by what wrote
	/// it, in the style of <c>ride-3f2a…</c>. Anything else is refused rather than sanitised,
	/// so a name can never climb out of the app's own directory.
	/// </param>
	/// <param name="cancellationToken">Cancels the read.</param>
	ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken = default);

	/// <summary>
	/// Stores <paramref name="content"/> so the next launch on this device reads it back.
	/// </summary>
	/// <param name="name">The entry's name, as <see cref="ReadAsync"/> describes it.</param>
	/// <param name="content">The encoded payload. Callers encode; this seam never inspects it.</param>
	/// <param name="cancellationToken">Cancels the write.</param>
	ValueTask WriteAsync(string name, string content, CancellationToken cancellationToken = default);

	/// <summary>
	/// Forgets an entry, so the next read answers <c>null</c>. What a ride that has been deleted
	/// or that this rider has left calls — see <c>CurrentRideState.ForgetAsync</c>, which decides
	/// the same thing about the rail's globe.
	/// </summary>
	/// <param name="name">The entry's name.</param>
	/// <param name="cancellationToken">Cancels the removal.</param>
	ValueTask RemoveAsync(string name, CancellationToken cancellationToken = default);
}
