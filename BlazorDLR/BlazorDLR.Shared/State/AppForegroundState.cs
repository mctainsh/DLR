namespace BlazorDLR.Shared.State;

/// <summary>
/// Whether the app is the thing the rider is currently looking at (§17.6).
/// <para>
/// <strong>It exists for one bug, and it is a bug that would happen on every ride.</strong>
/// <see cref="CommentNotifier"/> suppresses a notification for a thread the rider already has open,
/// which is right while they are reading it — and catastrophically wrong thirty seconds later, when
/// they have locked the phone and ridden off. The page is still mounted and still claims the thread
/// is open, so without this every post for the rest of the ride would be swallowed on the strength
/// of a screen that is face-down in a tank bag. That is the <c>Live</c> silence coming back in
/// through a side door, which is precisely what v0.26 removed.
/// </para>
/// <para>
/// <strong>Singleton, and set from outside Blazor.</strong> The MAUI head drives it from the
/// window's <c>Resumed</c> and <c>Stopped</c> events, which run on the platform side with no scope
/// to resolve from — the same arrangement as <see cref="NotificationRouting"/>, and the same reason
/// it cannot be scoped.
/// </para>
/// <para>
/// <strong>The browsers leave it at <c>true</c> and that is correct for them</strong>, not a
/// concession: they raise no notifications at all (§18.2), so the value is never read there. A
/// browser binding that tracked <c>visibilitychange</c> would be machinery for a decision nothing
/// makes.
/// </para>
/// </summary>
public sealed class AppForegroundState
{
	private volatile bool _foreground = true;

	/// <summary>
	/// Raised whenever <see cref="IsForeground"/> changes. Fires on the platform's thread, not the
	/// UI thread — a subscriber that touches rendered state has to marshal for itself.
	/// </summary>
	public event Action<bool>? Changed;

	/// <summary>
	/// Whether the app is in front of the rider. Starts <c>true</c>: an app that is running enough
	/// to ask has a window, and starting from "backgrounded" would mean the first post of a ride was
	/// treated as arriving to a phone in a pocket when it is in the rider's hand.
	/// </summary>
	public bool IsForeground => _foreground;

	/// <summary>Records what the platform just reported. Idempotent — a repeat raises nothing.</summary>
	/// <param name="foreground">True when the app became visible, false when it went away.</param>
	public void Set(bool foreground)
	{
		if (_foreground == foreground)
			return;

		_foreground = foreground;
		Changed?.Invoke(foreground);
	}
}
