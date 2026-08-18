using BlazorDLR.Shared.Diagnostics;
using BlazorDLR.Shared.Services;
using Foundation;
using UserNotifications;

namespace BlazorDLR.Platforms.Apple.Notifications;

/// <summary>
/// The iOS half of §17.6 — <c>UNUserNotificationCenter</c>, scheduling on the device itself.
/// <para>
/// <strong>There is no Apple Push Notifications registration anywhere in this app, and that is a
/// deliberate architectural choice rather than something deferred.</strong> Nothing here calls
/// <c>RegisterForRemoteNotifications</c>, the bundle carries no <c>aps-environment</c> entitlement,
/// the Apple Developer account needs no APNs key, and the server holds no device tokens. The post
/// this notification describes arrived over the SignalR connection the app was already holding for
/// the ride (§5.3); a push service would have been a second, slower, credentialed path for a
/// message that is already in memory.
/// </para>
/// <para>
/// <strong>What makes that work on iOS specifically</strong> is the <c>location</c> background mode
/// the receiver already declares (§4.3, Info.plist). A ride keeps the process alive, so the hub
/// stays connected and this class has something to post. Outside a ride iOS suspends the app and
/// nothing is raised — the rider reads the thread when they next open it, which is the documented
/// trade for owning no push infrastructure.
/// </para>
/// <para>
/// <strong>Local notifications need no capability and no provisioning change</strong>, so this ships
/// with the existing profile. The only thing the rider is ever asked for is the authorisation
/// prompt below, which is the same prompt every app on the phone shows.
/// </para>
/// </summary>
public sealed class AppleNotificationService : INotificationService
{
	/// <summary>
	/// The <c>UserInfo</c> key carrying <see cref="LocalNotification.Route"/> through the platform
	/// and back out at <see cref="ThreadNotificationDelegate"/> when the rider taps.
	/// </summary>
	public const string RouteKey = "dlr.route";

	/// <summary>
	/// The last settings line written, so <see cref="Describe"/> reports changes rather than
	/// repeating itself once per post. Null until the first read.
	/// </summary>
	private static string? _lastDescribed;

	/// <inheritdoc />
	public bool IsSupported => true;

	/// <inheritdoc />
	public async Task<bool> EnsurePermissionAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			// CommentNotifier asks here on two occasions and only two — a thread being opened, and
			// a post arriving on the hub — so this line is also the platform-side proof that a post
			// got this far. The shared notifier cannot say so itself: UiLayeringRules keeps every
			// Apple assembly out of BlazorDLR.Shared, so its own trace reaches the IDE only.
			DiagnosticLog.Write("Notification permission asked for.");

