using Microsoft.AspNetCore.SignalR;

namespace DLR.Server.Hubs;

/// <summary>One connection watching one ride, and the account behind it (§5.3, §16.5).</summary>
/// <param name="UserId">Whose connection.</param>
/// <param name="ConnectionId">Which connection.</param>
public readonly record struct RideWatcher(Guid UserId, string ConnectionId);

/// <summary>
/// Which connections each account is holding, and which of them are watching each adventure
/// (§5.2, §5.3, §16.5).
/// <para>
/// <strong>Two jobs, because they need the same bookkeeping.</strong> The first is eviction: taking
/// somebody off an adventure has to take them off its live feed too. The second is addressing: a
/// position batch that has to leave one rider out for one viewer (§16.5) needs that viewer's
/// connections by name, and SignalR groups are write-only - a connection can be added to one or
/// removed from one, never enumerated. Both answers come from knowing which connection belongs to
/// which account on which ride, so they live together rather than in two classes indexing the same
/// churn twice.
/// </para>
/// <para>
/// <strong><see cref="RideHub.JoinRide"/> is a gate, not a standing subscription.</strong> It runs
/// once, at the moment a connection asks to be added to the group, and nothing re-runs it - while
/// SignalR can only remove a <em>connection</em> from a group afterwards, never a user. So without
/// somewhere to look a rider's connection ids up, a member the organiser has just removed keeps
/// receiving every position batch until their connection happens to drop, which on a phone
/// mid-ride is hours. Their REST access ends immediately; this is what ends the rest of it.
/// </para>
/// <para>
/// A singleton because it is live state, and lost on restart for the reason
/// <see cref="Positions.RiderPositionCache"/> is: every connection goes with it, so there is
/// nothing left for it to describe. One process, too - a second instance would evict only from its
/// own connections, which is the per-ride affinity §9.2 already names as the first step in
/// scaling out rather than a new constraint.
/// </para>
/// </summary>
/// <param name="hub">The connections, for the groups an eviction takes them out of.</param>
public sealed class RideConnections(IHubContext<RideHub, IRideClient> hub)
{
	// A lock rather than a dictionary of concurrent sets. Connections churn at app-start rate and
	// not at fix rate, and the concurrent version cannot drop an account's empty bucket without
	// racing its next connect - a slow leak of one entry per account that has ever signed in.
	private readonly Lock _gate = new();

	private readonly Dictionary<Guid, HashSet<string>> _byUser = [];

	// Who is watching each ride, and which rides each connection watches. The second is only there
	// so a disconnect can undo the first without sweeping every ride - a phone dropping its
	// connection is the common case, not a rare one.
	private readonly Dictionary<Guid, Dictionary<string, Guid>> _watchers = [];
	private readonly Dictionary<string, HashSet<Guid>> _watching = new(StringComparer.Ordinal);

	/// <summary>Records a connection.</summary>
	/// <param name="userId">Whose.</param>
	/// <param name="connectionId">Which connection.</param>
	public void Add(Guid userId, string connectionId)
	{
		lock (_gate)
		{
			if (!_byUser.TryGetValue(userId, out HashSet<string>? held))
				_byUser[userId] = held = new HashSet<string>(StringComparer.Ordinal);

			held.Add(connectionId);
		}
	}

	/// <summary>Forgets one, and every ride it was watching.</summary>
	/// <param name="userId">Whose.</param>
	/// <param name="connectionId">Which connection.</param>
	public void Remove(Guid userId, string connectionId)
	{
		lock (_gate)
		{
			if (_byUser.TryGetValue(userId, out HashSet<string>? held)
				&& held.Remove(connectionId)
				&& held.Count == 0)
			{
				_byUser.Remove(userId);
			}

			if (_watching.Remove(connectionId, out HashSet<Guid>? rides))
			{
				foreach (Guid rideId in rides)
					DropWatcher(rideId, connectionId);
			}
		}
	}

	/// <summary>
	/// Records that a connection is now receiving a ride's position batches (§5.3).
	/// <para>
	/// SignalR can name a group but never enumerate one, and a batch that has to leave out one
	/// rider's pin for one viewer has to be addressed to that viewer's connections by hand
	/// (<see cref="RideBroadcastService"/>). This is the list that makes that addressable - and it
	/// carries the account behind each connection, because the question asked of it is "who is
	/// watching", not "what is connected".
	/// </para>
	/// </summary>
	/// <param name="rideId">Which ride's group the connection just entered.</param>
	/// <param name="userId">Whose connection.</param>
	/// <param name="connectionId">Which connection.</param>
	public void Watch(Guid rideId, Guid userId, string connectionId)
	{
		lock (_gate)
		{
			if (!_watchers.TryGetValue(rideId, out Dictionary<string, Guid>? held))
				_watchers[rideId] = held = new Dictionary<string, Guid>(StringComparer.Ordinal);

			held[connectionId] = userId;

			if (!_watching.TryGetValue(connectionId, out HashSet<Guid>? rides))
				_watching[connectionId] = rides = [];

			rides.Add(rideId);
		}
	}

	/// <summary>Forgets one connection's interest in one ride.</summary>
	/// <param name="rideId">Which ride.</param>
	/// <param name="connectionId">Which connection.</param>
	public void Unwatch(Guid rideId, string connectionId)
	{
		lock (_gate)
		{
			DropWatcher(rideId, connectionId);

			if (_watching.TryGetValue(connectionId, out HashSet<Guid>? rides)
				&& rides.Remove(rideId)
				&& rides.Count == 0)
			{
				_watching.Remove(connectionId);
			}
		}
	}

	/// <summary>
	/// Every connection currently receiving a ride's batches, with the account behind it.
	/// </summary>
	/// <param name="rideId">Which ride.</param>
	/// <returns>A snapshot, safe to iterate while connections churn.</returns>
	public IReadOnlyList<RideWatcher> WatchersIn(Guid rideId)
	{
		lock (_gate)
		{
			return _watchers.TryGetValue(rideId, out Dictionary<string, Guid>? held)
				? [.. held.Select(watcher => new RideWatcher(watcher.Value, watcher.Key))]
				: [];
		}
	}

	private void DropWatcher(Guid rideId, string connectionId)
	{
		if (_watchers.TryGetValue(rideId, out Dictionary<string, Guid>? held)
			&& held.Remove(connectionId)
			&& held.Count == 0)
		{
			_watchers.Remove(rideId);
		}
	}

	/// <summary>
	/// Takes every connection this account holds out of an adventure's two groups (§5.2).
	/// </summary>
	/// <param name="rideId">Which adventure.</param>
	/// <param name="userId">Whose connections.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <remarks>
	/// Called after the membership row is committed, never before - evicting somebody whose
	/// removal then fails would cut them off a ride they are still on, and only a reconnect would
	/// put them back.
	/// </remarks>
	public async Task EvictAsync(Guid rideId, Guid userId, CancellationToken cancellationToken = default)
	{
		string[] held;

		// Copied out rather than iterated under the lock: the group call is async, and holding a
		// lock across it would serialise every connect and disconnect behind one removal.
		lock (_gate)
		{
			held = _byUser.TryGetValue(userId, out HashSet<string>? connections) ? [.. connections] : [];
		}

		foreach (string connectionId in held)
		{
			Unwatch(rideId, connectionId);

			await RideHub.LeaveGroupsAsync(hub.Groups, connectionId, rideId, cancellationToken);
		}
	}
}
