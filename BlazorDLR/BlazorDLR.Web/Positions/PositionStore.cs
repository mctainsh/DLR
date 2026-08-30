using DLR.Core.Contracts.Rides;
using DLR.Server.Data;
using DLR.Server.Data.Positions;
using DLR.Server.Data.Rides;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Positions;

/// <summary>
/// What one publish did (§5.7, §10.1).
/// <para>
/// Two facts rather than one because they fan out on different channels: the rides tell the caller
/// nothing it has to send, while <paramref name="LeftPrivateArea"/> is a member-list change every
/// connection on those rides has to be told about. Returning it here rather than announcing it
/// inside the store keeps the hub out of the write path, the way every other broadcast in this
/// server is done from its endpoint.
/// </para>
/// </summary>
/// <param name="RideIds">
/// Every ride the fix was written to — the rides where this rider's own consent flag is set. An
/// empty list is the correct answer for a rider who is sharing with nobody, not an error.
/// </param>
/// <param name="LeftPrivateArea">
/// Whether this fix also ended a spell of privacy: the rider was marked private and a coordinate
/// has now proved otherwise.
/// </param>
public sealed record PositionPublication(IReadOnlyList<Guid> RideIds, bool LeftPrivateArea);

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
/// <param name="privacy">Who is inside their own private area right now (§10.1).</param>
public sealed class PositionStore(
	DlrDbContext database,
	RiderPositionCache cache,
	RiderPrivacyCache privacy,
	PositionActivityMeter meter)
{
	/// <summary>
	/// Writes a fix into every ride where this rider's own consent flag is set (§5.7).
	/// </summary>
	/// <param name="userId">Whose fix.</param>
	/// <param name="update">The fix.</param>
	/// <returns>Where it landed, and whether it also ended a spell of privacy.</returns>
	public async Task<PositionPublication> PublishAsync(Guid userId, PositionUpdate update)
	{
		// Consent is filtered on the *write* (§5.7). A rider not sharing with a ride has no row
		// in it at all — broadcasting anyway and having the recipients' apps hide the pin would
		// leave the position in the database, in the fan-out and on the wire, three places it has
		// no business being.
		IReadOnlyList<Guid> rideIds = await SharedRideIdsAsync(userId);

		// A coordinate arriving is proof the rider is outside their circle, so it clears the flag
		// whether or not the device remembered to say so (§10.1). The device does say so, and this
		// is what makes a lost "no longer private" call cost one tick instead of the rest of the
		// ride: the ways of being wrong are not equal, and the one that leaves somebody hidden
		// while their pin moves is the one that reads as a broken app.
		bool leftPrivateArea = privacy.Set(userId, isPrivate: false);

		PositionEntry entry = PositionEntry.From(update, isDirty: true);

		foreach (Guid rideId in rideIds)
		{
			cache.Upsert(rideId, userId, entry);
		}

		// Counted here rather than at the flush, and once rather than once per ride. The flush
		// upserts one row per rider per ride and coalesces a whole period of movement into it, so
		// counting there would report a rider publishing at 1 Hz as one fix every ten seconds; and
		// counting per ride would say a rider who joined three adventures rode three times as far.
		// This is the one place a fix arrives (§5.7), which is what makes it the place to count.
		meter.Record(userId);

		return new PositionPublication(rideIds, leftPrivateArea);
	}

	/// <summary>
	/// Takes a rider off every map they are on, or puts them back — the private area, as the rest of
	/// the ride sees it (§10.1, §5.6).
	/// </summary>
	/// <param name="userId">Which rider.</param>
	/// <param name="isPrivate">True on the way into the circle, false on the way out.</param>
	/// <returns>
	/// The rides to announce the change to, or an empty list when nothing changed — a device
	/// re-stating what the server already believes must not put a message on every connection.
	/// </returns>
	/// <remarks>
	/// Going private <strong>deletes</strong>, on <see cref="StopSharingAsync"/>'s reasoning and for
	/// a sharper reason. Ceasing to update would leave the last fix before the driveway sitting in
	/// the database and on every other rider's map — a pin that has stopped moving a few streets from
	/// somebody's house is a better clue to where they live than most of what this feature withholds.
	/// <para>
	/// The sharing flag is untouched: this is a place, not a decision about the ride, and the rider
	/// goes back on the map by riding out of the circle rather than by finding a switch.
	/// </para>
	/// </remarks>
	public async Task<IReadOnlyList<Guid>> SetPrivateAsync(Guid userId, bool isPrivate)
	{
		if (!privacy.Set(userId, isPrivate))
		{
			return [];
		}

		if (!isPrivate)
		{
			// Coming out is only the flag: the next fix is a second away and puts the pin back. There
			// is nothing to restore, because nothing was kept.
			return await SharedRideIdsAsync(userId);
		}

		await database
			.Set<RiderPosition>()
			.Where(position => position.UserId == userId)
			.ExecuteDeleteAsync();

		// The union of "rides the cache had them in" and "rides they are sharing with". The two can
		// disagree, and both halves matter: the first is where a stale pin would otherwise be left,
		// the second is where a member list needs to hear that the empty row is privacy rather than
		// a tunnel.
		HashSet<Guid> announce = [.. cache.RemoveRider(userId)];
		announce.UnionWith(await SharedRideIdsAsync(userId));

		return [.. announce];
	}

	/// <summary>Who is currently private, for the member list (§5.2).</summary>
	/// <returns>The riders inside their own private area.</returns>
	public IReadOnlySet<Guid> PrivateRiders() => privacy.Everyone();

	/// <summary>The rides a fix from this rider would currently land in (§5.7).</summary>
	private async Task<IReadOnlyList<Guid>> SharedRideIdsAsync(Guid userId) =>
		await database
			.Set<GroupRideMember>()
			.Where(member => member.UserId == userId && member.ShareLocation)
			.Select(member => member.GroupRideId)
			.ToListAsync();

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

	/// <summary>
	/// Deletes every position with no fix since <paramref name="floor"/>, and switches those
	/// riders' sharing off (§5.6, §7.11).
	/// </summary>
	/// <param name="floor">The oldest fix worth keeping.</param>
	/// <returns>How many rows went.</returns>
	/// <remarks>
	/// The fourth route that ends a rider's participation, and it lives here with the other three
	/// for the reason this type exists. It is set-based rather than a loop over
	/// <see cref="StopSharingAsync"/> because it is a backstop over the whole table, but the
	/// obligation is the same one and is stated once.
	/// </remarks>
	public async Task<int> ClearIdleAsync(DateTimeOffset floor)
	{
		await database
			.Set<GroupRideMember>()
			.Where(member => member.ShareLocation && database
				.Set<RiderPosition>()
				.Any(position => position.GroupRideId == member.GroupRideId
					&& position.UserId == member.UserId
					&& position.RecordedUtc < floor))
			.ExecuteUpdateAsync(row => row.SetProperty(member => member.ShareLocation, false));

		int deleted = await database
			.Set<RiderPosition>()
			.Where(position => position.RecordedUtc < floor)
			.ExecuteDeleteAsync();

		// The eviction is what stops a flush already in flight writing the row straight back —
		// the flush reads the cache and never the sharing flag, so clearing the flag first would
		// not have prevented it. Same delete-then-evict pair as StopSharingAsync.
		cache.RemoveOlderThan(floor);

		return deleted;
	}

	/// <summary>
	/// Deletes positions belonging to a rider who is not sharing with that adventure — including
	/// one who is no longer a member of it at all (§10.1, §7.11).
	/// </summary>
	/// <returns>How many rows went. <strong>Any number but zero is a defect having fired.</strong></returns>
	/// <remarks>
	/// A position is only ever written for a member whose <c>ShareLocation</c> is set — the filter
	/// is on the write, in <see cref="PublishAsync"/> — so a row that fails that test is not a
	/// stale row, it is a row that should not exist. §13 Q29 is the way it happens: a flush that
	/// snapshotted its batch before a concurrent delete completes its write afterwards and puts
	/// the row back. This is the backstop, not the fix; the fix is a tombstone the flush filters
	/// against, or a membership join in the upsert.
	/// <para>
	/// <c>rider_position</c> has <strong>no foreign key to <c>group_ride_member</c></strong>
	/// (§5.6), so a member row going away cascades nothing and the second arm below is reachable
	/// on its own.
	/// </para>
	/// </remarks>
	public async Task<int> ClearOrphanedAsync()
	{
		List<OrphanedPosition> orphans = await OrphanedQuery()
			.AsNoTracking()
			.Select(position => new OrphanedPosition(position.GroupRideId, position.UserId))
			.ToListAsync();

		if (orphans.Count == 0)
		{
			return 0;
		}

		int deleted = await OrphanedQuery().ExecuteDeleteAsync();

		// Materialised rather than swept by age like ClearIdleAsync, because this set is expected
		// to be empty: paying for the keys is what lets the cache be evicted precisely.
		foreach (OrphanedPosition orphan in orphans)
		{
			cache.Remove(orphan.RideId, orphan.UserId);
		}

		return deleted;
	}

	/// <summary>Counts what <see cref="ClearOrphanedAsync"/> would take, for a dry run.</summary>
	public Task<int> CountOrphanedAsync() => OrphanedQuery().CountAsync();

	private IQueryable<RiderPosition> OrphanedQuery() =>
		database
			.Set<RiderPosition>()
			.Where(position => !database
				.Set<GroupRideMember>()
				.Any(member => member.GroupRideId == position.GroupRideId
					&& member.UserId == position.UserId
					&& member.ShareLocation));

	/// <summary>One orphaned row's cache key.</summary>
	private sealed record OrphanedPosition(Guid RideId, Guid UserId);

	/// <summary>Counts what <see cref="ClearIdleAsync"/> would take, for a dry run (§7.11).</summary>
	/// <param name="floor">The oldest fix worth keeping.</param>
	public Task<int> CountIdleAsync(DateTimeOffset floor) =>
		database
			.Set<RiderPosition>()
			.CountAsync(position => position.RecordedUtc < floor);

	/// <summary>Deletes every position in an adventure — what deleting one takes with it (§5.6).</summary>
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
