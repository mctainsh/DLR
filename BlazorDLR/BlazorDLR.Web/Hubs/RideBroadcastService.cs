using DLR.Core.Contracts.Rides;
using DLR.Server.Moderation;
using DLR.Server.Positions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace DLR.Server.Hubs;

/// <summary>
/// One batch per ride per 5 s (§5.3).
/// <para>
/// The alternative - relaying each fix as it arrives - is <c>n × n</c> messages per tick, so a
/// fifty-rider ride would send 2,500 messages every five seconds instead of fifty. Batching is
/// what makes the live map affordable on the €4 VPS, and it is why the cache exists at all.
/// </para>
/// <para>
/// A separate cadence from the flush (§5.5), and they must not be conflated: this one is network
/// fan-out, that one is durability.
/// </para>
/// </summary>
/// <param name="cache">Where the positions are.</param>
/// <param name="hub">The connections.</param>
/// <param name="clock">Drives the timer (§10.4).</param>
/// <param name="options">The period.</param>
/// <param name="blocks">Who may not see whom (§16.5) - in memory, because this path takes no query.</param>
/// <param name="connections">Who is watching each ride, for the viewers who need their own copy.</param>
/// <param name="logger">Where a failed send is recorded.</param>
public sealed class RideBroadcastService(
	RiderPositionCache cache,
	IHubContext<RideHub, IRideClient> hub,
	TimeProvider clock,
	IOptions<RideOptions> options,
	BlockCache blocks,
	RideConnections connections,
	ILogger<RideBroadcastService> logger) : BackgroundService
{
	/// <inheritdoc />
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		TimeSpan period = TimeSpan.FromSeconds(Math.Max(1, options.Value.BroadcastSeconds));

		using PeriodicTimer timer = new(period, clock);

		try
		{
			while (await timer.WaitForNextTickAsync(stoppingToken))
			{
				await BroadcastAsync(stoppingToken);
			}
		}
		catch (OperationCanceledException)
		{
			// Shutdown. Nothing to drain - a missed batch is a skipped frame, not data loss.
		}
	}

	/// <summary>Sends one batch to every ride that has positions. Public so tests drive a tick.</summary>
	/// <param name="cancellationToken">Abandons the send.</param>
	public async Task BroadcastAsync(CancellationToken cancellationToken)
	{
		// Reads only what the cache already holds. The broadcast path never touches the database:
		// that is the entire point of the write-behind, and a query here would put one on the hot
		// path for every ride every five seconds.
		foreach (Guid rideId in cache.RideIds())
		{
			IReadOnlyDictionary<Guid, PositionEntry> held = cache.ForRide(rideId);

			if (held.Count == 0)
			{
				continue;
			}

			PositionFix[] fixes =
			[
				.. held.Select(rider => new PositionFix(
					rider.Key,
					rider.Value.Lat,
					rider.Value.Lon,
					rider.Value.SpeedMps,
					rider.Value.HeadingDeg,
					rider.Value.RecordedUtc)),
			];

			try
			{
				await SendAsync(rideId, fixes);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				// One ride's send failing must not stop the others. A dropped batch costs a
				// five-second-stale pin; an escaped exception would kill the timer loop and
				// every ride's map with it.
				logger.LogError(exception, "Could not broadcast positions for {RideId}.", rideId);
			}
		}
	}

	/// <summary>
	/// Sends one ride's batch, leaving a blocked rider out of the copy that reaches the traveller
	/// who must not see them (§16.5).
	/// <para>
	/// <strong>The group send stays the whole story whenever it can.</strong> A ride where nobody
	/// has blocked anybody - which is nearly all of them - costs one set lookup to establish and
	/// then takes the same single fan-out it always did. Only when a block touches somebody with a
	/// pin in this ride does the send split, and even then it splits into "everybody else" plus one
	/// message per affected viewer, not one per member.
	/// </para>
	/// <para>
	/// Filtered here rather than in the recipients' apps, on <see cref="Positions.PositionStore"/>'s
	/// reasoning about consent: a position a viewer may not see has no business being in the
	/// fan-out or on the wire, whatever the client would have drawn.
	/// </para>
	/// </summary>
	private async Task SendAsync(Guid rideId, PositionFix[] fixes)
	{
		PositionBatch batch = new(rideId, fixes);

		if (!blocks.AnyInvolved(fixes, static fix => fix.UserId))
		{
			await hub.Clients.Group(RideHub.Group(rideId)).PositionsUpdated(batch);
			return;
		}

		List<string> excluded = [];
		List<(string ConnectionId, PositionBatch Batch)> tailored = [];

		foreach (RideWatcher watcher in connections.WatchersIn(rideId))
		{
			IReadOnlySet<Guid> hidden = blocks.HiddenFrom(watcher.UserId);

			if (hidden.Count == 0 || !Array.Exists(fixes, fix => hidden.Contains(fix.UserId)))
			{
				continue;
			}

			excluded.Add(watcher.ConnectionId);

			tailored.Add((
				watcher.ConnectionId,
				new PositionBatch(rideId, [.. fixes.Where(fix => !hidden.Contains(fix.UserId))])));
		}

		if (excluded.Count == 0)
		{
			await hub.Clients.Group(RideHub.Group(rideId)).PositionsUpdated(batch);
			return;
		}

		await hub.Clients.GroupExcept(RideHub.Group(rideId), excluded).PositionsUpdated(batch);

		// One at a time, because each carries a different list. A viewer whose own send fails gets
		// the same five-second-stale pin the group send's failure costs - and must not take the
		// rest of the tailored copies with them.
		foreach ((string connectionId, PositionBatch own) in tailored)
		{
			try
			{
				await hub.Clients.Client(connectionId).PositionsUpdated(own);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				logger.LogError(exception, "Could not send filtered positions for {RideId}.", rideId);
			}
		}
	}
}
