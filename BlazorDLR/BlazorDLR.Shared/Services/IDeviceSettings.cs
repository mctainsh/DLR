namespace BlazorDLR.Shared.Services;

/// <summary>
/// A small key/value store for preferences that belong to <em>this device</em> and never
/// travel to the server (§18.6).
/// <para>
/// <strong>Generic, and deliberately the only one of its kind.</strong> The app used to have a
/// second, single-purpose store beside this one for the colour theme; when that setting was
/// removed its whole seam went with it, and nothing has needed one since. A per-setting
/// interface means a new interface, three host implementations and three DI registrations every
/// time somebody adds a checkbox, and the three implementations only ever differ in which
/// platform API holds the string.
/// </para>
/// <para>
/// <strong>Values are opaque strings.</strong> The backing stores — browser
/// <c>localStorage</c> and MAUI <c>Preferences</c> — are string stores, and keeping the
/// encoding in the caller means the store cannot be wrong about a type it never inspects.
/// A caller that needs structure encodes it (see <see cref="RouteStyle.Encode"/>).
/// </para>
/// <para>
/// <strong>Reads and writes never throw.</strong> A browser with storage blocked, a
/// prerender pass with no storage at all, and a first run with nothing written yet are the
/// same answer to a caller: <c>null</c>, meaning "use your default". A setting that cannot
/// be persisted is not worth failing a screen over.
/// </para>
/// </summary>
public interface IDeviceSettings
{
	/// <summary>Reads a value, or <c>null</c> when the key has never been written on this device.</summary>
	/// <param name="key">The setting's key. Namespaced <c>dlr.*</c> by convention.</param>
	/// <param name="cancellationToken">Cancels the read.</param>
	ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default);

	/// <summary>Persists a value so the next launch on this device reads it back.</summary>
	/// <param name="key">The setting's key.</param>
	/// <param name="value">The encoded value.</param>
	/// <param name="cancellationToken">Cancels the write.</param>
	ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default);

	/// <summary>
	/// Forgets a value, so the next read answers <c>null</c> and the caller falls back to its
	/// default. This is what "reset to defaults" writes — storing the current defaults instead
	/// would pin them, and a later change to what the app ships as default would not reach a
	/// device that had once pressed reset.
	/// </summary>
	/// <param name="key">The setting's key.</param>
	/// <param name="cancellationToken">Cancels the removal.</param>
	ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);
}
