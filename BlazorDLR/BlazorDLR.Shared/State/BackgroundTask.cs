namespace BlazorDLR.Shared.State;

/// <summary>
/// Abandoning work nobody is waiting for, without leaving an unobserved exception behind.
/// <para>
/// The callers are hub callbacks and component lifecycle methods, neither of which has anywhere to
/// report a failure — and a notification that did not appear, or a count that did not persist, must
/// never be a reason the post itself fails to arrive in the thread (§17.6).
/// </para>
/// </summary>
internal static class BackgroundTask
{
	/// <summary>Lets <paramref name="task"/> run on unwatched, observing any exception it ends with.</summary>
	/// <param name="task">The work to abandon.</param>
	public static void Forget(this Task task) =>
		task.ContinueWith(
			static faulted => _ = faulted.Exception,
			CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted,
			TaskScheduler.Default);
}
