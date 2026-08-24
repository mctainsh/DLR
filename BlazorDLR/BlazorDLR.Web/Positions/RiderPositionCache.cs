using System.Collections.Concurrent;
using DLR.Core.Contracts.Rides;

namespace DLR.Server.Positions;

/// <summary>One rider's last known position, as the cache holds it (§5.5).</summary>
/// <param name="Lat">Latitude, scaled by 1e5.</param>
/// <param name="Lon">Longitude, scaled by 1e5.</param>
/// <param name="SpeedMps">Metres per second, when known.</param>
/// <param name="HeadingDeg">Degrees from north, when known.</param>
/// <param name="AccuracyM">Reported accuracy in metres, when known.</param>
/// <param name="RecordedUtc">When the device took the fix.</param>
/// <param name="IsDirty">Whether it still needs writing to PostgreSQL.</param>
public sealed record PositionEntry(
	int Lat,
	int Lon,
	short? SpeedMps,
	short? HeadingDeg,
	short? AccuracyM,
	DateTimeOffset RecordedUtc,
	bool IsDirty)
{
	/// <summary>Builds an entry from a published fix.</summary>
	/// <param name="update">The fix.</param>
	/// <param name="isDirty">Whether it needs writing.</param>
	/// <returns>The entry.</returns>
	public static PositionEntry From(PositionUpdate update, bool isDirty) => new(
		update.Lat,
		update.Lon,
		update.SpeedMps,
		update.HeadingDeg,
		update.AccuracyM,
		update.RecordedUtc,
		isDirty);
}

/// <summary>
/// Live positions in memory, written behind to PostgreSQL every 10 s (§5.5).
/// <para>
/// The cache is the read path for fan-out; the database is durability, so that a restarted process
/// rehydrates a warm cache instead of showing a blank map until every rider's next push. A hard
/// process kill therefore loses up to 10 s of movement, which on restart shows as a pin that lags
/// for a few seconds and self-corrects.
/// </para>
/// </summary>
/// <param name="clock">The project clock; nothing here reads an ambient one (§10.4).</param>
public sealed class RiderPositionCache(TimeProvider clock)
{
	private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, PositionEntry>> rides = new();

	private readonly TaskCompletionSource ready =
		new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Unused for now; the wind-down sweep in SRV-25 reads it.</summary>
	public TimeProvider Clock { get; } = clock;

	/// <summary>
	/// Completes when startup rehydration has finished (§5.5).
	/// <para>
	/// <strong>The gate lives inside the cache</strong> rather than relying on hosted-service
	/// ordering, because Kestrel's <c>GenericWebHostService</c> can begin serving requests before
	/// custom hosted services have run — so a hub read or a snapshot request can genuinely arrive
	/// against a half-warm cache, and would answer "nobody is on this ride" with total confidence.
	/// </para>
	/// </summary>
	/// <returns>A task that completes once the cache is warm.</returns>
	public Task ReadyAsync() => ready.Task;

	/// <summary>Marks rehydration finished. Idempotent, so a retried rehydrator cannot deadlock.</summary>
	public void MarkReady() => ready.TrySetResult();

	/// <summary>
	/// Records a fix, unless an equal or newer one is already held.
	/// </summary>
	/// <param name="rideId">Which ride.</param>
	/// <param name="userId">Which rider.</param>
	/// <param name="entry">The fix.</param>
	/// <returns>True when the cache changed.</returns>
	/// <remarks>
	/// The comparison is on <see cref="PositionEntry.RecordedUtc"/>, not on arrival order: batches
	/// retry, connections reorder, and a rider whose pin jumps backwards is worse than one whose
	/// pin is briefly stale.
	/// </remarks>
	public bool Upsert(Guid rideId, Guid userId, PositionEntry entry)
	{
		ConcurrentDictionary<Guid, PositionEntry> ride =
			rides.GetOrAdd(rideId, _ => new ConcurrentDictionary<Guid, PositionEntry>());

		while (true)
		{
			if (!ride.TryGetValue(userId, out PositionEntry? existing))
			{
				if (ride.TryAdd(userId, entry))
				{
					return true;
				}

				continue;
			}

			if (entry.RecordedUtc <= existing.RecordedUtc)
			{
				return false;
			}

			// Compare-and-swap rather than an assignment. Two riders' publishes never contend,
			// but one rider's client retrying while a newer fix lands does, and a plain write
			// would let the loser overwrite the winner.
			if (ride.TryUpdate(userId, entry, existing))
			{
				return true;
			}
		}
	}

