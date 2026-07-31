using System.Net;
using DLR.Server.Data;
using DLR.Server.Data.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DLR.Server.Identity;

/// <summary>The §7.8 thresholds, in configuration rather than as constants.</summary>
public sealed class AbuseOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "Abuse";

	/// <summary>
	/// How many accounts one address may create in the window before an email is required.
	/// <para>
	/// Configurable not because the number is secret — it is printed in the design document —
	/// but because you want to change it in response to real abuse without shipping a release,
	/// and because a reader of a public repository should not learn the <em>current</em> value
	/// (§14.5).
	/// </para>
	/// </summary>
	public int FreeAccountsPerAddress { get; set; } = 3;

	/// <summary>The rolling window the ladder counts over.</summary>
	public TimeSpan LadderWindow { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Decides whether a registration needs an email address (§7.8).
/// <para>
/// There is deliberately <strong>no hard cap</strong>. Carrier-grade NAT means an entire mobile
/// network can present as one address, so a flat block would silently refuse legitimate signups
/// on mobile data with no path forward. A ladder gives a real person an obvious next step —
/// verify an email — while an abuser needs N distinct working mailboxes.
/// </para>
/// </summary>
/// <param name="database">The one context.</param>
/// <param name="clock">The project's clock (§10.4).</param>
/// <param name="options">The thresholds.</param>
/// <param name="logger">Where crossing the threshold is recorded.</param>
public sealed class RegistrationLadder(
	DlrDbContext database,
	TimeProvider clock,
	IOptions<AbuseOptions> options,
	ILogger<RegistrationLadder> logger)
{
	/// <summary>How many accounts this address has created inside the window.</summary>
	/// <param name="address">The client address, after forwarded headers (§7.8).</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public async Task<int> CountRecentAsync(IPAddress? address, CancellationToken cancellationToken = default)
	{
		if (address is null)
		{
			return 0;
		}

		DateTimeOffset since = clock.GetUtcNow() - options.Value.LadderWindow;

		// Counting rows, deliberately, and not AddRateLimiter. Its partitions live in process
		// memory: they reset on every deploy and are per-instance, so an attacker just waits
		// for a restart. A count over the table is the only version of this that survives one.
		return await database.Users
			.CountAsync(user => user.CreatedByIp!.Equals(address) && user.CreatedUtc > since, cancellationToken);
	}

	/// <summary>Whether an account created from here right now must supply an email address.</summary>
	/// <param name="address">The client address, after forwarded headers.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public async Task<bool> RequiresEmailAsync(
		IPAddress? address,
		CancellationToken cancellationToken = default)
	{
		int existing = await CountRecentAsync(address, cancellationToken);

		bool required = existing >= options.Value.FreeAccountsPerAddress;

		if (required)
		{
			// Crossing the threshold logs an alert (§7.8). One line is not an incident; the
			// same address appearing here all afternoon is.
			logger.LogWarning(
				"Registration from {Address} is past the ladder threshold — {Existing} accounts " +
				"in the last {Window}. An email address is now required.",
				address,
				existing,
				options.Value.LadderWindow);
		}

		return required;
	}
}
