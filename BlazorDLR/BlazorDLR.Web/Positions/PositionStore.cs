using DLR.Core.Contracts.Rides;
using DLR.Server.Data;
using DLR.Server.Data.Positions;
using DLR.Server.Data.Rides;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Positions;

/// <summary>
/// Reading, writing and — the part that matters — <em>deleting</em> live positions (§5.5, §5.6).
/// <para>
/// Every route that ends a rider's participation funnels through <see cref="StopSharingAsync"/>:
/// turning the switch off, leaving, and being removed by the organiser. They are three different
/// user actions with one identical obligation, and giving each its own delete is how one of them
/// eventually stops doing it.
/// </para>
/// <para>
/// Writes land in <see cref="RiderPositionCache"/> and reach PostgreSQL on the next flush (§5.5).
/// <strong>Deletes do not wait for a flush.</strong> A rider who turns sharing off has asked for
/// the row to be gone, and "gone in up to ten seconds" is not what they asked for — so the delete
/// hits the database first and the cache second, and a fix that raced in between is dropped by the
/// cache eviction rather than resurrected by it.
/// </para>
/// </summary>
/// <param name="database">The one context.</param>
/// <param name="cache">The write-behind cache.</param>
public sealed class PositionStore(DlrDbContext database, RiderPositionCache cache)
{
	/// <summary>
	/// Writes a fix into every ride where this rider's own consent flag is set (§5.7).
	/// </summary>
	/// <param name="userId">Whose fix.</param>
	/// <param name="update">The fix.</param>
	/// <returns>The rides it landed in.</returns>
	public async Task<IReadOnlyList<Guid>> PublishAsync(Guid userId, PositionUpdate update)
	{
		// Consent is filtered on the *write* (§5.7). A rider not sharing with a ride has no row
		// in it at all — broadcasting anyway and having the recipients' apps hide the pin would
		// leave the position in the database, in the fan-out and on the wire, three places it has
		// no business being.
		DateTimeOffset now = cache.Clock.GetUtcNow();

		// Live rides, plus rides inside an *unexpired* wind-down (§5.6). The second half is the
		// whole point of the wind-down: members who were sharing keep sharing until they stop or
		// the window closes, so the organiser at home can see that everyone else got home too.
		List<Guid> rideIds = await database
			.Set<GroupRideMember>()
			.Where(member =>
				member.UserId == userId
				&& member.ShareLocation
				&& (member.Ride!.State == GroupRideState.Live
					|| (member.Ride.SharingEndsUtc != null && member.Ride.SharingEndsUtc > now)))
			.Select(member => member.GroupRideId)
			.ToListAsync();

		PositionEntry entry = PositionEntry.From(update, isDirty: true);

		foreach (Guid rideId in rideIds)
		{
			cache.Upsert(rideId, userId, entry);
		}

		return rideIds;
	}

	/// <summary>
	/// Stops a rider sharing with one ride and <strong>deletes the stored position</strong>.
	/// </summary>
	/// <param name="rideId">Which ride.</param>
	/// <param name="userId">Which rider.</param>
	/// <remarks>
	/// Ceasing to update the row would leave a last-known position at rest in the database, which
	/// is precisely what a rider turning sharing off is asking you not to keep (§10.1). The delete
	/// is the feature; the flag is bookkeeping.
	/// </remarks>
	public async Task StopSharingAsync(Guid rideId, Guid userId)
	{
		await database
			.Set<RiderPosition>()
			.Where(position => position.GroupRideId == rideId && position.UserId == userId)
			.ExecuteDeleteAsync();

		cache.Remove(rideId, userId);
	}

	/// <summary>Deletes every position in a ride — the default ending, and cancellation (§5.6).</summary>
	/// <param name="rideId">Which ride.</param>
	public async Task ClearRideAsync(Guid rideId)
	{
		await database
			.Set<RiderPosition>()
			.Where(position => position.GroupRideId == rideId)
			.ExecuteDeleteAsync();

		cache.RemoveRide(rideId);
	}

	/// <summary>The snapshot a reconnecting client fetches instead of replaying history (§5.3).</summary>
	/// <param name="rideId">Which ride.</param>
	/// <returns>Everyone currently sharing, with a position.</returns>
	/// <remarks>
	/// Served from the cache, and gated on <see cref="RiderPositionCache.ReadyAsync"/> so that no
	/// client can observe a half-warm cache and conclude the ride is empty.
	/// </remarks>
	public async Task<IReadOnlyList<RiderPositionDto>> SnapshotAsync(Guid rideId)
	{
		await cache.ReadyAsync();

		IReadOnlyDictionary<Guid, PositionEntry> held = cache.ForRide(rideId);

		if (held.Count == 0)
		{
			return [];
		}

		// Names come from the database because the cache holds positions, not people. Denormalised
		// safely: a username is immutable (§7.2), so a cached one can never go stale.
		Dictionary<Guid, string> names = await database
			.Users
			.AsNoTracking()
			.Where(user => held.Keys.Contains(user.Id))
			.ToDictionaryAsync(user => user.Id, user => user.UserName!);

		return
		[
			.. held
				.Where(rider => names.ContainsKey(rider.Key))
				.Select(rider => new RiderPositionDto(
					rider.Key,
					names[rider.Key],
					rider.Value.Lat,
					rider.Value.Lon,
					rider.Value.SpeedMps,
					rider.Value.HeadingDeg,
					rider.Value.RecordedUtc))
				.OrderBy(position => position.UserName, StringComparer.Ordinal),
		];
	}

	/// <summary>Who has a position, for the member list's <em>no signal</em> state (§5.6).</summary>
	/// <param name="rideId">Which ride.</param>
	/// <returns>The riders currently located.</returns>
	public async Task<IReadOnlySet<Guid>> LocatedAsync(Guid rideId)
	{
		await cache.ReadyAsync();

		return cache.ForRide(rideId).Keys.ToHashSet();
	}
}
