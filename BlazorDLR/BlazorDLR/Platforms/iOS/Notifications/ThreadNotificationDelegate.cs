using BlazorDLR.Shared.State;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UserNotifications;

namespace BlazorDLR.Platforms.Apple.Notifications;

/// <summary>
/// The two callbacks that make local notifications actually behave on iOS (§17.6).
/// <para>
/// <strong><see cref="WillPresentNotification"/> is not optional.</strong> iOS does not show a
/// notification an app raises on itself while that app is in the foreground — it delivers it
/// silently and expects the app to have drawn its own UI, because normally the app knows. That
/// default is exactly wrong here: the case this feature exists for is a rider on the <em>live
/// map</em>, which is the foreground, with the thread nowhere on screen. Without this delegate the
/// whole feature would appear to work on Android and do nothing on iOS except when the phone was
/// locked. <c>CommentNotifier</c> has already decided the rider is not reading the thread, so by
/// the time anything reaches here the answer is always "show it".
/// </para>
/// <para>
/// <strong>Set from <c>AppDelegate.FinishedLaunching</c>, before the app finishes launching.</strong>
/// Apple's requirement, and it has teeth: a notification tapped from a cold lock screen delivers its
/// response during startup, and a delegate assigned any later than this misses it — which is the
/// one launch where the rider most obviously expected to land somewhere specific.
/// </para>
/// </summary>
public sealed class ThreadNotificationDelegate : UNUserNotificationCenterDelegate
{
	/// <summary>
	/// Shows a notification the app raised while it was in the foreground. See the type's remarks —
	/// without this it is swallowed.
	/// </summary>
	public override void WillPresentNotification(
		UNUserNotificationCenter center,
		UNNotification notification,
		Action<UNNotificationPresentationOptions> completionHandler)
	{
		// Banner + List rather than the Alert option, which is deprecated from iOS 14 and is a
		// no-op on the 15.0 minimum this app targets. List is what puts it in Notification Centre
		// so a rider who missed the banner at speed can still find it when they stop.
		completionHandler(
			UNNotificationPresentationOptions.Banner
			| UNNotificationPresentationOptions.List
			| UNNotificationPresentationOptions.Sound);
	}

	/// <summary>
	/// Handles a tap, sending the route it carries to <see cref="NotificationRouting"/> for the
	/// layout to travel once Blazor is up.
	/// </summary>
	public override void DidReceiveNotificationResponse(
		UNUserNotificationCenter center,
		UNNotificationResponse response,
		Action completionHandler)
	{
		try
		{
			NSDictionary userInfo = response.Notification.Request.Content.UserInfo;

			if (userInfo.ValueForKey(new NSString(AppleNotificationService.RouteKey)) is NSString route)
			{
				// Resolved from the platform provider rather than injected: this object is created
				// by AppDelegate, outside any DI scope and before there is a rendered tree to be
				// scoped to. NotificationRouting is a singleton precisely so this lookup and the
				// layout's injected instance are the same object.
				IPlatformApplication.Current?.Services
					.GetService<NotificationRouting>()
					?.Request(route.ToString());
			}
		}
		catch (Exception)
		{
			// A tap that cannot be routed still has to open the app, which it already has by the
			// time this runs. The completion handler below is the part iOS is waiting on.
		}

		// Always called, and always exactly once — iOS logs a warning and eventually kills the app
		// for a response handler that never completes.
		completionHandler();
	}
}
