namespace BlazorDLR.Shared.Services;

/// <summary>
/// Holds the device's screen on while a screen is being read (§4.3, §18.2).
/// <para>
/// <strong>Why the live map needs one.</strong> A phone on a bar mount with a moving map on it is
/// the app's whole reason to exist, and a rider gets no chance to touch it: gloves on, at speed,
/// both hands where they should be. The platform's idle timer measures <em>input</em>, and a rider
/// reading a map produces none of it - so after thirty seconds the one thing they mounted the
/// phone for goes black, and getting it back means a hand off the bars.
/// </para>
/// <para>
/// <strong>Balanced calls, reference-counted.</strong> Every <see cref="RequestAsync"/> is matched
/// by exactly one <see cref="ReleaseAsync"/>, and the screen is only let go when the last holder
/// has released. That is not defensive decoration: Blazor tears a page down asynchronously, so
/// leaving the live map and coming back can run the outgoing page's release <em>after</em> the
/// incoming one's request - and on a plain on/off flag that ordering turns the map's screen off
/// and leaves it off.
/// </para>
/// <para>
/// <strong>The lock is a request, not a guarantee, and it never fights the rider.</strong> Both
/// platforms drop it while the app is not in front - an idle-timer flag on iOS and a window flag
/// on Android, neither of which applies to a backgrounded app - so nothing here can keep a phone
/// in a pocket awake. It does not brighten the screen or hold the CPU either; the receiver's
/// foreground service is what keeps fixes coming when the screen does go off (§4.3).
/// </para>
/// <para>
/// <strong>Phones only.</strong> Both browser hosts bind a stub that reports
/// <see cref="IsSupported"/> false and does nothing (§18.6) - the Screen Wake Lock API exists
/// there, but the case for it does not: the web app is the big-screen surface for planning and
/// reading (§6.1), not the thing clamped to a set of bars.
/// </para>
/// <para>
/// <strong>Nothing here throws.</strong> A window that has gone away underneath the call is not
/// worth failing a live map over, and there is nothing a rider could do about it if it were.
/// </para>
/// </summary>
public interface IScreenWakeLock
{
	/// <summary>
	/// Whether this host can keep its screen on. False on the browser hosts, where both calls
	/// below do nothing.
	/// <para>
	/// Callers do not need to check it - the stub is safe to call - but a screen that wants to
	/// explain the behaviour, or a test asserting a host does not have it, needs to be able to ask.
	/// </para>
	/// </summary>
	bool IsSupported { get; }

	/// <summary>
	/// Asks for the screen to stay on, and registers this caller as one of its holders.
	/// </summary>
	/// <param name="cancellationToken">Cancels the platform call.</param>
	ValueTask RequestAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Gives up this caller's hold. The screen goes back to the rider's own display timeout once
	/// every holder has released; a release with nothing held is ignored rather than counted
	/// negative, so a caller that unwinds twice cannot leave the next holder unable to take it.
	/// </summary>
	/// <param name="cancellationToken">Cancels the platform call.</param>
	ValueTask ReleaseAsync(CancellationToken cancellationToken = default);
}
