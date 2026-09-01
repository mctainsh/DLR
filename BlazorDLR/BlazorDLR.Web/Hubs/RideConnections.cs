using Microsoft.AspNetCore.SignalR;

namespace DLR.Server.Hubs;

/// <summary>
/// Which connections each account is holding, so that taking somebody off an adventure also takes
/// them off its live feed (§5.2, §5.3).
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

	/// <summary>Forgets one.</summary>
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
			await RideHub.LeaveGroupsAsync(hub.Groups, connectionId, rideId, cancellationToken);
		}
	}
}
