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

	/// <inheritdoc />
	public bool IsSupported => true;

	/// <inheritdoc />
	public async Task<bool> EnsurePermissionAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			UNUserNotificationCenter centre = UNUserNotificationCenter.Current;
			UNNotificationSettings settings = await centre.GetNotificationSettingsAsync();

			// NotDetermined is the only status worth prompting on. Denied means the rider said no —
			// asking again does nothing at all on iOS, the prompt is shown once in the app's
			// lifetime — and Authorized/Provisional/Ephemeral are already a yes.
			if (settings.AuthorizationStatus != UNAuthorizationStatus.NotDetermined)
			{
				return settings.AuthorizationStatus
					is UNAuthorizationStatus.Authorized
					or UNAuthorizationStatus.Provisional
					or UNAuthorizationStatus.Ephemeral;
			}

			// Alert, Badge and Sound — the ordinary three. No CarPlay option is requested, and that
			// is not an oversight: §17.1's one surviving structural rule is that a thread never
			// reaches a car head unit, and asking for the entitlement that would let it is how that
			// rule gets quietly undone later.
			(bool approved, NSError? error) = await centre.RequestAuthorizationAsync(
				UNAuthorizationOptions.Alert | UNAuthorizationOptions.Badge | UNAuthorizationOptions.Sound);

			return approved && error is null;
		}
		catch (Exception)
		{
			// One notification that does not appear. The post is in the thread either way, and
			// there is no caller here with anywhere to report it.
			return false;
		}
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

			await UNUserNotificationCenter.Current.AddNotificationRequestAsync(request);
		}
		catch (Exception)
		{
			// See EnsurePermissionAsync. Nothing downstream of a notification is retryable, and a
			// failure here must never be a reason the post itself looks like it failed.
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
