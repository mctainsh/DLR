using DLR.Server.Identity;

namespace DLR.TestSupport.Identity;

/// <summary>
/// A breach corpus a test controls, and the only way to reach §7.2's outage clause.
/// <para>
/// The real client talks to a third party. A suite that used it would be slow, would fail on
/// an aeroplane, and — worse — would exercise the "service unreachable, registration proceeds"
/// path only on the days the service happened to be down.
/// </para>
/// </summary>
public sealed class FakeBreachedPasswordCheck : IBreachedPasswordCheck
{
	private readonly HashSet<string> _breached = new(StringComparer.Ordinal);

	/// <summary>What every lookup returns when the password is not in <see cref="Breach"/>.</summary>
	public BreachCheckResult DefaultResult { get; set; } = BreachCheckResult.NotBreached;

	/// <summary>Every password this fake has been asked about, in order.</summary>
	public List<string> Queried { get; } = [];

	/// <summary>Marks a password as present in the corpus.</summary>
	/// <param name="password">The password a later registration will be refused for.</param>
	public FakeBreachedPasswordCheck Breach(string password)
	{
		_breached.Add(password);

		return this;
	}

	/// <summary>Makes every lookup report the service as unreachable.</summary>
	public FakeBreachedPasswordCheck GoOffline()
	{
		DefaultResult = BreachCheckResult.Unavailable;

		return this;
	}

	/// <inheritdoc />
	public Task<BreachCheckResult> CheckAsync(
		string password,
		CancellationToken cancellationToken = default)
	{
		Queried.Add(password);

		// Offline wins over the corpus, because that is what an outage is: not a service
		// that answers "no", a service that answers nothing. It also makes the interesting
		// test possible — a password known to be breached, registered anyway, because
		// nobody could find out (§7.2).
		if (DefaultResult == BreachCheckResult.Unavailable)
		{
			return Task.FromResult(BreachCheckResult.Unavailable);
		}

		return Task.FromResult(_breached.Contains(password)
			? BreachCheckResult.Breached
			: DefaultResult);
	}
}
