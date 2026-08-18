using System.Diagnostics.CodeAnalysis;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using BlazorDLR.Platforms.Android.Notifications;
using BlazorDLR.Shared.Diagnostics;
using BlazorDLR.Shared.State;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorDLR;

// .gpx share-sheet + "Open with" integration (SharedFrontend.md §7 Phase 1). Three
// filters so the app appears both when a file manager opens a .gpx and when another
// app shares one:
//   1. VIEW on content:/file: URIs whose mime type is application/gpx+xml — what
//      browsers and cloud clients declare when they know the type.
//   2. VIEW on any URI whose path ends in .gpx — catches sources that hand the file
//      out as application/octet-stream without a specific mime.
//   3. SEND on application/gpx+xml — the "Share" sheet variant.
// The DEFAULT + BROWSABLE categories are needed so the system offers this app as a
// handler; MAUI's generated activity class name is what the manifest's <activity>
// block would have to reference, which is why the filters live here instead.
[Activity(
	Theme = "@style/Maui.SplashTheme",
	MainLauncher = true,
	ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(
	[Intent.ActionView],
	Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
	DataSchemes = new[] { "content", "file" },
	DataMimeType = "application/gpx+xml")]
[IntentFilter(
	[Intent.ActionView],
	Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
	DataSchemes = new[] { "content", "file" },
	DataMimeType = "*/*",
	DataPathPattern = ".*\\.gpx")]
[IntentFilter(
	[Intent.ActionSend],
	Categories = new[] { Intent.CategoryDefault },
	DataMimeType = "application/gpx+xml")]
[SuppressMessage("Interoperability", "CA1422:Validate platform compatibility")]
public class MainActivity : MauiAppCompatActivity
{

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);

		// The counterpart of the iOS Program.Main banner. Later than that one — MauiProgram has
		// already run by here, so the file sink is open — but it is still the first thing in the
		// Android head, and a relaunch the OS did on its own is worth being able to see.
		DiagnosticLog.Write(
			$"===== DLR starting (Android) ===== rebuilt by the OS: {savedInstanceState is not null}.");

		// A notification tapped from a cold lock screen launches the process and arrives here, on
		// the intent that started it. OnNewIntent below is the same tap on an app that was already
		// running — the ordinary case during a ride — and both have to be handled or the feature
		// works from exactly one of the two states.
		RouteFromNotification(Intent);

		Window!.SetFlags(WindowManagerFlags.Fullscreen,
						WindowManagerFlags.Fullscreen);

		Window.DecorView.SystemUiFlags =
			SystemUiFlags.ImmersiveSticky |
			SystemUiFlags.HideNavigation |
			SystemUiFlags.Fullscreen |
			SystemUiFlags.LayoutHideNavigation |
			SystemUiFlags.LayoutFullscreen |
			SystemUiFlags.LayoutStable;
	}


	/// <summary>
	/// A notification tapped while the app was already running (§17.6). The launcher intent is sent
	/// <c>SingleTop | ClearTop</c> — see <c>AndroidNotificationService.BuildContentIntent</c> — so
	/// the system delivers it here instead of building a second copy of the activity.
	/// </summary>
	protected override void OnNewIntent(Intent? intent)
	{
		base.OnNewIntent(intent);

		// Replaces the intent this activity reports for the rest of its life. Without it a later
		// OnCreate — a configuration change, a process the OS rebuilt — would replay whatever route
		// launched the app originally and take the rider somewhere they left ten minutes ago.
		if (intent is not null)
			Intent = intent;

		RouteFromNotification(intent);
	}

	protected override void OnResume()
	{
		base.OnResume();

		Window!.DecorView.SystemUiFlags =
			SystemUiFlags.ImmersiveSticky |
			SystemUiFlags.HideNavigation |
			SystemUiFlags.Fullscreen |
			SystemUiFlags.LayoutHideNavigation |
			SystemUiFlags.LayoutFullscreen |
			SystemUiFlags.LayoutStable;
	}

	/// <summary>
	/// Hands a tapped notification's route to <see cref="NotificationRouting"/>, which parks it
	/// until Blazor is rendered and able to travel it.
	/// <para>
	/// The extra is removed as it is read. An activity's intent outlives the tap — it is what
	/// <c>OnCreate</c> sees again if the OS rebuilds the process — and a route left on it would send
	/// the rider back to the same thread every time the app was restored.
	/// </para>
	/// </summary>
	private static void RouteFromNotification(Intent? intent)
	{
		string? route = intent?.GetStringExtra(AndroidNotificationService.ExtraRoute);

		if (string.IsNullOrWhiteSpace(route))
			return;

		intent!.RemoveExtra(AndroidNotificationService.ExtraRoute);

		// Resolved rather than injected: an Activity is constructed by the platform, outside DI.
		// NotificationRouting is a singleton so this and the layout's injected copy are one object.
		IPlatformApplication.Current?.Services
			.GetService<NotificationRouting>()
			?.Request(route);
	}
}
