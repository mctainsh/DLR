using System.Globalization;
using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Comments;

namespace BlazorDLR.Shared.State;

/// <summary>
/// Turns a post arriving on the hub into a notification on this phone (§17.6).
/// <para>
/// <strong>This is the whole of "notifications" in the product.</strong> Since v0.26 removed the
/// <c>Live</c>-ride silence, there is no ride state that changes what happens here and no per-ride
/// mute to consult — a post either interrupts the rider or it does not, and the two questions that
/// decide it are below. Everything else is the operating system's, where the rider already knows
/// how to turn it off.
/// </para>
/// <para>
/// <strong>Why the app can do this without a push service.</strong> The post has already arrived:
/// the ride screens hold a SignalR connection (§5.3) and the phone keeps it alive during a ride
/// through the receiver's foreground service on Android and the <c>location</c> background mode on
/// iOS (§4.3). So the message that FCM or APNs would have carried is sitting in memory, and all
/// that is missing is the lock screen. That is the trade this design makes: notifications work
/// exactly while the app is running, which during a ride is exactly when they are wanted.
/// </para>
/// <para>
/// <strong>Lives for as long as the app does.</strong> Wired in the constructor and resolved by
/// <c>MainLayout</c>, which renders on every page — so a rider on the live map, on the ride's info
/// page or three screens deep in settings is still being watched over by one instance. A notifier
/// that only existed while the thread was open would be a notifier for the one case that never
/// needs it.
/// </para>
/// </summary>
public sealed class CommentNotifier : IDisposable
{
	/// <summary>
	/// How much of a post goes on a lock screen. A comment may be 2 000 characters (§17.2) and both
	/// platforms will show two or three lines of it; the rest is weight in a payload nobody reads,
	/// and a rider who wants the whole thing is one tap from the thread.
	/// </summary>
	public const int MaxBodyChars = 180;

	private readonly IRideHubClient _hub;
	private readonly INotificationService _notifications;
	private readonly AuthState _auth;
	private readonly AppForegroundState _appState;
	private readonly Lock _gate = new();

	private Guid? _openThread;
	private bool _disposed;

	/// <summary>Starts watching the hub for posts.</summary>
	/// <param name="hub">Where posts arrive (§5.3).</param>
	/// <param name="notifications">The platform's notifier, or the no-op on a host with none.</param>
	/// <param name="auth">Who is reading — which is how a rider's own post is recognised.</param>
	/// <param name="appState">
	/// Whether the rider is actually looking at the app. Read rather than subscribed to: the only
	/// question asked of it is "right now?", at the moment a post arrives.
	/// </param>
	public CommentNotifier(
		IRideHubClient hub,
		INotificationService notifications,
		AuthState auth,
		AppForegroundState appState)
	{
		_hub = hub;
		_notifications = notifications;
		_auth = auth;
		_appState = appState;

		_hub.CommentPosted += OnCommentPosted;
	}

	/// <summary>The <see cref="LocalNotification.Tag"/> every post in one adventure shares.</summary>
	/// <param name="rideId">Which adventure.</param>
	/// <returns>A tag stable across posts, so the newest replaces the last.</returns>
	public static string TagFor(Guid rideId) =>
		"dlr.thread." + rideId.ToString("N", CultureInfo.InvariantCulture);

	/// <summary>
	/// Where <c>group-rides/{id}/thread</c> is assembled, so the notification and the router cannot
	/// drift apart.
	/// </summary>
	/// <param name="rideId">Which adventure.</param>
	/// <returns>A route relative to the app's base href.</returns>
	public static string RouteFor(Guid rideId) =>
		"group-rides/" + rideId.ToString("D", CultureInfo.InvariantCulture) + "/thread";

	/// <summary>
	/// Tells the notifier that the rider is looking at this adventure's thread, and clears whatever
	/// was already showing for it.
	/// <para>
	/// Called by <c>RideThread</c> on open. Notifying somebody about a message that is on the screen
	/// in front of them is the fastest way to teach them to ignore notifications.
	/// </para>
	/// </summary>
	/// <param name="rideId">The thread now on screen.</param>
	public void ThreadOpened(Guid rideId)
	{
		lock (_gate)
		{
			_openThread = rideId;
		}

		// The card in the shade is about a conversation the rider has just opened, so it has done
		// its job and withdrawing it is the courtesy. Fire-and-forget: there is no caller waiting
		// and a failure leaves a stale card, which the next post replaces anyway.
		Forget(_notifications.CancelAsync(TagFor(rideId)));
	}

	/// <summary>
	/// Tells the notifier the rider has left that thread, so posts in it interrupt again.
	/// <para>
	/// Takes the ride id and ignores a mismatch on purpose. Blazor disposes the outgoing page
	/// <em>after</em> initialising the incoming one, so a rider stepping from one adventure's thread
	/// straight to another's would otherwise have the first page's teardown clear the second page's
	/// claim — and then be notified about the thread they are reading.
	/// </para>
	/// </summary>
	/// <param name="rideId">The thread being left.</param>
	public void ThreadClosed(Guid rideId)
	{
		lock (_gate)
		{
			if (_openThread == rideId)
				_openThread = null;
		}
	}

