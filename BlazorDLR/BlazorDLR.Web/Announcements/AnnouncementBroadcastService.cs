using DLR.Core.Contracts.Announcements;
using DLR.Server.Data;
using DLR.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Announcements;

/// <summary>
/// Sends an announcement to every connected rider at the moment it goes live (§20.3).
/// <para>
/// <strong>A sweep rather than a send from the endpoint that wrote the row.</strong> An
/// announcement may carry a publish-from date, so it goes live at an instant when no request is
/// happening and there is nobody to send it. One path covers both that and the immediate case;
/// broadcasting from the create endpoint <em>as well</em> would be a second path that has to keep
/// agreeing with this one about what "live" means.
/// </para>
/// <para>
/// <strong>The window it sends for is <c>(lastTick, now]</c>.</strong> That is what makes the send
/// once-only without a column to mark: an announcement is sent by the first tick its publish-from
/// falls behind, and no later tick's window contains it again.
/// </para>
/// <para>
/// <strong>A restart does not re-blast.</strong> <c>lastTick</c> starts at the boot instant, so
/// everything published while the process was down is left to the launch check - which every client
/// runs anyway, and which is the path a client that was offline depends on regardless.
/// </para>
/// </summary>
/// <param name="hub">Where the messages go.</param>
/// <param name="scopes">A scope per tick - a background service must not hold a scoped context.</param>
/// <param name="clock">The project clock, so a test drives the sweep rather than waiting on it.</param>
public sealed class AnnouncementBroadcastService(
	IHubContext<RideHub, IRideClient> hub,
	IServiceScopeFactory scopes,
	TimeProvider clock) : BackgroundService
{
	/// <summary>
	/// How often the sweep looks. A maintenance notice arriving within a minute of being published
	/// is the whole requirement; anything faster would be a per-second query against a table that
	/// changes a few times a month.
	/// </summary>
	public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

	/// <summary>The upper edge of the last window swept. Everything at or before it has been sent.</summary>
	private DateTimeOffset _sentThrough = clock.GetUtcNow();

	/// <summary>
	/// Sends every announcement that became live since the last sweep.
	/// <para>
	/// Public so a test can drive it directly. Advancing a fake clock and waiting for a
	/// <see cref="PeriodicTimer"/> is a race twice over - <c>StartAsync</c> returns as soon as
	/// <c>ExecuteAsync</c> reaches its first await, which is not necessarily after the timer
	/// exists - and SRV-22 already paid for learning that.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancellation.</param>
	public async Task FlushAsync(CancellationToken cancellationToken = default)
	{
		DateTimeOffset now = clock.GetUtcNow();
		DateTimeOffset since = _sentThrough;

		// Moved before the send, not after. A failed send is a message a client picks up from the
		// launch check; a window that stayed open because the send threw would re-send everything
		// in it on every subsequent tick, forever.
		_sentThrough = now;

		List<AnnouncementDto> arrived = await ReadAsync(since, now, cancellationToken);

		foreach (AnnouncementDto announcement in arrived)
		{
			// Clients.All rather than a group: an announcement belongs to the server, not to an
			// adventure or a route. Safe because RideHub is [Authorize] - every connection on it
			// is an authenticated rider.
			await hub.Clients.All.AnnouncementPosted(announcement);
		}
	}

	/// <summary>
	/// What became live in the window, read in a scope of its own.
	/// <para>
	/// Its own scope so the <see cref="DlrDbContext"/> and its connection are handed back before
	/// the fan-out below, rather than held for the length of a send to every connected client.
	/// </para>
	/// </summary>
	private async Task<List<AnnouncementDto>> ReadAsync(
		DateTimeOffset since,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		using IServiceScope scope = scopes.CreateScope();

		DlrDbContext database = scope.ServiceProvider.GetRequiredService<DlrDbContext>();

		return await database
			.Set<Data.Announcements.Announcement>()
			.AsNoTracking()
			.Where(announcement => announcement.PublishFromUtc > since
				&& announcement.PublishFromUtc <= now
				&& announcement.ExpiresUtc > now)
			.OrderBy(announcement => announcement.PublishFromUtc)
			.Select(AnnouncementQueries.ToDto)
			.ToListAsync(cancellationToken);
	}

	/// <inheritdoc />
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		using PeriodicTimer timer = new(Interval, clock);

		try
		{
			while (await timer.WaitForNextTickAsync(stoppingToken))
			{
				// A failed tick must not take the service down. The row is committed either way,
				// and the launch check is the client's other way of finding it.
				try { await FlushAsync(stoppingToken); }
				catch (Exception) when (!stoppingToken.IsCancellationRequested) { }
			}
		}
		catch (OperationCanceledException)
		{
			// Shutdown.
		}
	}
}
