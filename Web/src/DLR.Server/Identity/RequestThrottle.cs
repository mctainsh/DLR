using System.Collections.Concurrent;

namespace DLR.Server.Identity;

/// <summary>
/// The §7.8 rate-limit table: fixed windows, in process memory, keyed on whatever the rule is
/// actually about.
/// <para>
/// Not <c>AddRateLimiter</c>, and the reason is the table rather than a preference. Three of
/// §7.8's rows are keyed on a <em>username</em>, an <em>email address</em> or a <em>device</em>,
/// all of which arrive in the request body — a middleware partitioner sees the URL and the
/// connection and would have to guess at the rest. The two rows that are per-address are the
/// only ones it could have enforced.
/// </para>
/// <para>
/// In-memory is correct here and wrong for the ladder (§7.8). These limits exist to blunt a
/// burst, so losing them on deploy costs a few seconds of protection. The ladder decides whether
/// an account may exist at all, so losing it on deploy is a bypass an attacker can simply wait
/// for — which is why that one counts rows.
/// </para>
/// </summary>
/// <param name="clock">The project's clock (§10.4), so a test can roll a window without waiting.</param>
public sealed class RequestThrottle(TimeProvider clock)
{
	private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

	/// <summary>Records an attempt and says whether it is within the limit.</summary>
	/// <param name="key">What the rule is about — an address, a username, a device.</param>
	/// <param name="limit">How many are allowed in a window.</param>
	/// <param name="window">How long the window is.</param>
	public bool TryAcquire(string key, int limit, TimeSpan window)
	{
		DateTimeOffset now = clock.GetUtcNow();

		Window updated = _windows.AddOrUpdate(
			key,
			_ => new Window(now + window, 1),
			(_, current) => now >= current.ExpiresAt
				? new Window(now + window, 1)
				: current with { Used = current.Used + 1 });

		if (_windows.Count > PruneAbove)
		{
			Prune(now);
		}

		return updated.Used <= limit;
	}

	/// <summary>
	/// Above this many live keys, expired ones are swept on the next write. A bounded dictionary
	/// matters more than it looks: the keys include caller-supplied strings, so an unbounded one
	/// would make the throttle itself the memory-exhaustion vector.
	/// </summary>
	private const int PruneAbove = 10_000;

	private void Prune(DateTimeOffset now)
	{
		foreach (KeyValuePair<string, Window> entry in _windows)
		{
			if (now >= entry.Value.ExpiresAt)
			{
				_windows.TryRemove(entry.Key, out _);
			}
		}
	}

	private readonly record struct Window(DateTimeOffset ExpiresAt, int Used);
}
