namespace BlazorDLR.Shared.State;

/// <summary>
/// Where a tapped notification wants the app to go (§17.6).
/// <para>
/// <strong>Why this is a letterbox rather than a call to <c>NavigationManager</c>.</strong> A
/// notification tap arrives at a platform object - Android's <c>MainActivity</c>, iOS's
/// <c>UNUserNotificationCenterDelegate</c> - and those run outside Blazor entirely. There may be no
/// rendered tree at all when the tap lands: a notification tapped from a cold lock screen launches
/// the process, and the router does not exist for another second or two. So the platform writes the
/// route here and leaves, and the layout picks it up whenever it is ready.
/// </para>
/// <para>
/// <strong>The pending route is held, not dropped.</strong> That is the whole reason this is not
/// just an event. A tap that arrives before anything is listening would otherwise open the app on
/// the home screen - which is the exact failure the route exists to prevent, and it would happen
/// specifically in the cold-launch case that matters most. <see cref="TakePending"/> is how the
/// listener collects one that arrived early, and it clears as it reads so a route is never
/// travelled twice.
/// </para>
/// <para>
/// <strong>Singleton, on every host.</strong> The two mobile heads resolve it from the platform
/// service provider outside any scope, so a scoped registration would hand them a different
/// instance from the one the layout is listening to. The browsers register it too and simply never
/// write to it - the shared layout injects it unconditionally (§18.2).
/// </para>
/// </summary>
public sealed class NotificationRouting
{
	private readonly Lock _gate = new();
	private string? _pending;

	/// <summary>
	/// Raised when a route arrives and something is already listening. Never raised for a route
	/// that had to be parked - that one comes back from <see cref="TakePending"/> instead.
	/// </summary>
	public event Action<string>? RouteRequested;

	/// <summary>
	/// Records that the rider tapped a notification pointing at <paramref name="route"/>.
	/// <para>
	/// Safe to call from any thread and from outside Blazor, which is the only way it is ever
	/// called. The handler hops to the UI thread itself - see the layout.
	/// </para>
	/// </summary>
	/// <param name="route">Relative to the app's base href, e.g. <c>group-rides/thread/{id}</c>.</param>
	public void Request(string route)
	{
		if (string.IsNullOrWhiteSpace(route))
			return;

		Action<string>? listeners = RouteRequested;

		if (listeners is null)
		{
			// Nothing is rendered yet - a cold launch from the lock screen. Park it for whoever
			// subscribes next. Listeners must call TakePending immediately after subscribing,
			// which is what closes the gap between this read and their subscription.
			lock (_gate)
			{
				_pending = route;
			}

			return;
		}

		listeners(route);
	}

	/// <summary>
	/// Collects a route that arrived before anything was listening, clearing it as it reads.
	/// Answers null when there is nothing waiting, which is the ordinary case.
	/// </summary>
	/// <returns>The parked route, or null.</returns>
	public string? TakePending()
	{
		lock (_gate)
		{
			string? pending = _pending;
			_pending = null;
			return pending;
		}
	}
}
