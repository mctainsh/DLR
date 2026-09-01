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
public sealed record PositionEntry(
	int Lat,
	int Lon,
	short? SpeedMps,
	short? HeadingDeg,
	short? AccuracyM,
	DateTimeOffset RecordedUtc)
{
	/// <summary>Builds an entry from a published fix.</summary>
	/// <param name="update">The fix.</param>
	/// <returns>The entry.</returns>
	public static PositionEntry From(PositionUpdate update) => new(
		update.Lat,
		update.Lon,
		update.SpeedMps,
		update.HeadingDeg,
		update.AccuracyM,
		update.RecordedUtc);
}

/// <summary>
/// <strong>The only place a live position ever exists</strong> (§5.5, §10.1).
/// <para>
/// There is no <c>rider_position</c> table behind this and no write-behind in front of it. A fix
/// reaches memory and stops there, which is what lets §10.1 say a live location never touches
/// disk, never enters a backup and cannot survive a restore.
/// </para>
/// <para>
/// <strong>The cost, stated plainly:</strong> a restart loses every pin. Each rider's next push is
/// about five seconds away and puts theirs back, so what a restart actually costs is a few seconds
/// of empty map for riders who are moving, and the rest of the ride for one whose phone has gone
/// quiet - which <c>PinExpiry</c> was hiding from the map anyway (§5.3, §18.6).
/// </para>
/// </summary>
public sealed class RiderPositionCache
{
	private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, PositionEntry>> rides = new();

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

	/// <summary>Every ride currently holding a position - what the broadcast ticks over (§5.3).</summary>
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
	/// values safely. A caller that only wants to know <em>who</em> is out there - the statistics
	/// screen counting distinct riders - would pay for a copy it immediately discards.
	/// </remarks>
	public IEnumerable<Guid> RiderIds(Guid rideId) =>
		rides.TryGetValue(rideId, out ConcurrentDictionary<Guid, PositionEntry>? ride)
			? ride.Keys
			: [];

	/// <summary>Forgets every entry older than <paramref name="floor"/> (§5.6).</summary>
	/// <param name="floor">The oldest fix worth keeping.</param>
	/// <remarks>
	/// A sweep rather than a loop over keys a caller believes are stale, on
	/// <see cref="RemoveRider"/>'s reasoning: the cache is the authority on what it holds.
	/// </remarks>
	public void RemoveOlderThan(DateTimeOffset floor)
	{
		foreach (KeyValuePair<Guid, ConcurrentDictionary<Guid, PositionEntry>> ride in rides)
		{
			foreach (KeyValuePair<Guid, PositionEntry> rider in ride.Value)
			{
				if (rider.Value.RecordedUtc < floor)
				{
					Remove(ride.Key, rider.Key);
				}
			}
		}
	}

	/// <summary>Drops one rider from one ride - sharing off, leaving, removal (§5.6).</summary>
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
	/// Drops one rider from every ride at once - what entering a private area asks for (§10.1).
	/// <para>
	/// A sweep rather than a loop over the rides the caller believes the rider is in. The two lists
	/// can disagree - a ride that ended, a membership that changed while the phone was in a tunnel -
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

	/// <summary>Drops a whole ride - what deleting an adventure takes with it (§5.6).</summary>
	/// <param name="rideId">Which ride.</param>
	public void RemoveRide(Guid rideId) => rides.TryRemove(rideId, out _);
}
