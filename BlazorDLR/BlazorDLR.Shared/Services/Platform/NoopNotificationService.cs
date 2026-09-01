namespace BlazorDLR.Shared.Services.Platform;

/// <summary>
/// An <see cref="INotificationService"/> that reports "not supported" - the binding on both browser
/// hosts and on the desktop shells (§18.2).
/// <para>
/// Every method is a silent no-op rather than a throw, and <see cref="EnsurePermissionAsync"/>
/// answers <c>false</c> rather than pretending. A caller that wants to notify should not have to
/// know which host it is running on, and there is no state to corrupt by doing nothing - the post
/// it was about to describe is already in the thread, which is the only place it ever really lived.
/// </para>
/// </summary>
public sealed class NoopNotificationService : INotificationService
{
	/// <inheritdoc />
	public bool IsSupported => false;

	/// <inheritdoc />
	public Task<bool> EnsurePermissionAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult(false);

	/// <inheritdoc />
	public Task ShowAsync(LocalNotification notification, CancellationToken cancellationToken = default) =>
		Task.CompletedTask;

	/// <inheritdoc />
	public Task CancelAsync(string tag, CancellationToken cancellationToken = default) =>
		Task.CompletedTask;
}
