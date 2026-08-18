using Android.App;
using Android.Content;
using AndroidX.Core.App;
using BlazorDLR.Shared.Diagnostics;
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
/// <strong>Importance is <c>High</c> as of v0.27, and the last of §17.1 that lived in code is
/// gone.</strong> It read <c>Default</c> — sound and a card in the shade, but no heads-up banner —
/// on the argument that a banner slides over the live map a rider is navigating by. That argument
/// has not changed; who answers it has. The app no longer holds anything back on the rider's
/// behalf: it presents the notification, and a rider who wants less turns it down in the channel's
/// own settings, in Do Not Disturb, or in a riding focus mode — the controls the phone already
/// applies to every other app on it (§17.6).
/// </para>
/// <para>
/// <strong>Which is why the channel id carries a version.</strong> Android takes a channel's
/// importance as the value it is <em>created</em> with and ignores every later create for the same
/// id, precisely so an app cannot turn its own volume back up behind a rider's back. That is the
/// right rule and it is also why raising this line alone would have changed nothing on any phone
/// the app is already installed on — the <c>dlr.thread</c> channel would still be sitting there at
/// <c>Default</c>. The new id is a new channel, so the new importance actually lands, and the old
/// one is deleted rather than left in the app's settings as a dead duplicate.
/// </para>
/// </summary>
public sealed class AndroidNotificationService : INotificationService
{
	/// <summary>
	/// The channel adventure-thread posts arrive on. Separate from <c>dlr.location</c> so a rider
	/// can silence conversation without silencing the ongoing location notification, which must
	/// stay visible for as long as the receiver runs (§4.3) — one channel for both would make those
	/// two settings the same setting.
	/// <para>
	/// The <c>.v2</c> is what carries v0.27's importance change onto phones that already have the
	/// app — see the type's remarks. A rider who had turned the old channel down has to turn this
	/// one down as well, once; that is the cost of the change and it is stated rather than hidden.
	/// </para>
	/// </summary>
	private const string ChannelId = "dlr.thread.v2";

	/// <summary>
	/// The channel this replaced, deleted on the first post after an upgrade. Kept as a constant
	/// rather than a literal so it is obvious that it is dead, and so a future rename has one
	/// obvious place to add to.
	/// </summary>
	private const string RetiredChannelId = "dlr.thread";

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

	/// <summary>
	/// The system's notification manager for this app.
	/// <para>
	/// AndroidX declares <c>From</c> as returning a nullable, the same way its builder setters return
	/// a nullable self — see the note in <see cref="ShowAsync"/>. It is not known to answer null on
	/// any device, and there is no documented case where it would; the null is a gap in the binding's
	/// annotations rather than a state worth designing around.
	/// </para>
	/// <para>
	/// It is still handled at every call site rather than dismissed with a <c>!</c>, because the two
	/// are not the same bet: every use of it here is inside a method that already swallows its own
	/// failures — a notification that does not appear costs a card in the shade, and the post is in
	/// the thread either way — so treating null as "no notification" costs nothing, while the <c>!</c>
	/// would trade that for a crash on whichever phone eventually proves the annotation right.
	/// </para>
	/// </summary>
	private static NotificationManagerCompat? Notifications =>
		NotificationManagerCompat.From(Platform.AppContext);

