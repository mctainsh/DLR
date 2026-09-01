using DLR.Core.Contracts.Rides;
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
/// <param name="logger">Where a failed send is recorded.</param>
public sealed class RideBroadcastService(
	RiderPositionCache cache,
	IHubContext<RideHub, IRideClient> hub,
	TimeProvider clock,
	IOptions<RideOptions> options,
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

			PositionBatch batch = new(
				rideId,
				[
					.. held.Select(rider => new PositionFix(
						rider.Key,
						rider.Value.Lat,
						rider.Value.Lon,
						rider.Value.SpeedMps,
						rider.Value.HeadingDeg,
						rider.Value.RecordedUtc)),
				]);

			try
			{
				await hub.Clients
					.Group(RideHub.Group(rideId))
					.PositionsUpdated(batch);
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
}