			// On the main thread, for the same reason AndroidNotificationService puts its
			// Permissions.RequestAsync there. The only caller is CommentNotifier, reacting to a post
			// that arrived on a SignalR callback — so without this the authorisation alert is asked
			// for from a thread pool thread, which is not where UIKit wants to be told to put a
			// system alert on screen.
			return await MainThread.InvokeOnMainThreadAsync(AuthoriseAsync);
		}
		catch (Exception exception)
		{
			// One notification that does not appear. The post is in the thread either way, and
			// there is no caller here with anywhere to report it — but a build being debugged on a
			// device should not have to guess, which is what the trace line is for.
			DiagnosticLog.Write($"Notification authorisation failed: {exception}");
			return false;
		}
	}

	/// <summary>
	/// Reads the current authorisation and, only on <c>NotDetermined</c>, asks for it. Always called
	/// on the main thread — see <see cref="EnsurePermissionAsync"/>.
	/// </summary>
	private static async Task<bool> AuthoriseAsync()
	{
		UNUserNotificationCenter centre = UNUserNotificationCenter.Current;
		UNNotificationSettings settings = await centre.GetNotificationSettingsAsync();

		Describe(settings);

		// NotDetermined is the only status worth prompting on. Denied means the rider said no —
		// asking again does nothing at all on iOS, the prompt is shown once in the app's
		// lifetime — and Authorized/Provisional/Ephemeral are already a yes.
		if (settings.AuthorizationStatus != UNAuthorizationStatus.NotDetermined)
		{
			bool already = settings.AuthorizationStatus
				is UNAuthorizationStatus.Authorized
				or UNAuthorizationStatus.Provisional
				or UNAuthorizationStatus.Ephemeral;

			if (!already)
				DiagnosticLog.Write($"Notifications not authorised ({settings.AuthorizationStatus}); nothing will be raised until the rider turns them on in Settings.");

			return already;
		}

		// Alert, Badge and Sound — the ordinary three. No CarPlay option is requested, and that
		// is not an oversight: §17.1's one surviving structural rule is that a thread never
		// reaches a car head unit, and asking for the entitlement that would let it is how that
		// rule gets quietly undone later.
		(bool approved, NSError? error) = await centre.RequestAuthorizationAsync(
			UNAuthorizationOptions.Alert | UNAuthorizationOptions.Badge | UNAuthorizationOptions.Sound);

		if (!approved || error is not null)
			DiagnosticLog.Write($"Notification authorisation refused (approved: {approved}, error: {error?.LocalizedDescription ?? "none"}).");

		return approved && error is null;
	}

	/// <summary>
	/// Writes out every switch behind <c>AuthorizationStatus</c>.
	/// <para>
	/// <strong>Authorised does not mean visible, and that gap is the whole reason this exists.</strong>
	/// A rider — or a Focus mode, or Scheduled Summary — can leave authorisation granted while
	/// turning off the banner, the lock screen, the sound, or all previews. iOS then accepts the
	/// request, calls the presentation delegate, honours none of what it asks for, and reports
	/// nothing wrong anywhere. From inside the app that is indistinguishable from a notification
	/// system that is simply broken, which is exactly how it looks from outside too.
	/// </para>
	/// <para>
	/// <c>AlertSetting</c> and <c>AlertStyle</c> are the two that most often answer it: <c>Disabled</c>
	/// and <c>None</c> respectively mean "deliver it quietly to Notification Centre", which is a
	/// notification nobody sees.
	/// </para>
	/// </summary>
	private static void Describe(UNNotificationSettings settings)
	{
		// iOS 15's two additions are on the same line as the rest on purpose: Scheduled Summary
		// holds a notification back for the next digest and a Focus mode drops anything that is not
		// Time Sensitive, and neither is visible in AuthorizationStatus. Between them they explain
		// most of "authorised, delivered, and nobody saw it".
		string described = OperatingSystem.IsIOSVersionAtLeast(15)
			? $"authorisation: {settings.AuthorizationStatus}, alert: {settings.AlertSetting}, " +
			  $"style: {settings.AlertStyle}, lock screen: {settings.LockScreenSetting}, " +
			  $"centre: {settings.NotificationCenterSetting}, sound: {settings.SoundSetting}, " +
			  $"badge: {settings.BadgeSetting}, previews: {settings.ShowPreviewsSetting}, " +
			  $"scheduled summary: {settings.ScheduledDeliverySetting}, " +
			  $"time sensitive allowed: {settings.TimeSensitiveSetting}"
			: $"authorisation: {settings.AuthorizationStatus}, alert: {settings.AlertSetting}, " +
			  $"style: {settings.AlertStyle}, lock screen: {settings.LockScreenSetting}, " +
			  $"centre: {settings.NotificationCenterSetting}, sound: {settings.SoundSetting}, " +
			  $"badge: {settings.BadgeSetting}, previews: {settings.ShowPreviewsSetting}";

		// Only when it changes. This runs on every post, and a ride's worth of identical settings
		// dumps would push the lines that actually differ out of a 1 000-line ring — while a rider
		// toggling Scheduled Summary or entering a Focus mode mid-ride is precisely the event this
		// is here to catch, and it only reads as an event against an unchanged background.
		string? previous = Interlocked.Exchange(ref _lastDescribed, described);

		if (!string.Equals(previous, described, StringComparison.Ordinal))
			DiagnosticLog.Write($"Notification settings — {described}.");
	}

	/// <inheritdoc />
	public async Task ShowAsync(LocalNotification notification, CancellationToken cancellationToken = default)
	{
		try
		{
			UNMutableNotificationContent content = new()
			{
				Title = notification.Title,
				Body = notification.Body,
				Sound = UNNotificationSound.Default,
			};

			if (notification.Route is { } route)
			{
				content.UserInfo = NSDictionary.FromObjectAndKey(
					new NSString(route),
					new NSString(RouteKey));
			}

			// A null trigger means "deliver now". The identifier is the tag, which is what makes a
			// second post in the same adventure replace the first rather than stack under it — iOS
			// treats a request whose identifier matches a delivered notification as an update.
			UNNotificationRequest request = UNNotificationRequest.FromIdentifier(
				notification.Tag,
				content,
				trigger: null);

			// Main thread, as above: the caller is a hub callback, and the notification centre is
			// being driven here in the same breath as the delegate UIKit calls back on.
			await MainThread.InvokeOnMainThreadAsync(
				() => UNUserNotificationCenter.Current.AddNotificationRequestAsync(request));

			// Half of the pair that says where the chain stopped. This line means iOS accepted the
			// request; whether anything appears is then ThreadNotificationDelegate's business, and
			// its own trace line is what distinguishes "never raised" from "raised and swallowed".
			DiagnosticLog.Write($"Notification handed to iOS: tag {notification.Tag}, delegate {UNUserNotificationCenter.Current.Delegate?.GetType().Name ?? "NONE — nothing will show in the foreground"}.");

			// Read back what iOS actually holds. This separates the last two possibilities that look
			// identical from here: if the tag is in this list, the notification exists and the phone
			// chose not to draw it — a settings answer, and the rider will find it by swiping down.
			// If it is absent, it was dropped or replaced, which is a bug in this app.
			UNNotification[] delivered = await MainThread.InvokeOnMainThreadAsync(
				() => UNUserNotificationCenter.Current.GetDeliveredNotificationsAsync());

			bool present = delivered.Any(item =>
				string.Equals(item.Request.Identifier, notification.Tag, StringComparison.Ordinal));

			DiagnosticLog.Write(
				$"iOS is holding {delivered.Length} delivered notification(s); this one is " +
				$"{(present ? "AMONG them — it exists, so the phone is choosing not to show it (check alert style, Focus, Scheduled Summary)" : "NOT among them — it was dropped or replaced")}.");
		}
		catch (Exception exception)
		{
			// See EnsurePermissionAsync. Nothing downstream of a notification is retryable, and a
			// failure here must never be a reason the post itself looks like it failed.
			DiagnosticLog.Write($"Notification could not be raised: {exception}");
		}
	}

	/// <inheritdoc />
	public Task CancelAsync(string tag, CancellationToken cancellationToken = default)
	{
		try
		{
			string[] identifiers = [tag];

			// Both lists: one that has been shown and one that is still queued are different
			// collections on iOS, and a card cleared from only the first can reappear.
			UNUserNotificationCenter.Current.RemoveDeliveredNotifications(identifiers);
			UNUserNotificationCenter.Current.RemovePendingNotificationRequests(identifiers);
		}
		catch (Exception)
		{
			// A card that will not go away is cosmetic, and the next post replaces it.
		}

		return Task.CompletedTask;
	}
}
