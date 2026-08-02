using DLR.Server.Data;
using DLR.Server.Data.Identity;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Identity;

/// <summary>
/// Records that the server heard from an account (§7.10).
/// <para>
/// Piggybacked on the refresh a client already makes at app start: no extra endpoint, no extra
/// round trip, and no client work beyond what it does anyway. Throttled so that opening the app
/// five times in a morning is one <c>UPDATE</c> rather than five — which is the difference
/// between a free field and a write on the hot path of every launch.
/// </para>
/// </summary>
/// <param name="database">The one context.</param>
/// <param name="clock">The project's clock (§10.4).</param>
public sealed class ActivityTracker(DlrDbContext database, TimeProvider clock)
{
	/// <summary>
	/// How stale the record has to be before it is worth a write. An hour is far below the
	/// resolution anything reads it at — the inactivity sweep counts in days (§7.11) and the
	/// session list says "2 hours ago" — so the throttle costs nothing that is looked at.
	/// </summary>
	public static readonly TimeSpan ThrottleWindow = TimeSpan.FromHours(1);

	/// <summary>Updates the account and the device, if either is stale enough to be worth it.</summary>
	/// <param name="userId">Whose activity.</param>
	/// <param name="deviceId">Which installation was heard from.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public async Task RecordAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken = default)
	{
		DateTimeOffset now = clock.GetUtcNow();
		DateTimeOffset stale = now - ThrottleWindow;

		// Set-based and guarded by the same predicate that decides whether to write, so two
		// launches racing each other cost at most one UPDATE and never a read-then-write that
		// disagrees with itself.
		//
		// The warning stamp is cleared in the same statement (§7.11). A rider who was warned at
		// 150 days and then came back has answered the warning, and leaving the stamp set would
		// mean the next quiet spell — a year later — ran to deletion with no warning at all,
		// because the sweep would find an account it had already told.
		await database
			.Set<AppUser>()
			.Where(user => user.Id == userId && user.LastActiveUtc <= stale)
			.ExecuteUpdateAsync(
				user => user
					.SetProperty(entity => entity.LastActiveUtc, now)
					.SetProperty(entity => entity.InactivityWarnedUtc, (DateTimeOffset?)null),
				cancellationToken);

		await database
			.Set<Device>()
			.Where(device => device.Id == deviceId && device.LastSeenUtc <= stale)
			.ExecuteUpdateAsync(
				device => device.SetProperty(entity => entity.LastSeenUtc, now),
				cancellationToken);
	}
}
