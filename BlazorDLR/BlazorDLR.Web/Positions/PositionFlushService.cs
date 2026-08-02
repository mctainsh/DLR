using Microsoft.Extensions.Options;

namespace DLR.Server.Positions;

/// <summary>
/// The 10 s write-behind (§5.5).
/// <para>
/// At 500 concurrent riders this is roughly 50 rows a second in a single statement — negligible
/// on the €4 VPS, and the reason the fan-out path never touches the database at all.
/// </para>
/// </summary>
/// <param name="cache">Where the dirty entries live.</param>
/// <param name="scopes">A scope per flush, because the writer holds a scoped context.</param>
/// <param name="clock">Drives the timer, so tests advance rather than sleep (§10.4).</param>
/// <param name="options">The period.</param>
/// <param name="logger">Where a failed write is recorded.</param>
public sealed class PositionFlushService(
	RiderPositionCache cache,
	IServiceScopeFactory scopes,
	TimeProvider clock,
	IOptions<RideOptions> options,
	ILogger<PositionFlushService> logger) : BackgroundService
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
			// Shutdown. The final flush below is the one that matters.
		}
	}

	/// <inheritdoc />
	public override async Task StopAsync(CancellationToken cancellationToken)
	{
		await base.StopAsync(cancellationToken);

		// A graceful shutdown loses nothing (§5.5), which is only true because of this. Without
		// it every deploy would throw away up to a flush period of movement for every rider —
		// the one case where the write-behind's cost is entirely avoidable.
		await FlushAsync(cancellationToken);
	}

	/// <summary>Writes every dirty entry and marks them clean. Public so tests drive one tick.</summary>
	/// <param name="cancellationToken">Abandons the write.</param>
	public async Task FlushAsync(CancellationToken cancellationToken)
	{
		IReadOnlyList<DirtyPosition> batch = cache.Dirty();

		if (batch.Count == 0)
		{
			// No scope, no connection, no command. A quiet server must be quiet — otherwise the
			// idle cost of the feature is a database round trip every ten seconds, forever.
			return;
		}

		try
		{
			await using AsyncServiceScope scope = scopes.CreateAsyncScope();

			IPositionWriter writer = scope.ServiceProvider.GetRequiredService<IPositionWriter>();

			await writer.WriteAsync(batch, cancellationToken);

			foreach (DirtyPosition position in batch)
			{
				cache.MarkClean(position);
			}
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			// Deliberately leaves the entries dirty. The next tick retries them, and the upsert's
			// WHERE guard makes retrying safe — whereas marking them clean on a failed write
			// would silently discard exactly the positions that failed to persist.
			logger.LogError(exception, "Could not flush {Count} rider positions.", batch.Count);
		}
	}
}
