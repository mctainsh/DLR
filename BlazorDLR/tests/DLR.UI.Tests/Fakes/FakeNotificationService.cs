using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Fakes;

/// <summary>
/// An <see cref="INotificationService"/> that records what a phone would have shown.
/// <para>
/// Stands in for <c>AndroidNotificationService</c> and <c>AppleNotificationService</c>, neither of
/// which can be referenced from here - <c>UiLayeringRules</c> keeps every MAUI assembly out of this
/// project so bUnit runs under a plain <c>dotnet test</c>. The platform half is the part a compiler
/// can check and a device has to prove; the part worth asserting on is which posts got this far,
/// and that is <c>CommentNotifier</c>'s decision rather than the phone's.
/// </para>
/// </summary>
public sealed class FakeNotificationService : INotificationService
{
	/// <summary>Set false to stand in for a browser host, or any device with no notifier (§18.2).</summary>
	public bool IsSupported { get; set; } = true;

	/// <summary>Set false to stand in for a rider who refused the permission - a choice, not a fault.</summary>
	public bool PermissionGranted { get; set; } = true;

	/// <summary>How many times permission was asked for. Both platforms prompt at most once.</summary>
	public int PermissionRequests { get; private set; }

	/// <summary>Everything that reached the platform, oldest first.</summary>
	public List<LocalNotification> Shown { get; } = [];

	/// <summary>Every tag withdrawn, in order.</summary>
	public List<string> Cancelled { get; } = [];

	/// <inheritdoc />
	public Task<bool> EnsurePermissionAsync(CancellationToken cancellationToken = default)
	{
		PermissionRequests++;
		return Task.FromResult(PermissionGranted);
	}

	/// <inheritdoc />
	public Task ShowAsync(LocalNotification notification, CancellationToken cancellationToken = default)
	{
		Shown.Add(notification);
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public Task CancelAsync(string tag, CancellationToken cancellationToken = default)
	{
		Cancelled.Add(tag);
		return Task.CompletedTask;
	}
}
