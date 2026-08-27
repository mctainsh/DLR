namespace BlazorDLR.Shared.Services;

/// <summary>
/// What one turn of a <see cref="PositionOutbox"/> has to send.
/// </summary>
/// <param name="Fix">The newest position waiting to go, or <c>null</c> if none is.</param>
/// <param name="Privacy">The private-area crossing waiting to go, or <c>null</c> if none is.</param>
public readonly record struct OutboxBatch(LocationFix? Fix, bool? Privacy)
{
	/// <summary>Nothing to send — what a drained and closed outbox answers.</summary>
	public bool IsEmpty => Fix is null && Privacy is null;
}

/// <summary>
/// The one-deep mailbox between the fix pump and the network (§4.2, §4.3).
/// <para>
/// <strong>Why it exists.</strong> The pump used to send inline: a fix arrived, and the loop that
/// read it awaited a hub round trip and then an HTTP POST before it went back for the next one. On
/// a good link that is invisible. On a marginal one — which on a motorcycle is most of them — a
/// single send that hangs stops <em>everything</em>: the rider's own mark, the recorder, and every
/// fix behind it. Riders saw exactly that: a pin frozen for minutes, then a jump of tens of
/// kilometres, then normal movement again. This is the seam that stops it. The pump posts and
/// returns; one task on the other side does the waiting.
/// </para>
/// <para>
/// <strong>Latest wins, and that is the whole design.</strong> There is one slot for a position and
/// one for a privacy crossing, not a queue. A position that a newer one has already superseded is
/// worth nothing to anybody — publishing it costs uplink to tell a ride where a rider <em>was</em>,
/// and, worse, guarantees the backlog never drains: every stalled send used to be followed by ten
/// more sends of history before the current point was reached. A slot cannot fall behind.
/// </para>
/// <para>
/// The two slots are separate rather than one because they are not interchangeable. A fix is
/// repeated seconds later; a privacy crossing is sent once, at the edge of the circle, and
/// coalescing it away would leave a rider hidden — or exposed — for the rest of the ride.
/// </para>
/// </summary>
public sealed class PositionOutbox : IDisposable
{
	private readonly SemaphoreSlim _posted = new(0);
	private readonly object _gate = new();

	private LocationFix? _fix;
	private bool? _privacy;

	/// <summary>Whether a permit is already outstanding, so one post cannot leak several.</summary>
	private bool _signalled;

	private bool _completed;

	/// <summary>
	/// Hands over the newest position, replacing any the sender has not picked up yet.
	/// <para>
	/// Never blocks and never throws — the caller is the fix pump, and the pump's whole job after
	/// this change is to not wait for anything that involves a socket.
	/// </para>
	/// </summary>
	/// <param name="fix">The fix the §4.2 gate let through.</param>
	public void Post(LocationFix fix)
	{
		bool wake;

		lock (_gate)
		{
			if (_completed)
				return;

			_fix = fix;
			wake = !_signalled;
			_signalled = true;
		}

		if (wake)
			_posted.Release();
	}

	/// <summary>
	/// Hands over a private-area crossing (§10.1).
	/// </summary>
	/// <param name="isPrivate">Which way the rider crossed the edge of their own circle.</param>
	public void PostPrivacy(bool isPrivate)
	{
		bool wake;

		lock (_gate)
		{
			if (_completed)
				return;

			_privacy = isPrivate;

			// A rider who has just crossed *into* their circle must not have a position from
			// outside it delivered a moment later. Inline sending got this for free because the
			// fix had already gone by the time the crossing was noticed; a slot has to say it.
			// The direction matters: coming out, a queued fix is exactly what should follow.
			if (isPrivate)
				_fix = null;

			wake = !_signalled;
			_signalled = true;
		}

		if (wake)
			_posted.Release();
	}

	/// <summary>
	/// Closes the outbox: nothing more is accepted, and a sender waiting on
	/// <see cref="TakeAsync"/> is woken so its loop can end.
	/// <para>
	/// What the pump calls when the receiver has stopped. Whatever is already in the slots is still
	/// handed over first — the last thing posted before a stop is usually the privacy crossing that
	/// takes a rider off the map.
	/// </para>
	/// </summary>
	public void Complete()
	{
		bool wake;

		lock (_gate)
		{
			if (_completed)
				return;

			_completed = true;
			wake = !_signalled;
			_signalled = true;
		}

		if (wake)
			_posted.Release();
	}

	/// <summary>
	/// Whether a newer position is already waiting, which makes the one in flight worth nothing.
	/// <para>
	/// The sender reads this between its two transports. One turn can spend a deadline on the hub
	/// and another on REST, and pushing a fix that has already been superseded down the slow path
	/// costs uplink to tell the ride where the rider <em>was</em> — the same backlog this class
	/// exists to prevent, arriving one turn at a time instead of ten.
	/// </para>
	/// </summary>
	public bool HasFixWaiting
	{
		get
		{
			lock (_gate)
			{
				return _fix is not null;
			}
		}
	}

	/// <summary>
	/// Waits for something to send and takes both slots at once.
	/// </summary>
	/// <param name="cancellationToken">Stops the sender.</param>
	/// <returns>
	/// What to send, or an empty batch — <see cref="OutboxBatch.IsEmpty"/> — once the outbox has
	/// been completed and drained. The sender's loop ends on that rather than on an exception.
	/// </returns>
	public async ValueTask<OutboxBatch> TakeAsync(CancellationToken cancellationToken = default)
	{
		while (true)
		{
			lock (_gate)
			{
				OutboxBatch batch = new(_fix, _privacy);

				_fix = null;
				_privacy = null;

				if (!batch.IsEmpty)
					return batch;

				if (_completed)
					return default;

				// Cleared here, inside the lock and immediately before the wait, so a post that
				// lands in the gap still releases a permit and the wait below returns at once.
				_signalled = false;
			}

			await _posted.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	/// <inheritdoc />
	public void Dispose() => _posted.Dispose();
}
