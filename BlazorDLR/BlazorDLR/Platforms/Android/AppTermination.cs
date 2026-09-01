using Android.Content;
using BlazorDLR.Platforms.Android.Location;
using BlazorDLR.Shared.Diagnostics;
using AndroidProcess = Android.OS.Process;

namespace BlazorDLR.Platforms.Android;

/// <summary>
/// Ends the app's process, for the two gestures that mean the rider closed the app: swiping the
/// task off Recents, and backing out of the root page.
/// <para>
/// <strong>Why this has to be explicit.</strong> Android does not end a process when its task goes
/// away. It finishes the activities and keeps the process in the LRU cache - and if a foreground
/// service is running in it, which is exactly what a ride is (see
/// <see cref="LocationForegroundService"/>), it keeps the process alive outright. The next launch
/// then lands *inside that surviving process*: <c>MainApplication</c> is not constructed again, so
/// <c>MauiProgram.CreateMauiApp</c> never runs again, and a brand-new <c>MainActivity</c> is built
/// on a MAUI host whose window, handlers and <c>BlazorWebView</c> were torn down with the activity
/// that has gone. What the rider sees is the splash screen and then nothing - the app "hangs" on
/// restart, and no amount of waiting fixes it because there is nothing left running to finish
/// starting.
/// </para>
/// <para>
/// So the swipe is honoured literally. Every launch after one is a cold start, which is the only
/// state this app is actually tested in.
/// </para>
/// </summary>
internal static class AppTermination
{
	/// <summary>
	/// Stops the receiver and ends this process. Does not return.
	/// </summary>
	/// <param name="context">Any context; the application context is used.</param>
	/// <param name="reason">What triggered the shutdown, for the log.</param>
	public static void EndProcess(Context context, string reason)
	{
		DiagnosticLog.Write($"===== DLR terminating (Android) ===== {reason}.");

		try
		{
			// Told to the ActivityManager *before* the process dies, not after, and this is the
			// load-bearing half of the whole file. LocationForegroundService returns START_STICKY,
			// so a process that simply dies with the service still "started" as far as the system
			// is concerned is one the system brings straight back - the service restarted with a
			// null intent, holding GPS and posting the ongoing notification, with no app behind it
			// and no way for the rider to reach it. Asking for the stop first clears that flag, so
			// the death is final.
			//
			// StopService rather than the service's own ActionStop intent: from Android 8 a
			// backgrounded app may not *start* a service, and by the time an activity is being
			// destroyed this app is background. Stopping one is always allowed.
			Context app = context.ApplicationContext ?? context;
			app.StopService(new Intent(app, typeof(LocationForegroundService)));
		}
		catch (Exception exception)
		{
			// Nothing here is retryable, and the process is going either way - but an orphaned
			// receiver is the failure worth being able to read about afterwards.
			DiagnosticLog.WriteError("stopping the location service during shutdown", exception);
		}

		AndroidProcess.KillProcess(AndroidProcess.MyPid());
	}
}
