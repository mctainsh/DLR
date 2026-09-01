using System.Runtime.CompilerServices;
using Android.Content;
using BlazorDLR.Shared.Diagnostics;
using BlazorDLR.Shared.Services;
using Application = Android.App.Application;

namespace BlazorDLR.Platforms.Android.Location;

/// <summary>
/// The Android binding of the GPS seam (§4.3, §18.2). Owns the permission ladder and the
/// foreground service that does the actual receiving.
/// <para>
/// <strong>The permission ladder is three separate asks, in order, and the order is not
/// negotiable.</strong>
/// <list type="number">
///   <item><c>ACCESS_FINE_LOCATION</c> - the only one that can be requested alongside anything
///     else. Since Android 12 the system dialog offers the rider "precise" or "approximate", and
///     approximate is a few hundred metres, which cannot place somebody on a road.</item>
///   <item><c>ACCESS_BACKGROUND_LOCATION</c> - from Android 11 this <em>cannot</em> be bundled
///     with the first ask. Requesting it in the same call gets the whole request denied without a
///     dialog. It has to be a second request, after foreground permission is already granted, and
///     the system sends the rider to a settings page rather than showing a dialog.</item>
///   <item><c>POST_NOTIFICATIONS</c> (Android 13+) - not needed to receive fixes, but the
///     foreground service's notification is invisible without it, and an invisible ongoing
///     notification is exactly what Play's background-location policy is written against.</item>
/// </list>
/// </para>
/// <para>
/// <strong>Background permission is asked for but not required.</strong> Refusing it leaves an app
/// that publishes while it is on screen and stops when the rider pockets the phone - degraded, but
/// working, and the honest thing to do with a rider who said no. Treating it as fatal would make
/// the answer to a privacy question a broken app.
/// </para>
/// </summary>
public sealed class AndroidLocationProvider : ILocationProvider
{
	/// <inheritdoc />
	public bool IsSupported => true;

	/// <inheritdoc />
	public bool IsRecording => LocationForegroundService.IsRunning;

	/// <inheritdoc />
	public async Task<LocationPermissionState> EnsurePermissionsAsync(
		CancellationToken cancellationToken = default)
	{
		// MAUI's Permissions API is used rather than raw ActivityCompat: it already knows which
		// manifest entries each request maps to per API level, and it marshals to the activity -
		// this is called from a background pump, and a permission request off the UI thread throws.
		// Every rung of the ladder is written down, and this is the file where that pays: the three
		// asks happen in a fixed order, each can be answered four ways, and the failures riders
		// actually hit - a background ask that never appeared, a "never ask again" from months ago
		// - are all invisible from inside the app afterwards. A log that says which rung was on
		// screen and what came back is the difference between diagnosing that and guessing at it.
		DiagnosticLog.Write("GPS: checking foreground location permission.");

		PermissionStatus status = await MainThread.InvokeOnMainThreadAsync(
			Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>);

		DiagnosticLog.Write($"GPS: foreground location permission is {status}.");

		if (status != PermissionStatus.Granted)
		{
			if (Permissions.ShouldShowRationale<Permissions.LocationWhenInUse>())
			{
				// The rider has refused once already. The ride screens carry the explanation
				// (§4.3), so the ask itself is not dressed up here - asking again immediately with
				// no new information is how an app trains somebody to hit Deny.
			}

			DiagnosticLog.Write("GPS: asking for foreground location permission.");

			status = await MainThread.InvokeOnMainThreadAsync(
				Permissions.RequestAsync<Permissions.LocationWhenInUse>);

			DiagnosticLog.Write($"GPS: rider answered {status} to foreground location.");
		}

		if (status != PermissionStatus.Granted)
		{
			// Android does not report "never ask again" directly. The tell is a denied permission
			// whose rationale flag is also false: the system will no longer show a dialog, so only
			// the app's settings page can change the answer.
			LocationPermissionState refused = Permissions.ShouldShowRationale<Permissions.LocationWhenInUse>()
				? LocationPermissionState.Denied
				: LocationPermissionState.DeniedPermanently;

			DiagnosticLog.Write($"GPS: foreground location refused - {refused}. Nothing else is asked for.");

			return refused;
		}

		// Second rung, and only now - see the type's remarks on why this cannot be bundled.
		if (OperatingSystem.IsAndroidVersionAtLeast(29))
		{
			PermissionStatus always = await MainThread.InvokeOnMainThreadAsync(
				Permissions.CheckStatusAsync<Permissions.LocationAlways>);

			DiagnosticLog.Write($"GPS: background location permission is {always}.");

			if (always != PermissionStatus.Granted)
			{
				// Not awaited for its answer beyond this: refusing background location is a
				// supported outcome, not a failure. See the remarks.
				always = await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<Permissions.LocationAlways>);

				// Recorded rather than acted on. A ride that stops producing fixes the moment the
				// screen goes off is this line, months later, and nothing else in the app can tell
				// the difference between that and a phone that cannot see the sky.
				DiagnosticLog.Write($"GPS: rider answered {always} to background location.");
			}
		}

