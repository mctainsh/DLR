using DLR.Core.Contracts.Rides;
using DLR.Server.Data;
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
/// Every ride the fix was written to - the rides where this rider's own consent flag is set. An
/// empty list is the correct answer for a rider who is sharing with nobody, not an error.
/// </param>
/// <param name="LeftPrivateArea">
/// Whether this fix also ended a spell of privacy: the rider was marked private and a coordinate
/// has now proved otherwise.
/// </param>
public sealed record PositionPublication(IReadOnlyList<Guid> RideIds, bool LeftPrivateArea);

/// <summary>
/// Reading, writing and - the part that matters - <em>deleting</em> live positions (§5.5, §5.6).
/// <para>
/// Every route that ends a rider's participation funnels through <see cref="StopSharing"/>:
/// turning the switch off, leaving, and being removed by the organiser. They are three different
/// user actions with one identical obligation, and giving each its own delete is how one of them
/// eventually stops doing it.
/// </para>
/// <para>
/// A position lives in <see cref="RiderPositionCache"/> and nowhere else (§5.5), so a delete here
/// is immediate and total - there is no write behind it to outrun, and nothing left on disk to
/// reclaim later. The database this still holds is for <em>membership</em>: which rides a fix may
/// land in, and what the riders in one are called.
/// </para>
/// </summary>
/// <param name="database">The one context - read for membership and names, never for positions.</param>
/// <param name="cache">Where every live position is.</param>
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
		// in it at all - broadcasting anyway and having the recipients' apps hide the pin would
		// leave the position in the database, in the fan-out and on the wire, three places it has
		// no business being.
		IReadOnlyList<Guid> rideIds = await SharedRideIdsAsync(userId);

		// A coordinate arriving is proof the rider is outside their circle, so it clears the flag
		// whether or not the device remembered to say so (§10.1). The device does say so, and this
		// is what makes a lost "no longer private" call cost one tick instead of the rest of the
		// ride: the ways of being wrong are not equal, and the one that leaves somebody hidden
		// while their pin moves is the one that reads as a broken app.
		bool leftPrivateArea = privacy.Set(userId, isPrivate: false);

		PositionEntry entry = PositionEntry.From(update);

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
	/// Takes a rider off every map they are on, or puts them back - the private area, as the rest of
	/// the ride sees it (§10.1, §5.6).
	/// </summary>
	/// <param name="userId">Which rider.</param>
	/// <param name="isPrivate">True on the way into the circle, false on the way out.</param>
	/// <returns>
	/// The rides to announce the change to, or an empty list when nothing changed - a device
	/// re-stating what the server already believes must not put a message on every connection.
	/// </returns>
	/// <remarks>
	/// Going private <strong>deletes</strong>, on <see cref="StopSharing"/>'s reasoning and for a
	/// sharper reason. Ceasing to update would leave the last fix before the driveway on every
	/// other rider's map - a pin that has stopped moving a few streets from somebody's house is a
	/// better clue to where they live than most of what this feature withholds.
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
	/// Ceasing to update the entry would leave a last-known position at rest, which is precisely
	/// what a rider turning sharing off is asking you not to keep (§10.1). The delete is the
	/// feature; the flag is bookkeeping.
	/// </remarks>
	public void StopSharing(Guid rideId, Guid userId) => cache.Remove(rideId, userId);

	/// <summary>Deletes every position in an adventure - what deleting one takes with it (§5.6).</summary>
	/// <param name="rideId">Which ride.</param>
	public void ClearRide(Guid rideId) => cache.RemoveRide(rideId);

	/// <summary>
	/// Forgets every position with no fix since <paramref name="floor"/> (§7.11).
	/// </summary>
	/// <param name="floor">The oldest fix worth keeping.</param>
	/// <remarks>
	/// A memory bound rather than a retention rule - nothing is retained. A rider whose phone went
	/// quiet holds an entry nothing else reclaims, and this is what stops a year of them
	/// accumulating in a process that has been up that long.
	/// </remarks>
	public void ClearIdle(DateTimeOffset floor) => cache.RemoveOlderThan(floor);

	/// <summary>The snapshot a reconnecting client fetches instead of replaying history (§5.3).</summary>
	/// <param name="rideId">Which ride.</param>
	/// <returns>Everyone currently sharing, with a position.</returns>
	/// <remarks>
	/// Served from the cache, which is the only place a position is (§5.5). There is nothing to
	/// wait for: an empty answer means the ride is empty, not that a warm-up is still running.
	/// </remarks>
	public async Task<IReadOnlyList<RiderPositionDto>> SnapshotAsync(Guid rideId)
	{
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
	public IReadOnlySet<Guid> Located(Guid rideId) => cache.RiderIds(rideId).ToHashSet();
}
