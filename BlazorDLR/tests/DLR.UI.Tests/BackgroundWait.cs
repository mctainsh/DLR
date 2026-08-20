using Xunit.Sdk;

namespace DLR.UI.Tests;

/// <summary>
/// Waits for something a <em>background task</em> reaches — the location pump, a fire-and-forget
/// continuation — rather than something the renderer publishes.
/// <para>
/// <strong>Why not <c>WaitForAssertion</c>.</strong> bUnit's waiters are render-driven: the
/// assertion is evaluated once when the wait starts and then only on each subsequent render of
/// the component. A value that changes on a thread-pool thread <em>without</em> causing a render
/// is therefore invisible to them — if the last render lands a moment before the change, nothing
/// re-checks and the wait times out with the condition long since true.
/// </para>
/// <para>
/// The receiver is exactly that shape. <c>LocationBroadcastState</c> raises
/// <c>WaitingForFix</c> — which does render — and only <em>then</em> enumerates
/// <c>ILocationProvider.WatchAsync</c>, so <c>FakeLocationProvider.WatchCount</c> moves after the
/// last render the page has any reason to do. Which of the two won was down to how busy the
/// machine was, which is what a flaky test is.
/// </para>
/// <para>
/// So this polls. Nothing here waits on a render, and nothing sleeps for a fixed period either:
/// a condition that comes true in 10 ms costs 10 ms.
/// </para>
/// </summary>
internal static class BackgroundWait
{
	/// <summary>How long between checks. Short enough not to dominate a fast pass.</summary>
	private const int PollMilliseconds = 10;

	/// <summary>
	/// How many checks before giving up — five seconds' worth. Generous on purpose: the thing
	/// being waited on is a thread-pool task, and the suite runs its collections in parallel, so
	/// the budget has to cover a scheduler that is busy rather than a pump that is broken.
	/// </summary>
	private const int Attempts = 500;

	/// <summary>
	/// Polls <paramref name="condition"/> until it holds, or fails the test.
	/// </summary>
	/// <param name="condition">What the background work is expected to reach.</param>
	/// <param name="because">
	/// What is being waited for, phrased to follow "Timed out waiting:" — "the receiver to start".
	/// </param>
	/// <param name="detail">
	/// Optional extra state for the failure message, read only if the wait times out. Where the
	/// answer usually is: a pump that gave up has already put the reason in its own status, and a
	/// bare "timed out" sends the next reader to the wrong place.
	/// </param>
	public static async Task UntilAsync(Func<bool> condition, string because, Func<string>? detail = null)
	{
		for (int attempt = 0; attempt < Attempts; attempt++)
		{
			if (condition())
			{
				return;
			}

			await Task.Delay(PollMilliseconds);
		}

		throw new XunitException(
			$"Timed out waiting: {because}.{(detail is null ? string.Empty : $" {detail()}")}");
	}
}
