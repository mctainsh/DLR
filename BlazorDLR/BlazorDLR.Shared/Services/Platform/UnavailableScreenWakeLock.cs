namespace BlazorDLR.Shared.Services.Platform;

/// <summary>
/// The <see cref="IScreenWakeLock"/> both browser hosts bind, and the one the SSR pass gets
/// (§18.6).
/// <para>
/// <strong>Not a placeholder for a browser implementation that is coming.</strong> The Screen Wake
/// Lock API is there and would work; what is not there is the reason. The lock exists for a phone
/// clamped to a set of bars with a rider who cannot touch it (see <see cref="IScreenWakeLock"/>),
/// and the web app is the big-screen surface for planning and reading (§6.1). Silently overriding
/// somebody's display timeout on a laptop because they left a ride open in a tab is worse than
/// doing nothing.
/// </para>
/// </summary>
public sealed class UnavailableScreenWakeLock : IScreenWakeLock
{
	/// <inheritdoc />
	public bool IsSupported => false;

	/// <inheritdoc />
	public ValueTask RequestAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

	/// <inheritdoc />
	public ValueTask ReleaseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