		// Third rung. A refused notification permission does not stop the service - it makes it
		// silent, which is worth knowing but not worth refusing to ride over.
		if (OperatingSystem.IsAndroidVersionAtLeast(33))
		{
			PermissionStatus notifications = await MainThread.InvokeOnMainThreadAsync(
				Permissions.CheckStatusAsync<Permissions.PostNotifications>);

			DiagnosticLog.Write($"GPS: notification permission is {notifications}.");

			if (notifications != PermissionStatus.Granted)
			{
				notifications = await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<Permissions.PostNotifications>);

				DiagnosticLog.Write($"GPS: rider answered {notifications} to notifications.");
			}
		}

		DiagnosticLog.Write("GPS: permission ladder complete; the receiver may start.");

		return LocationPermissionState.Granted;
	}

	/// <summary>
	/// Starts the foreground service and yields what it produces, until the caller stops
	/// enumerating.
	/// <para>
	/// The service is started here and stopped in the <c>finally</c>, so the receiver's lifetime is
	/// exactly the lifetime of the enumeration - which is what makes
	/// <c>LocationBroadcastState.StopAsync</c> awaiting its pump the thing that takes the
	/// notification down.
	/// </para>
	/// </summary>
	/// <param name="rate">The rider's update rate (§4.2).</param>
	/// <param name="cancellationToken">Stops the receiver.</param>
	public async IAsyncEnumerable<LocationFix> WatchAsync(
		LocationUpdateRate rate,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		Context context = Application.Context;

		DiagnosticLog.Write($"GPS: asking the OS to start the foreground service - {rate}.");

		LocationForegroundService.Start(context, rate);

		// The gap between the service starting and the first fix arriving is the one a rider feels
		// - a map with no dot on it - and it is the gap nothing else measures. One line, on the
		// first fix only: the rest of a ride's fixes belong to the gate, not to this log.
		bool first = true;

		try
		{
			await foreach (LocationFix fix in LocationForegroundService.Reader
				.ReadAllAsync(cancellationToken)
				.ConfigureAwait(false))
			{
				if (first)
				{
					first = false;
					DiagnosticLog.Write(
						$"GPS: first fix - {fix.Latitude:F5},{fix.Longitude:F5}, " +
						$"accuracy {fix.AccuracyM?.ToString("F0") ?? "unknown"} m.");
				}

				yield return fix;
			}
		}
		finally
		{
			DiagnosticLog.Write("GPS: watch ended; stopping the foreground service.");

			// Runs on cancellation and on the consumer simply walking away from the enumerator.
			// An orphaned foreground service is a permanent notification and a permanent GPS -
			// the single worst bug this file could ship.
			LocationForegroundService.Stop(context);
		}
	}
}
