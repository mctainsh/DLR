namespace BlazorDLR.Shared.Services;

/// <summary>
/// One notification this device raises on itself (§17.6).
/// <para>
/// <strong>Local, not push.</strong> Nothing here crosses the wire and no server ever sends it:
/// the app is already holding a SignalR connection for the ride it is on (§5.3), so the message
/// that would have travelled through FCM or APNs has <em>already arrived</em> by the time this is
/// composed. All that is left is asking the operating system to put it on the lock screen. That is
/// why this type lives beside the client seam rather than in <c>DLR.Core.Contracts</c> - it is not
/// a contract with anything, it is an instruction to the phone in the rider's pocket.
/// </para>
/// </summary>
/// <param name="Tag">
/// What this notification is <em>about</em>, so a second one replaces the first rather than
/// stacking under it.
/// <para>
/// One tag per adventure (see <c>CommentNotifier.TagFor</c>). A group of twelve riders can put
/// twenty posts in a thread while somebody is riding, and twenty entries in a shade is not twenty
/// times as useful as one - it is a wall the rider has to dismiss at the next set of lights. The
/// newest post is the one worth showing, and the thread holds the rest.
/// </para>
/// <para>
/// Both platforms have this concept natively: Android's <c>notify(tag, id)</c> replaces by tag,
/// and iOS replaces a delivered notification whose request identifier matches. Neither needs the
/// app to track what it has already shown.
/// </para>
/// </param>
/// <param name="Title">
/// The bold first line. For a thread post this is the author's handle - which §7.2 makes immutable,
/// so it is safe to render from whatever the hub happened to send.
/// </param>
/// <param name="Body">
/// The line underneath. Already summarised and already shortened by the caller: a comment may be
/// 2 000 characters (§17.2) and a lock screen shows two lines of them.
/// </param>
/// <param name="Route">
/// Where tapping it should land, relative to the app's base href - <c>group-rides/thread/{id}</c>
/// for a post. Null for a notification with nowhere to go.
/// <para>
/// <strong>A notification that only opens the home screen is a dead end.</strong> The rider was
/// told there is something to read; making them find it defeats the point of telling them. The
/// hosts route this through <see cref="NotificationRouting"/>.
/// </para>
/// </param>
public sealed record LocalNotification(
	string Tag,
	string Title,
	string Body,
	string? Route = null);