	/// <summary>Every ride currently holding a position — what the broadcast ticks over (§5.3).</summary>
	public IReadOnlyList<Guid> RideIds() =>
	[
		.. rides.Where(ride => !ride.Value.IsEmpty).Select(ride => ride.Key),
	];

	/// <summary>Everything currently held for a ride.</summary>
	/// <param name="rideId">Which ride.</param>
	/// <returns>The riders and their positions.</returns>
	public IReadOnlyDictionary<Guid, PositionEntry> ForRide(Guid rideId) =>
		rides.TryGetValue(rideId, out ConcurrentDictionary<Guid, PositionEntry>? ride)
			? ride.ToDictionary()
			: new Dictionary<Guid, PositionEntry>();

	/// <summary>Who currently has a position in a ride, without copying what they are.</summary>
	/// <param name="rideId">Which ride.</param>
	/// <returns>The rider ids, or empty when the ride holds nothing.</returns>
	/// <remarks>
	/// <see cref="ForRide"/> snapshots every <see cref="PositionEntry"/> so a caller can read the
	/// values safely. A caller that only wants to know <em>who</em> is out there — the statistics
	/// screen counting distinct riders — would pay for a copy it immediately discards.
	/// </remarks>
	public IEnumerable<Guid> RiderIds(Guid rideId) =>
		rides.TryGetValue(rideId, out ConcurrentDictionary<Guid, PositionEntry>? ride)
			? ride.Keys
			: [];

	/// <summary>Every dirty entry, as the flush wants them (§5.5).</summary>
	/// <returns>The rows needing a write.</returns>
	public IReadOnlyList<DirtyPosition> Dirty() =>
	[
		.. rides.SelectMany(ride => ride.Value
			.Where(rider => rider.Value.IsDirty)
			.Select(rider => new DirtyPosition(ride.Key, rider.Key, rider.Value))),
	];

	/// <summary>
	/// Marks an entry clean, unless it changed while the write was in flight.
	/// </summary>
	/// <param name="position">What was written.</param>
	/// <remarks>
	/// Compared by value: if a newer fix arrived during the round trip, the entry is a different
	/// object and stays dirty, so the next flush picks it up rather than the newer position being
	/// silently dropped.
	/// </remarks>
	public void MarkClean(DirtyPosition position)
	{
		if (rides.TryGetValue(position.RideId, out ConcurrentDictionary<Guid, PositionEntry>? ride))
		{
			ride.TryUpdate(position.UserId, position.Entry with { IsDirty = false }, position.Entry);
		}
	}

	/// <summary>Drops one rider from one ride — sharing off, leaving, removal (§5.6).</summary>
	/// <param name="rideId">Which ride.</param>
	/// <param name="userId">Which rider.</param>
	public void Remove(Guid rideId, Guid userId)
	{
		if (rides.TryGetValue(rideId, out ConcurrentDictionary<Guid, PositionEntry>? ride))
		{
			ride.TryRemove(userId, out _);
		}
	}

	/// <summary>
	/// Drops one rider from every ride at once — what entering a private area asks for (§10.1).
	/// <para>
	/// A sweep rather than a loop over the rides the caller believes the rider is in. The two lists
	/// can disagree — a ride that ended, a membership that changed while the phone was in a tunnel —
	/// and the direction the disagreement must not go is "a position left behind in a ride nobody
	/// remembered to ask about".
	/// </para>
	/// </summary>
	/// <param name="userId">Which rider.</param>
	/// <returns>The rides a position was actually removed from, for the caller to announce to.</returns>
	public IReadOnlyList<Guid> RemoveRider(Guid userId) =>
	[
		.. rides.Where(ride => ride.Value.TryRemove(userId, out _)).Select(ride => ride.Key),
	];

	/// <summary>Drops a whole ride — the default ending, and cancellation (§5.6).</summary>
	/// <param name="rideId">Which ride.</param>
	public void RemoveRide(Guid rideId) => rides.TryRemove(rideId, out _);
}

/// <summary>An entry waiting to be written (§5.5).</summary>
/// <param name="RideId">Which ride.</param>
/// <param name="UserId">Which rider.</param>
/// <param name="Entry">The position.</param>
public sealed record DirtyPosition(Guid RideId, Guid UserId, PositionEntry Entry);
