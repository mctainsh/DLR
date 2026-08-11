namespace BlazorDLR.Shared.Services.Platform;

/// <summary>
/// An <see cref="INotificationService"/> that reports "not supported" — the web binding in
/// v1 (§18.2), and the mobile binding until FCM/APNs are wired.
/// <para>
/// Register and unregister are silent no-ops rather than throws: a caller that registers on
/// sign-in should not have to know whether this host has push, and there is no state to
/// corrupt by doing nothing.
/// </para>
/// </summary>
public sealed class NoopNotificationService : INotificationService
{
	/// <inheritdoc />
	public bool IsSupported => false;

	/// <inheritdoc />
	public Task RegisterAsync(string deviceToken, CancellationToken cancellationToken = default) =>
		Task.CompletedTask;

	/// <inheritdoc />
	public Task UnregisterAsync(CancellationToken cancellationToken = default) =>
		Task.CompletedTask;
}