	/// <inheritdoc />
	public async Task<bool> EnsurePermissionAsync(CancellationToken cancellationToken = default)
	{
		// Below Android 13 there is no runtime permission at all — notifications are granted at
		// install and the request API answers Granted without a dialog. Checked explicitly anyway
		// so the intent is readable rather than resting on MAUI's shim behaving.
		// False rather than true when there is no manager to ask: this answer is what decides whether
		// a post is attempted at all, and "I could not find out" has to read as "do not", or the one
		// device that lands here posts into a system that has already said no.
		if (!OperatingSystem.IsAndroidVersionAtLeast(33))
			return Notifications?.AreNotificationsEnabled() ?? false;

		try
		{
			PermissionStatus status = await MainThread.InvokeOnMainThreadAsync(
				Permissions.CheckStatusAsync<Permissions.PostNotifications>);

			if (status != PermissionStatus.Granted)
			{
				DiagnosticLog.Write($"Notification permission is {status}; asking.");

				status = await MainThread.InvokeOnMainThreadAsync(
					Permissions.RequestAsync<Permissions.PostNotifications>);

				DiagnosticLog.Write($"Notification permission answered: {status}.");
			}

			// Granted is not the same as visible — the channel carries its own importance, and a
			// rider who turned it down keeps that setting through every upgrade. Reported for the
			// same reason iOS reports its alert style: authorised-and-silent is the failure that
			// looks like a broken app from both sides.
			if (status == PermissionStatus.Granted && Notifications is { } manager)
			{
				DiagnosticLog.Write(
					$"Notifications enabled: {manager.AreNotificationsEnabled()}, " +
					$"channel '{ChannelId}' importance: {ChannelImportance()}.");
			}

			return status == PermissionStatus.Granted;
		}
		catch (Exception exception)
		{
			DiagnosticLog.WriteError("asking for the notification permission", exception);

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

			// PriorityHigh is the pre-Android-8 half of the same decision the channel makes above:
			// on API 25 and below there are no channels and this field is what produces the
			// heads-up. Both are set because the app supports both, and a build where the two
			// disagreed would behave differently on two phones for no reason a rider could see.
			builder.SetPriority(NotificationCompat.PriorityHigh);
			builder.SetAutoCancel(true);

			// Public, matching the location notification, as of v0.27. Private hid the text on a
			// locked phone — which is a sensible default for a message and is exactly the sort of
			// call the app has stopped making for the rider: Android has a system-wide "show
			// sensitive content on the lock screen" setting, and Public is what defers to it
			// instead of overriding it in one direction for one app.
			builder.SetVisibility(NotificationCompat.VisibilityPublic);

			if (BuildContentIntent(notification.Route) is { } content)
				builder.SetContentIntent(content);

			Notifications?.Notify(notification.Tag, NotificationId, builder.Build());
			DiagnosticLog.Write($"Notification posted: tag {notification.Tag}.");
		}
		catch (Exception exception)
		{
			// Swallowed for the same reason the permission failure above is. Nothing downstream of
			// a notification can be retried usefully, and the thread already holds the post.
			DiagnosticLog.WriteError($"posting notification {notification.Tag}", exception);
		}

		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public Task CancelAsync(string tag, CancellationToken cancellationToken = default)
	{
		try
		{
			Notifications?.Cancel(tag, NotificationId);
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
	/// Creates the channel if it is not already there, and clears away the one it replaced.
	/// Idempotent, and cheap — the platform ignores a create for an id it already knows, and
	/// deliberately ignores every property on it, so the importance chosen here is the
	/// <em>initial</em> value and a rider's later change to it wins forever. That is the correct
	/// behaviour and the reason this is not a setting in the app.
	/// </summary>
	/// <summary>
	/// The importance the channel is actually running at, which is not necessarily the one this
	/// code asked for: Android fixes it at creation and a rider's later change wins forever. That
	/// is the correct behaviour and exactly why it is worth reading back.
	/// </summary>
	private static string ChannelImportance()
	{
		if (!OperatingSystem.IsAndroidVersionAtLeast(26))
			return "n/a below API 26";

		NotificationManager? manager =
			(NotificationManager?)Platform.AppContext.GetSystemService(Context.NotificationService);

		return manager?.GetNotificationChannel(ChannelId)?.Importance.ToString() ?? "channel not created yet";
	}

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
			NotificationImportance.High)
		{
			Description = "Posts in the thread of a group adventure you are on.",
		};

		channel.SetShowBadge(true);
		manager.CreateNotificationChannel(channel);

		// Only reached once per install, on the first post after upgrading: the guard above returns
		// early on every subsequent call. Leaving the retired channel behind would put two
		// "Adventure posts" rows in the app's notification settings, one of which controls nothing.
		manager.DeleteNotificationChannel(RetiredChannelId);
	}
}
