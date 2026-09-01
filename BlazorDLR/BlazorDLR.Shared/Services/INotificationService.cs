namespace BlazorDLR.Shared.Services;

/// <summary>
/// Notifications this device raises on itself (§17.6, §18.2).
/// <para>
/// <strong>Local notifications, and deliberately not push.</strong> There is no FCM sender key, no
/// APNs <c>.p8</c>, no device-token registry on the server and no Apple Push Notifications
/// entitlement on the bundle - this app registers with nothing. What it has instead is the hub
/// connection it is already holding for the ride (§5.3): the post arrives over SignalR, and this
/// seam is only the last step of putting it on the lock screen. The whole feature is
/// <c>UNUserNotificationCenter</c> on iOS and <c>NotificationManagerCompat</c> on Android, both of
/// which are free of any server-side relationship.
/// </para>
/// <para>
/// <strong>What that costs, stated plainly.</strong> A notification can only be raised by a process
/// that is running, so this delivers exactly while the app is alive - which during a ride it is,
/// because the receiver holds an Android foreground service and iOS's <c>location</c> background
/// mode (§4.3). An app the OS has suspended raises nothing and the rider sees the thread when they
/// next open it. That is the trade for owning no push infrastructure, and it lands in the right
/// place: the case §17.1 cares about is the one that works.
/// </para>
/// <para>
/// <strong>There is no per-ride mute and no quiet-hours logic here.</strong> Every phone already has
/// both, applied consistently to every app on it and reachable from a place riders already know.
/// Re-implementing them would be a second, worse copy that only covers this app - so the channel
/// (Android) and the authorisation (iOS) are the whole of the control surface, and a rider who
/// wants an adventure to stop buzzing turns it off where they turn everything else off.
/// </para>
/// <para>
/// <strong>Web:</strong> not supported in v1 - the implementation is a no-op (§18.2). The browser
/// has its own Notification API, but the surface this feature exists for is the phone on a bar
/// mount, and a laptop with the tab open is already showing the thread.
/// </para>
/// </summary>
public interface INotificationService
{
	/// <summary>Whether this host can raise a notification at all. False on the browsers (§18.2).</summary>
	bool IsSupported { get; }

	/// <summary>
	/// Asks the platform for permission to post notifications, once, and answers whether it was
	/// given.
	/// <para>
	/// <strong>Idempotent, and cheap to call again.</strong> Neither platform will show a second
	/// prompt - Android 13's <c>POST_NOTIFICATIONS</c> dialog and iOS's authorisation alert are
	/// each shown once ever, and after that the answer comes back from the system without the
	/// rider seeing anything. So callers may simply ask before every notification rather than
	/// tracking state of their own.
	/// </para>
	/// <para>
	/// A refusal is not an error and must not be treated as one: it is a rider saying they do not
	/// want to be interrupted, which is exactly the choice §17.6 now leaves to them.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancels the request.</param>
	/// <returns>True when notifications may be posted.</returns>
	Task<bool> EnsurePermissionAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Posts <paramref name="notification"/> now, replacing any earlier one carrying the same
	/// <see cref="LocalNotification.Tag"/>.
	/// <para>
	/// Never throws for a reason the caller can act on - a denied permission, a host with no
	/// notifier and a platform that rejected the post all end the same way, with nothing on screen.
	/// The caller is a hub callback with nobody to report to.
	/// </para>
	/// </summary>
	/// <param name="notification">What to show, already summarised and shortened.</param>
	/// <param name="cancellationToken">Cancels the post.</param>
	Task ShowAsync(LocalNotification notification, CancellationToken cancellationToken = default);

	/// <summary>
	/// Withdraws whatever is showing under <paramref name="tag"/>, delivered or still pending.
	/// <para>
	/// What opening a thread calls: a rider reading the conversation does not also need a card
	/// about it sitting in the shade, and leaving it there is how a notification becomes something
	/// people swipe away without reading.
	/// </para>
	/// </summary>
	/// <param name="tag">The <see cref="LocalNotification.Tag"/> to clear. Unknown tags do nothing.</param>
	/// <param name="cancellationToken">Cancels the removal.</param>
	Task CancelAsync(string tag, CancellationToken cancellationToken = default);
}
