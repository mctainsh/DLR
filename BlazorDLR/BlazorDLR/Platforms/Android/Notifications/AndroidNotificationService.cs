using Android.App;
using Android.Content;
using AndroidX.Core.App;
using BlazorDLR.Shared.Services;

namespace BlazorDLR.Platforms.Android.Notifications;

/// <summary>
/// The Android half of §17.6 — <c>NotificationManagerCompat</c>, and nothing else.
/// <para>
/// <strong>No Firebase, no sender key, no <c>google-services.json</c>.</strong> This posts a
/// notification the app composed itself from a message that arrived on the hub it was already
/// holding (§5.3). There is no cloud messaging dependency to add, no token to register with the
/// server and nothing to keep in step when a device reinstalls — the entire feature is one call to
/// <c>notify</c>.
/// </para>
/// <para>
/// <strong>Importance is <c>Default</c>, deliberately, and that is the last of §17.1 that survives
/// in code.</strong> <c>Default</c> makes a sound and puts a card in the shade; <c>High</c> would
/// add a heads-up banner that slides over whatever is on screen — which during a ride is the live
/// map the rider is navigating by. Telling somebody there is a message is the point; covering their
/// map with it is the thing §17.1 was written about. A rider who wants more or less than this
/// changes the channel in Android's own settings, where the control belongs and where it looks the
/// same as it does for every other app on the phone.
/// </para>
/// </summary>
public sealed class AndroidNotificationService : INotificationService
{
	/// <summary>
	/// The channel adventure-thread posts arrive on. Separate from <c>dlr.location</c> so a rider
	/// can silence conversation without silencing the ongoing location notification, which must
	/// stay visible for as long as the receiver runs (§4.3) — one channel for both would make those
	/// two settings the same setting.
	/// </summary>
	private const string ChannelId = "dlr.thread";

	/// <summary>
	/// The notification id every thread post shares. The <em>tag</em> is what separates one
	/// adventure from another — <c>notify(tag, id)</c> keys on the pair, so a constant id with a
	/// per-ride tag gives exactly one live card per adventure, replaced by its newest post.
	/// </summary>
	private const int NotificationId = 4302;

	/// <summary>
	/// The intent extra carrying <see cref="LocalNotification.Route"/> into <c>MainActivity</c>.
	/// Read there and handed to <c>NotificationRouting</c>.
	/// </summary>
	public const string ExtraRoute = "dlr.route";

	/// <inheritdoc />
	public bool IsSupported => true;

	/// <inheritdoc />
	public async Task<bool> EnsurePermissionAsync(CancellationToken cancellationToken = default)
	{
		// Below Android 13 there is no runtime permission at all — notifications are granted at
		// install and the request API answers Granted without a dialog. Checked explicitly anyway
		// so the intent is readable rather than resting on MAUI's shim behaving.
		if (!OperatingSystem.IsAndroidVersionAtLeast(33))
			return NotificationManagerCompat.From(Platform.AppContext).AreNotificationsEnabled();

		try
		{
			PermissionStatus status = await MainThread.InvokeOnMainThreadAsync(
				Permissions.CheckStatusAsync<Permissions.PostNotifications>);

			if (status != PermissionStatus.Granted)
			{
				status = await MainThread.InvokeOnMainThreadAsync(
					Permissions.RequestAsync<Permissions.PostNotifications>);
			}

			return status == PermissionStatus.Granted;
		}
		catch (Exception)
		{
			// A permission request that throws — no activity attached because the app is being
			// reclaimed, most likely — is not a rider-facing failure. It is one notification that
			// does not appear, and the post is in the thread either way.
			return false;
		}
	}

