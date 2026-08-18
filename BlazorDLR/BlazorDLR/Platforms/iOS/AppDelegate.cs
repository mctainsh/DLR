using BlazorDLR.Platforms.Apple.Notifications;
using BlazorDLR.Shared.Diagnostics;
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
	/// <param name="application">The application being launched.</param>
	/// <param name="launchOptions">
	/// Why the app was started, or <c>null</c> — which is the ordinary case, since a launch from the
	/// home screen carries no options at all. The <c>?</c> is not decoration: UIKit declares this
	/// parameter nullable, and an override that narrows it is a promise the framework never made.
	/// </param>
	public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
	{
		// Before base: MAUI builds the app and starts the first window in there, and a notification
		// response arriving in the middle of that still needs somewhere to land.
		_notificationDelegate = new ThreadNotificationDelegate();
		UNUserNotificationCenter.Current.Delegate = _notificationDelegate;

		bool launched = base.FinishedLaunching(application, launchOptions);

		// Read back *after* MAUI has built the app, not just after the assignment: the delegate is
		// a weak reference on the Objective-C side and this is the one place a later claimant — or
		// a collected delegate — becomes visible before a rider notices a notification that never
		// appeared. Names the type, so "somebody else owns it now" reads differently from "nobody
		// does".
		DiagnosticLog.Write(
			$"Launched. Notification centre delegate: {UNUserNotificationCenter.Current.Delegate?.GetType().Name ?? "NONE"}.");

		return launched;
	}
}
