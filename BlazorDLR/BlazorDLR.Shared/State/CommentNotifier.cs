using System.Globalization;
using BlazorDLR.Shared.Diagnostics;
using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Comments;

namespace BlazorDLR.Shared.State;

/// <summary>
/// Turns a post arriving on the hub into a notification on this phone (§17.6).
/// <para>
/// <strong>This is the whole of "notifications" in the product, and since v0.27 it decides almost
/// nothing.</strong> There is no ride state that changes what happens here, no per-ride mute, and
/// no test of whether the rider happens to be looking at the thread the post landed in — a post
/// arrives, the phone is told about it. The one question left is below, and it is arithmetic rather
/// than a restriction: a rider is not notified about the post they just wrote. Everything else
/// belongs to the operating system, where the rider already knows how to turn it off and where the
/// switch looks the same as it does for every other app on the phone.
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

	private bool _disposed;

	/// <summary>Starts watching the hub for posts.</summary>
	/// <param name="hub">Where posts arrive (§5.3).</param>
	/// <param name="notifications">The platform's notifier, or the no-op on a host with none.</param>
	/// <param name="auth">Who is reading — which is how a rider's own post is recognised.</param>
	public CommentNotifier(
		IRideHubClient hub,
		INotificationService notifications,
		AuthState auth)
	{
		_hub = hub;
		_notifications = notifications;
		_auth = auth;

		_hub.CommentPosted += OnCommentPosted;
	}

	/// <summary>The <see cref="LocalNotification.Tag"/> every post in one adventure shares.</summary>
	/// <param name="rideId">Which adventure.</param>
	/// <returns>A tag stable across posts, so the newest replaces the last.</returns>
	public static string TagFor(Guid rideId) =>
		"dlr.thread." + rideId.ToString("N", CultureInfo.InvariantCulture);

	/// <summary>
	/// The tag every post on one shared route shares (§6.2).
	/// <para>
	/// A different prefix from an adventure's, and that matters rather than being tidy: both are
	/// guids, and one namespace would let a route's card replace the ride card a rider is halfway
	/// through reading if the two identifiers ever collided.
	/// </para>
	/// </summary>
	/// <param name="trackId">Which route.</param>
	/// <returns>A tag stable across posts, so the newest replaces the last.</returns>
	public static string TagForTrack(Guid trackId) =>
		"dlr.route." + trackId.ToString("N", CultureInfo.InvariantCulture);

	/// <summary>
	/// Where <c>group-rides/thread/{id}</c> is assembled, so the notification and the router cannot
	/// drift apart.
	/// </summary>
	/// <param name="rideId">Which adventure.</param>
	/// <returns>A route relative to the app's base href.</returns>
	public static string RouteFor(Guid rideId) =>
		"group-rides/thread/" + rideId.ToString("D", CultureInfo.InvariantCulture);

	/// <summary>
	/// Where <c>rides/{id}</c> is assembled — a route's thread lives on the route's own page rather
	/// than on a screen of its own (§6.2), so tapping the card lands on the map, the description and
	/// the conversation together.
	/// </summary>
	/// <param name="trackId">Which route.</param>
	/// <returns>A route relative to the app's base href.</returns>
	public static string RouteForTrack(Guid trackId) =>
		"rides/" + trackId.ToString("D", CultureInfo.InvariantCulture);

	/// <summary>
	/// Withdraws whatever card is already showing for this adventure, because the rider has just
	/// opened its thread — and takes the chance to settle the notification permission while the
	/// app is demonstrably in front of the rider.
	/// <para>
	/// <strong>Housekeeping, not suppression.</strong> Called by <c>RideThread</c> on open, and it
	/// quietens nothing that comes afterwards — a post landing while the thread is on screen still
	/// raises a notification, which is what v0.27 means by presenting always. What it clears is the
	/// card left standing in the shade about a conversation the rider has now opened: that one has
	/// done its job, and a stale card is how riders learn to swipe notifications away without
	/// reading them.
	/// </para>
	/// </summary>
	/// <param name="rideId">The thread now on screen.</param>
	public void ThreadOpened(Guid rideId) => Opened(TagFor(rideId));

	/// <summary>
	/// The same housekeeping for a shared route's thread (§6.2), called by the route's page.
	/// </summary>
	/// <param name="trackId">The route whose thread is now on screen.</param>
	public void TrackThreadOpened(Guid trackId) => Opened(TagForTrack(trackId));

	/// <summary>Clears one thread's standing card and settles the permission while the app is up.</summary>
	/// <param name="tag">Which card.</param>
	private void Opened(string tag)
	{
		// Fire-and-forget: there is no caller waiting, and a failure leaves a stale card that the
		// next post replaces anyway.
		_notifications.CancelAsync(tag).Forget();

		if (!_notifications.IsSupported)
			return;

		// Ask for the permission here as well as on the way past RaiseAsync, and the difference is
		// *when*: this is a rider who has just opened a conversation, with the app in front of them.
		// RaiseAsync asks at the moment a post lands, which on a hub callback during a ride is a
		// phone in a tank bag with the screen off — and iOS will not put an authorisation alert on a
		// screen nobody is looking at, so a first-ever prompt raised from there is a prompt the rider
		// never answers and a notification that never appears. Android hid this: below API 33 there
		// is no prompt to miss at all.
		//
		// Idempotent by contract (INotificationService.EnsurePermissionAsync) — neither platform
		// shows a second prompt, so a rider who already answered sees nothing here.
		_notifications.EnsurePermissionAsync().Forget();
	}

	/// <summary>
	/// Whether <paramref name="comment"/> is worth interrupting the rider for.
	/// <para>
	/// One question, and deliberately no others: <strong>is it theirs?</strong> Nobody is told about
	/// their own post. That is not politeness and it is not a restriction on delivery — every post a
	/// rider makes comes straight back down the hub they published it on, so without this the app
	/// would notify you about everything you said, one card per sentence you typed.
	/// </para>
	/// <para>
	/// Nothing about ride state, nothing about a mute, and — since v0.27 — nothing about whether the
	/// thread is open in front of the rider or whether the app is the thing on screen. The first was
	/// v0.26's reversal; the rest are controls every phone already has, applied to every app on it
	/// (§17.6).
	/// </para>
	/// </summary>
	/// <param name="comment">The post that arrived.</param>
	/// <returns>True when a notification should be raised.</returns>
	public bool ShouldNotify(CommentDto comment) =>
		_auth.UserId is not { } me || comment.AuthorId != me;

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
	/// <remarks>
	/// A post from a shared route's thread is tagged and routed to the route's page instead, which
	/// is the only thing about a notification that the second kind of thread changed — the title,
	/// the body and the "not your own post" rule are the same question about the same record.
	/// </remarks>
	public static LocalNotification Compose(CommentDto comment) =>
		new(
			Tag: comment.TrackId is { } trackTag ? TagForTrack(trackTag) : TagFor(comment.GroupRideId ?? Guid.Empty),
			Title: string.IsNullOrWhiteSpace(comment.AuthorUserName) ? "New post" : comment.AuthorUserName,
			Body: Summarise(comment),
			Route: comment.TrackId is { } trackRoute
				? RouteForTrack(trackRoute)
				: RouteFor(comment.GroupRideId ?? Guid.Empty));

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
		// The first link in the chain, and the one with no other symptom: a hub that never
		// connected raises no event at all, which is indistinguishable from a platform that
		// refused the notification unless somebody says which happened. Goes to DiagnosticLog
		// rather than Debug.WriteLine so it survives a Release build and can be read on the phone.
		DiagnosticLog.Write(
			$"Post on the hub: ride {comment.GroupRideId}, author {comment.AuthorId} " +
			$"(disposed: {_disposed}, notifier: {_notifications.GetType().Name}, " +
			$"supported: {_notifications.IsSupported}, mine: {!ShouldNotify(comment)}).");

		if (_disposed || !_notifications.IsSupported || !ShouldNotify(comment))
			return;

		RaiseAsync(Compose(comment)).Forget();
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

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		_hub.CommentPosted -= OnCommentPosted;
	}
}
