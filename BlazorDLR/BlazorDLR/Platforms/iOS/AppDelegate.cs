using BlazorDLR.Platforms.Apple.Notifications;
using Foundation;
using UIKit;
using UserNotifications;

namespace BlazorDLR;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	/// <summary>
	/// Kept alive for the life of the process. <c>UNUserNotificationCenter.Delegate</c> is a weak
	/// reference on the Objective-C side, so a delegate held only by the local that assigned it is
	/// collected as soon as this method returns — and the symptom is a notification that shows on a
	/// locked phone and silently does nothing in the foreground, which is a bad thing to have to
	/// diagnose on a device.
	/// </summary>
	private static ThreadNotificationDelegate? _notificationDelegate;

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	/// <summary>
	/// Claims the notification centre's delegate before launch completes (§17.6).
	/// <para>
	/// Apple requires the assignment to happen here rather than lazily, and the reason is the cold
	/// launch: tapping a notification on a locked phone starts the process and delivers the response
	/// during startup, so a delegate assigned when the first Blazor page renders has already missed
	/// it. See <see cref="ThreadNotificationDelegate"/> for what the two callbacks do.
	/// </para>
	/// <para>
	/// This registers <em>nothing remote</em> — no <c>RegisterForRemoteNotifications</c>, no APNs
	/// token, no entitlement. Claiming the delegate is a local-notification concern only.
	/// </para>
	/// </summary>
	public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
	{
		// Before base: MAUI builds the app and starts the first window in there, and a notification
		// response arriving in the middle of that still needs somewhere to land.
		_notificationDelegate = new ThreadNotificationDelegate();
		UNUserNotificationCenter.Current.Delegate = _notificationDelegate;

		return base.FinishedLaunching(application, launchOptions);
	}
}
