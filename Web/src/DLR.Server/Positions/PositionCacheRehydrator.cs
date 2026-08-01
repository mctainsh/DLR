using DLR.Server.Data;
using DLR.Server.Data.Positions;
using DLR.Server.Data.Rides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DLR.Server.Positions;

/// <summary>
/// Fills the cache once, at startup (§5.5).
/// <para>
/// A restart mid-ride must not blank the map for the riders the feature exists to protect. All
/// four rules below matter; each one omitted is a defect rather than a rough edge.
/// </para>
/// </summary>
/// <param name="cache">What to fill.</param>
/// <param name="scopes">A scope to read through.</param>
/// <param name="clock">For the freshness gate.</param>
/// <param name="options">The staleness window.</param>
/// <param name="logger">Where a failed rehydration is recorded.</param>
public sealed class PositionCacheRehydrator(
	RiderPositionCache cache,
	IServiceScopeFactory scopes,
	TimeProvider clock,
	IOptions<RideOptions> options,
	ILogger<PositionCacheRehydrator> logger) : IHostedService
{
	private readonly Lazy<Task> once = new(() => Task.CompletedTask);

	/// <inheritdoc />
	public Task StartAsync(CancellationToken cancellationToken)
	{
		// Kicked off eagerly and not awaited: the cache is usually warm before the first request
		// arrives, and anything that arrives sooner blocks on ReadyAsync() rather than on this.
		_ = RehydrateAsync();

		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	/// <summary>Loads the cache. Safe to call more than once; the second call does nothing.</summary>
	public async Task RehydrateAsync()
	{
		if (once.IsValueCreated)
		{
			return;
		}

		_ = once.Value;

		try
		{
			await using AsyncServiceScope scope = scopes.CreateAsyncScope();

			DlrDbContext database = scope.ServiceProvider.GetRequiredService<DlrDbContext>();

			// Rule 2: the freshness gate. A position from before the process died must not
			// reappear on the map as if the rider were still there.
			DateTimeOffset floor = clock.GetUtcNow()
				.AddMinutes(-Math.Max(1, options.Value.StalenessMinutes));

			DateTimeOffset now = clock.GetUtcNow();

			// Rule 1: live rides only — plus rides inside an unexpired wind-down, which are
			// Completed but still legitimately sharing (§5.6). A restart during a wind-down must
			// not blank the map for the riders it exists to protect, and must not resurrect one
			// that has already expired.
			List<RiderPosition> rows = await database
				.Set<RiderPosition>()
				.AsNoTracking()
				.Where(position =>
					position.RecordedUtc > floor
					&& (position.Ride!.State == GroupRideState.Live
						|| (position.Ride.SharingEndsUtc != null
							&& position.Ride.SharingEndsUtc > now)))
				.ToListAsync();

			foreach (RiderPosition row in rows)
			{
				// Rule 3: loaded entries are clean. Otherwise startup immediately schedules a
				// pointless write of everything it has just read.
				cache.Upsert(
					row.GroupRideId,
					row.UserId,
					new PositionEntry(
						row.Lat,
						row.Lon,
						row.SpeedMps,
						row.HeadingDeg,
						row.AccuracyM,
						row.RecordedUtc,
						IsDirty: false));
			}

			logger.LogInformation("Rehydrated {Count} rider positions.", rows.Count);
		}
		catch (Exception exception)
		{
			// A cache that failed to warm is a blank map that fills in on each rider's next push,
			// which is bad. A server that refuses to start is worse. Recorded loudly and opened
			// anyway.
			logger.LogError(exception, "Could not rehydrate the position cache.");
		}
		finally
		{
			// Rule 4 lives here as much as in the cache: reads block until this runs, so it has
			// to run on the failure path too or every read hangs forever.
			cache.MarkReady();
		}
	}
}
