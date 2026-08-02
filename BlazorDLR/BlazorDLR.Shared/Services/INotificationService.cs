namespace BlazorDLR.Shared.Services;

/// <summary>
/// Push notifications (§17.6, §18.2).
/// <para>
/// <strong>Mobile:</strong> FCM on Android, APNs on iOS. Ordinary comments are silent while
/// the ride is <c>Live</c>; a pinned post from the organiser breaks through (§17.1, §17.6).
/// <strong>Web:</strong> no push in v1 — the implementation is a no-op. A component that
/// depends on delivery has picked mobile.
/// </para>
/// </summary>
public interface INotificationService
{
	/// <summary>Whether push is available at all on the current host.</summary>
	bool IsSupported { get; }

	/// <summary>Register a device token with the server so it can be targeted (§17.6).</summary>
	Task RegisterAsync(string deviceToken, CancellationToken cancellationToken = default);

	/// <summary>Un-register the current device's token. Called on sign-out.</summary>
	Task UnregisterAsync(CancellationToken cancellationToken = default);
}
