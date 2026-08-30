using Microsoft.Extensions.Options;

namespace DLR.Server.Positions;

/// <summary>
/// Banks the fix counts on a timer (§14.6).
/// <para>
/// <strong>Accounting, not durability.</strong> Until v0.33 this also wrote every rider's position
/// to PostgreSQL on the same tick, and the name meant a flush of the map. Positions now live in
/// <see cref="RiderPositionCache"/> and nowhere else (§5.5), so what is left on this timer is the
/// lifetime counter — a number about an account, not a place a person was.
/// </para>
/// <para>
/// It keeps a period rather than counting on every fix because the counts coalesce: a rider sending
/// at 5 s produces one <c>UPDATE</c> per drain instead of one per fix, and a quiet server does no
/// work at all.
/// </para>
/// </summary>
/// <param name="meter">The fix counts to fold into each rider's lifetime total (§14.6).</param>
/// <param name="scopes">A scope per drain, because the counter holds a scoped context.</param>
/// <param name="clock">Drives the timer, so tests advance rather than sleep (§10.4).</param>
/// <param name="options">The period.</param>
/// <param name="logger">Where a failed write is recorded.</param>
public sealed class PositionCounterFlushService(
	PositionActivityMeter meter,
	IServiceScopeFactory scopes,
	TimeProvider clock,
	IOptions<RideOptions> options,
	ILogger<PositionCounterFlushService> logger) : BackgroundService
{
	/// <inheritdoc />
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		TimeSpan period = TimeSpan.FromSeconds(Math.Max(1, options.Value.FlushSeconds));

		using PeriodicTimer timer = new(period, clock);

		try
		{
			while (await timer.WaitForNextTickAsync(stoppingToken))
			{
				await FlushAsync(stoppingToken);
			}
		}
		catch (OperationCanceledException)
		{
			// Shutdown. The final drain below is the one that matters.
		}
	}

	/// <inheritdoc />
	public override async Task StopAsync(CancellationToken cancellationToken)
	{
		await base.StopAsync(cancellationToken);

		// Counts held in the meter have no second copy anywhere, so a deploy without this drops
		// every fix banked since the last tick.
		await FlushAsync(cancellationToken);
	}

	/// <summary>Banks every counted fix. Public so tests drive one tick.</summary>
	/// <param name="cancellationToken">Abandons the write.</param>
	public async Task FlushAsync(CancellationToken cancellationToken)
	{
		IReadOnlyDictionary<Guid, long> counted = meter.DrainRiderCounts();

		if (counted.Count == 0)
		{
			// No scope, no connection, no command. A quiet server must be quiet — otherwise the
			// idle cost of the feature is a database round trip every ten seconds, forever.
			return;
		}

		try
		{
			await using AsyncServiceScope scope = scopes.CreateAsyncScope();

			await scope.ServiceProvider
				.GetRequiredService<IPositionCounter>()
				.CountAsync(counted, cancellationToken);
		}
		catch (Exception failure) when (failure is not OperationCanceledException)
		{
			// The counts have already left the meter, so they go back by hand or they are gone.
			meter.Restore(counted);

			try
			{
				logger.LogError(failure, "Could not bank {Count} rider fix counts.", counted.Count);
			}
			catch (ObjectDisposedException)
			{
				// A drain during shutdown can outlive the logging provider, and a failure to
				// *report* a failure must not become the exception that takes the host down.
			}
		}
	}
}