	/// <inheritdoc />
	public Task ShowAsync(LocalNotification notification, CancellationToken cancellationToken = default)
	{
		try
		{
			EnsureChannel();

			NotificationCompat.Builder builder = new(Platform.AppContext, ChannelId);

			// Called as statements rather than chained, for the reason spelled out in
			// LocationForegroundService.BuildNotification: AndroidX's setters return a nullable
			// self, so a fluent chain is a run of possible-null dereferences.
			builder.SetContentTitle(notification.Title);
			builder.SetContentText(notification.Body);

			// The expanded form, for a post longer than the collapsed card's one line. Without it a
			// two-sentence comment is truncated with no way to read the rest short of opening the
			// app — which is the tap this notification is trying to save on a moving bike.
			builder.SetStyle(new NotificationCompat.BigTextStyle().BigText(notification.Body));

			builder.SetSmallIcon(global::BlazorDLR.Resource.Drawable.dlr_thread_notification);
			builder.SetCategory(NotificationCompat.CategoryMessage);
			builder.SetPriority(NotificationCompat.PriorityDefault);
			builder.SetAutoCancel(true);

			// Private, unlike the location notification's Public. That one is a claim about the app
			// that has to be visible on a locked phone; this one is somebody's message, and a
			// message on a lock screen in a café is read by whoever is standing there. The system
			// shows the app name and hides the text until the phone is unlocked.
			builder.SetVisibility(NotificationCompat.VisibilityPrivate);

			if (BuildContentIntent(notification.Route) is { } content)
				builder.SetContentIntent(content);

			NotificationManagerCompat.From(Platform.AppContext)
				.Notify(notification.Tag, NotificationId, builder.Build());
		}
		catch (Exception)
		{
			// Swallowed for the same reason the permission failure above is. Nothing downstream of
			// a notification can be retried usefully, and the thread already holds the post.
		}

		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public Task CancelAsync(string tag, CancellationToken cancellationToken = default)
	{
		try
		{
			NotificationManagerCompat.From(Platform.AppContext).Cancel(tag, NotificationId);
		}
		catch (Exception)
		{
			// A card that will not go away is a cosmetic problem, and the next post replaces it.
		}

		return Task.CompletedTask;
	}

	/// <summary>
	/// The tap target: the app's launcher intent, carrying the route as an extra.
	/// <para>
	/// <c>SingleTop</c> plus <c>ClearTop</c> so a tap reuses the activity that is already there
	/// rather than stacking a second copy of the app behind the first —
	/// <c>MainActivity.OnNewIntent</c> is what receives it in that case, which is the ordinary one
	/// during a ride.
	/// </para>
	/// <para>
	/// <c>Immutable</c> from API 23 up: the extra is ours, nothing outside the app has any business
	/// rewriting the destination, and from Android 12 a mutable <c>PendingIntent</c> with no
	/// explicit flag throws outright.
	/// </para>
	/// </summary>
	private static PendingIntent? BuildContentIntent(string? route)
	{
		Context context = Platform.AppContext;
		Intent? launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);

		if (launch is null)
			return null;

		launch.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);

		if (route is not null)
			launch.PutExtra(ExtraRoute, route);

		return PendingIntent.GetActivity(
			context,
			// A request code per route, so two adventures' notifications do not share one
			// PendingIntent and quietly overwrite each other's extras — UpdateCurrent keys on the
			// intent's identity, and extras are not part of that identity.
			route?.GetHashCode(StringComparison.Ordinal) ?? 0,
			launch,
			OperatingSystem.IsAndroidVersionAtLeast(23)
				? PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent
				: PendingIntentFlags.UpdateCurrent);
	}

	/// <summary>
	/// Creates the channel if it is not already there. Idempotent, and cheap — the platform ignores
	/// a create for an id it already knows, and deliberately ignores every property on it, so the
	/// importance chosen above is the <em>initial</em> value and a rider's later change to it wins
	/// forever. That is the correct behaviour and the reason this is not a setting in the app.
	/// </summary>
	private static void EnsureChannel()
	{
		if (!OperatingSystem.IsAndroidVersionAtLeast(26))
			return;

		NotificationManager? manager =
			(NotificationManager?)Platform.AppContext.GetSystemService(Context.NotificationService);

		if (manager is null || manager.GetNotificationChannel(ChannelId) is not null)
			return;

		NotificationChannel channel = new(
			ChannelId,
			"Adventure posts",
			NotificationImportance.Default)
		{
			Description = "Posts in the thread of a group adventure you are on.",
		};

		channel.SetShowBadge(true);
		manager.CreateNotificationChannel(channel);
	}
}
