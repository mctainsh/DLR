using DLR.Server.Data;
using DLR.Server.Data.Rides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DLR.Server.Positions;

/// <summary>
/// Closes expired post-ride sharing windows (§5.6).
/// <para>
/// <strong>This is the load-bearing half of the whole wind-down.</strong> The cap is server-side
/// and unconditional precisely because it must not depend on any client being awake to honour it:
/// a rider whose phone is flat, or who force-quit the app at the pub, must still stop broadcasting
/// at the deadline. A bounded window that relies on a client to end it is an unbounded window.
/// </para>
/// </summary>
/// <param name="scopes">A scope per sweep.</param>
/// <param name="cache">Evicted alongside the rows.</param>
/// <param name="clock">Drives the timer and decides what has expired (§10.4).</param>
/// <param name="options">The sweep period.</param>
/// <param name="logger">Where a failed sweep is recorded.</param>
public sealed class SharingWindDownService(
	IServiceScopeFactory scopes,
	RiderPositionCache cache,
	TimeProvider clock,
	IOptions<RideOptions> options,
	ILogger<SharingWindDownService> logger) : BackgroundService
{
	/// <inheritdoc />
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		TimeSpan period = TimeSpan.FromSeconds(Math.Max(1, options.Value.WindDownSweepSeconds));

		using PeriodicTimer timer = new(period, clock);

		try
		{
			while (await timer.WaitForNextTickAsync(stoppingToken))
			{
				await SweepAsync(stoppingToken);
			}
		}
		catch (OperationCanceledException)
		{
			// Shutdown. The next process start sweeps again, and the deadline is stored rather
			// than held in memory, so nothing is lost by stopping here.
		}
	}

	/// <summary>Force-stops every expired window. Public so tests drive one sweep.</summary>
	/// <param name="cancellationToken">Abandons the sweep.</param>
	public async Task SweepAsync(CancellationToken cancellationToken)
	{
		try
		{
			await using AsyncServiceScope scope = scopes.CreateAsyncScope();

			DlrDbContext database = scope.ServiceProvider.GetRequiredService<DlrDbContext>();
			PositionStore positions = scope.ServiceProvider.GetRequiredService<PositionStore>();

			DateTimeOffset now = clock.GetUtcNow();

			List<Guid> expired = await database
				.Set<GroupRide>()
				.Where(ride => ride.SharingEndsUtc != null && ride.SharingEndsUtc <= now)
				.Select(ride => ride.Id)
				.ToListAsync(cancellationToken);

			foreach (Guid rideId in expired)
			{
				// Deleted unconditionally, not "asked to stop". At the deadline the server force
				// stops sharing for everyone still on and deletes every position row.
				await positions.ClearRideAsync(rideId);

				await database
					.Set<GroupRideMember>()
					.Where(member => member.GroupRideId == rideId)
					.ExecuteUpdateAsync(
						member => member.SetProperty(row => row.ShareLocation, false),
						cancellationToken);

				// Cleared rather than moved forward. The window is over and there is no such
				// thing as the next one (§5.6).
				await database
					.Set<GroupRide>()
					.Where(ride => ride.Id == rideId)
					.ExecuteUpdateAsync(
						ride => ride.SetProperty(row => row.SharingEndsUtc, (DateTimeOffset?)null),
						cancellationToken);

				cache.RemoveRide(rideId);

				logger.LogInformation("Wind-down expired for {RideId}; sharing stopped.", rideId);
			}
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			// Retried on the next tick. Left loud, because a sweep that keeps failing is a ride
			// that keeps broadcasting past the time its riders were promised.
			logger.LogError(exception, "Could not sweep expired sharing wind-downs.");
		}
	}
}
