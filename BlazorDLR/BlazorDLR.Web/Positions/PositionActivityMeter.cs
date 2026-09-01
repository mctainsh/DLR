using System.Collections.Concurrent;

namespace DLR.Server.Positions;

/// <summary>
/// Counts GPS fixes as they arrive - once per rider for the lifetime total, and once per minute
/// for the administration screen's graph (§5.5).
/// <para>
/// <strong>Here rather than over the stored rows, because the stored rows do not last.</strong>
/// A position is deleted as soon as the ride carrying it stops being live, so neither number can
/// be recovered afterwards by counting <c>rider_position</c>: that table answers "what is on a map
/// right now", and both of these questions are about what happened.
/// </para>
/// <para>
/// <strong>Nothing here touches the database.</strong> The publish path is the hot one - 500
/// riders at a fix a second - and a counter that took a row lock per fix would cost more than the
/// position write it is counting. Per-rider totals accumulate here and are drained by the flush
/// service, which already has a scope, a connection and a batching statement; see
/// <see cref="DrainRiderCounts"/>.
/// </para>
/// <para>
/// The graph is this process's own count and starts empty when it restarts. That is stated on the
/// wire - <see cref="StartedUtc"/> travels with the numbers - because a graph climbing out of a
/// restart and a service that lost half its riders look identical otherwise.
/// </para>
/// </summary>
/// <param name="clock">The clock every bucket is stamped against (§10.4).</param>
public sealed class PositionActivityMeter(TimeProvider clock)
{
	/// <summary>
	/// How many minutes the graph covers. A day, because that is the window the screen asks for
	/// and 1 440 <see cref="int"/>s is 6 KB - small enough that there is no reason to keep less.
	/// </summary>
	public const int WindowMinutes = 24 * 60;

	/// <summary>
	/// Counts per slot, and the absolute minute each slot currently holds.
	/// <para>
	/// A ring over time rather than a queue: the slot for a minute is that minute modulo the
	/// window, and the stamp beside it says which minute is actually in there. A slot whose stamp
	/// has fallen out of the window reads as zero and is overwritten on its next write, so nothing
	/// ever has to be swept and a server that went quiet for six hours does not report the counts
	/// it had six hours ago.
	/// </para>
	/// </summary>
	private readonly int[] _counts = new int[WindowMinutes];

	private readonly long[] _stamps = new long[WindowMinutes];

	private readonly Lock _gate = new();

	/// <summary>
	/// Fixes seen per rider since the last drain.
	/// <para>
	/// Concurrent rather than under <see cref="_gate"/>: the two are written on the same call but
	/// contend differently - every fix touches one bucket and one rider, and riders are spread
	/// across the dictionary while the bucket is a single slot every fix in a minute shares.
	/// </para>
	/// </summary>
	private ConcurrentDictionary<Guid, long> _riders = new();

	/// <summary>Handed out for a drain that found nothing, so an idle tick allocates nothing.</summary>
	private static readonly Dictionary<Guid, long> Empty = [];

	/// <summary>When this process began counting. Travels with the graph - see the type's note.</summary>
	public DateTimeOffset StartedUtc { get; } = clock.GetUtcNow();

	/// <summary>
	/// Records one accepted fix.
	/// <para>
	/// One call per fix, not per ride it lands in. A rider sharing with three adventures publishes
	/// one position and three rows are written from it; counting the rows would say that rider
	/// rode three times as far as the one beside them who joined a single ride.
	/// </para>
	/// </summary>
	/// <param name="userId">Whose fix.</param>
	public void Record(Guid userId)
	{
		_riders.AddOrUpdate(userId, 1, static (_, running) => running + 1);

		long minute = Minute(clock.GetUtcNow());
		int slot = Slot(minute);

		lock (_gate)
		{
			if (_stamps[slot] != minute)
			{
				// The slot is holding some minute a day ago. Claim it rather than adding to it.
				_stamps[slot] = minute;
				_counts[slot] = 0;
			}

			_counts[slot]++;
		}
	}

	/// <summary>
	/// Takes everything counted per rider since the last call and clears it.
	/// <para>
	/// Swaps the dictionary rather than walking it, so a fix arriving mid-drain lands in the new
	/// one and is counted next time rather than being dropped or double-counted.
	/// </para>
	/// </summary>
	/// <returns>Fixes per rider, or an empty dictionary when nothing has arrived.</returns>
	/// <remarks>
	/// The caller owns what it is handed and must persist it or put it back: these counts exist
	/// nowhere else the moment this returns. <c>PositionFlushService</c> restores them on a failed
	/// write for exactly that reason.
	/// </remarks>
	public IReadOnlyDictionary<Guid, long> DrainRiderCounts() =>
		// Nothing arrived, so nothing is swapped. The flush asks every tick forever, and a quiet
		// server allocating a fresh dictionary every ten seconds is the cost its own early return
		// exists to avoid.
		_riders.IsEmpty
			? Empty
			: Interlocked.Exchange(ref _riders, new ConcurrentDictionary<Guid, long>());

	/// <summary>
	/// Puts drained counts back after a write that did not land.
	/// </summary>
	/// <param name="counts">What <see cref="DrainRiderCounts"/> handed out.</param>
	public void Restore(IReadOnlyDictionary<Guid, long> counts)
	{
		foreach ((Guid userId, long count) in counts)
		{
			_riders.AddOrUpdate(userId, count, (_, running) => running + count);
		}
	}

	/// <summary>
	/// The last day of per-minute counts, oldest first.
	/// </summary>
	/// <param name="windowStartUtc">The minute the first entry covers.</param>
	/// <returns>Exactly <see cref="WindowMinutes"/> counts, zero for every minute nothing arrived.</returns>
	public IReadOnlyList<int> PerMinute(out DateTimeOffset windowStartUtc)
	{
		DateTimeOffset now = clock.GetUtcNow();
		long current = Minute(now);

		// The window ends at the minute in progress, so the last column of the graph is the one
		// filling up now rather than a minute of history that happens to be complete.
		long first = current - WindowMinutes + 1;

		windowStartUtc = DateTimeOffset.FromUnixTimeSeconds(first * 60);

		int[] window = new int[WindowMinutes];

		lock (_gate)
		{
			for (int offset = 0; offset < WindowMinutes; offset++)
			{
				long minute = first + offset;
				int slot = Slot(minute);

				// The stamp is what makes an untouched slot a zero rather than yesterday's count.
				window[offset] = _stamps[slot] == minute ? _counts[slot] : 0;
			}
		}

		return window;
	}

	/// <summary>Whole minutes since the epoch - the graph's unit, and the ring's key.</summary>
	private static long Minute(DateTimeOffset instant) => instant.ToUnixTimeSeconds() / 60;

	/// <summary>
	/// Which slot a minute lives in. Spelled once, because the writer and the reader addressing
	/// different slots would be a silently wrong graph rather than a failure.
	/// </summary>
	/// <param name="minute">Whole minutes since the epoch, never negative for a clock's "now".</param>
	/// <returns>An index into the ring.</returns>
	private static int Slot(long minute) => (int)(minute % WindowMinutes);
}
