namespace DLR.Server.Identity;

/// <summary>
/// Whether a password has turned up in a public breach corpus (§7.2).
/// <para>
/// An interface rather than a direct call, for one reason: the outage path has to be
/// testable. §7.2 requires that registration proceeds when the service is unreachable, and a
/// rule that only holds when a third party is down is a rule nobody can verify against the
/// real thing.
/// </para>
/// </summary>
public interface IBreachedPasswordCheck
{
	/// <summary>Looks the password up.</summary>
	/// <param name="password">The candidate, in the clear. It never leaves the process.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	Task<BreachCheckResult> CheckAsync(string password, CancellationToken cancellationToken = default);
}

/// <summary>What a breach lookup concluded.</summary>
public enum BreachCheckResult
{
	/// <summary>The corpus does not contain it. Registration proceeds.</summary>
	NotBreached = 0,

	/// <summary>The corpus contains it. Registration is refused.</summary>
	Breached = 1,

	/// <summary>
	/// The service could not be reached, so nothing is known.
	/// <para>
	/// Registration proceeds, exactly as for <see cref="NotBreached"/> — but this is a
	/// distinct value rather than a synonym for it, because "no breach found" and "nobody
	/// looked" are different facts about a deployment, and only one of them is worth
	/// waking up for when it lasts a week.
	/// </para>
	/// </summary>
	Unavailable = 2,
}