	/// <summary>
	/// Whether <paramref name="comment"/> is worth interrupting the rider for.
	/// <para>
	/// Two questions, and deliberately no others:
	/// <list type="number">
	///   <item><strong>Is it theirs?</strong> Nobody is told about their own post. This is not
	///     politeness — every post a rider makes comes straight back down the hub they published it
	///     on, so without this the app would notify you about everything you said.</item>
	///   <item><strong>Are they already reading it?</strong> The thread is open, the app is in front
	///     of them, and the post is about to appear on screen on its own. Both halves are required —
	///     see <see cref="AppForegroundState"/> for what happens if the app trusts a mounted page on
	///     a phone that is in a pocket.</item>
	/// </list>
	/// </para>
	/// <para>
	/// Nothing about ride state, and nothing about a mute. The first is v0.26's reversal (§17.6);
	/// the second is a control every phone already has, applied to every app on it.
	/// </para>
	/// </summary>
	/// <param name="comment">The post that arrived.</param>
	/// <returns>True when a notification should be raised.</returns>
	public bool ShouldNotify(CommentDto comment)
	{
		if (_auth.UserId is { } me && comment.AuthorId == me)
			return false;

		lock (_gate)
		{
			return !(_appState.IsForeground && _openThread == comment.GroupRideId);
		}
	}

	/// <summary>
	/// Renders a post as the two lines a lock screen shows.
	/// <para>
	/// <strong>The author is the title, not the adventure.</strong> A hub post carries the author's
	/// handle (denormalised safely, §7.2) and the ride's <em>id</em> — not its name. Naming the
	/// adventure would mean a round trip per notification, or a cache read on a background thread,
	/// for a line the rider can already infer: they are on one ride, and the person talking is the
	/// thing they actually want to know. It is also the shape every messaging app on the phone
	/// already uses, which is worth more than being clever.
	/// </para>
	/// </summary>
	/// <param name="comment">The post that arrived.</param>
	/// <returns>What to hand the platform.</returns>
	public static LocalNotification Compose(CommentDto comment) =>
		new(
			Tag: TagFor(comment.GroupRideId),
			Title: string.IsNullOrWhiteSpace(comment.AuthorUserName) ? "New post" : comment.AuthorUserName,
			Body: Summarise(comment),
			Route: RouteFor(comment.GroupRideId));

	/// <summary>
	/// The one line under the author's name.
	/// <para>
	/// A post is text, a photograph, both, or a poll (§17.2, §17.5), and the three that are not
	/// plain text still have to say something — <em>"Sent a photo"</em> is a notification, an empty
	/// body is a bug that looks like one. Newlines collapse because a lock screen renders them and a
	/// three-line post would push everything else off the card.
	/// </para>
	/// </summary>
	private static string Summarise(CommentDto comment)
	{
		string? body = Flatten(comment.Body);

		if (comment.Kind == CommentKindDto.Poll)
			return body is null ? "Started a poll." : "Poll: " + body;

		if (body is not null)
			return body;

		return comment.PhotoId is not null ? "Sent a photo." : "Posted to the adventure.";
	}

	/// <summary>
	/// One line, no longer than <see cref="MaxBodyChars"/>, or null when there was nothing.
	/// <para>
	/// The ellipsis replaces the last character rather than being appended, so the result never
	/// exceeds the limit — a payload that grows past what it promised is how a cap stops being one.
	/// </para>
	/// </summary>
	private static string? Flatten(string? body)
	{
		if (string.IsNullOrWhiteSpace(body))
			return null;

		string flattened = string.Join(' ', body.Split(
			['\r', '\n', '\t'],
			StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

		if (flattened.Length == 0)
			return null;

		return flattened.Length <= MaxBodyChars
			? flattened
			: string.Concat(flattened.AsSpan(0, MaxBodyChars - 1).TrimEnd(), "…");
	}

	private void OnCommentPosted(CommentDto comment)
	{
		if (_disposed || !_notifications.IsSupported || !ShouldNotify(comment))
			return;

		Forget(RaiseAsync(Compose(comment)));
	}

	/// <summary>
	/// Asks for permission, then posts. Permission is requested every time rather than cached
	/// because neither platform prompts twice — see <see cref="INotificationService.EnsurePermissionAsync"/>
	/// — and a cache here would be a second, staler copy of an answer the rider can change in
	/// settings between one post and the next.
	/// </summary>
	private async Task RaiseAsync(LocalNotification notification)
	{
		if (await _notifications.EnsurePermissionAsync())
			await _notifications.ShowAsync(notification);
	}

	/// <summary>
	/// Abandons a task that nobody is waiting for, without leaving an unobserved exception behind.
	/// <para>
	/// Every caller here is a hub callback or a page lifecycle method, neither of which has anywhere
	/// to report a failed notification — and a notification that did not appear must never be a
	/// reason a post fails to arrive in the thread, which is the part that matters.
	/// </para>
	/// </summary>
	private static void Forget(Task task) =>
		task.ContinueWith(
			static faulted => _ = faulted.Exception,
			CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted,
			TaskScheduler.Default);

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		_hub.CommentPosted -= OnCommentPosted;
	}
}
