using System.Collections.Concurrent;

namespace DLR.Server.Identity;

/// <summary>
/// Holds a freshly issued refresh token in memory for the length of the grace window, so a
/// replay inside it can be answered with the same successor (§7.4).
/// <para>
/// This is a deliberate, bounded exception to "the raw token is never stored". It is process
/// memory rather than a row, it lasts ten seconds, and the alternative is worse than the risk:
/// a client that fires two requests, takes two 401s and refreshes twice would otherwise revoke
/// its own family and drop the rider at a login screen mid-ride. With permanent sessions that
/// is the single most likely way anyone is ever signed out.
/// </para>
/// <para>
/// A process restart empties it. <see cref="RefreshTokenService"/> treats a miss inside the
/// window as "cannot answer idempotently" rather than as theft — the caller gets a 401 and the
/// family survives, because a restart is not evidence of anything.
/// </para>
/// </summary>
/// <param name="clock">The project's clock (§10.4), so a test can close the window.</param>
public sealed class RefreshTokenGraceCache(TimeProvider clock)
{
	/// <summary>
	/// How long a replay is treated as the client asking twice rather than as a stolen token.
	/// Long enough to cover a burst of parallel requests, short enough that a genuinely stolen
	/// token is caught the first time it is used in anger.
	/// </summary>
	public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

	private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

	/// <summary>Remembers a successor against the id of the token it replaced.</summary>
	/// <param name="successorId">The row id of the newly issued token.</param>
	/// <param name="rawToken">The token as handed to the client.</param>
	public void Remember(Guid successorId, string rawToken)
	{
		Prune();

		_entries[successorId] = new Entry(rawToken, clock.GetUtcNow().Add(Window));
	}

	/// <summary>The successor issued against <paramref name="successorId"/>, if still inside the window.</summary>
	/// <param name="successorId">The row id recorded on the replayed token.</param>
	/// <param name="rawToken">The token to hand back.</param>
	public bool TryGet(Guid successorId, out string rawToken)
	{
		rawToken = string.Empty;

		if (!_entries.TryGetValue(successorId, out Entry entry))
		{
			return false;
		}

		if (clock.GetUtcNow() >= entry.ExpiresAt)
		{
			_entries.TryRemove(successorId, out _);

			return false;
		}

		rawToken = entry.RawToken;

		return true;
	}

	/// <summary>
	/// Drops what has expired. On write rather than on a timer: entries live ten seconds, and
	/// a background sweep for something that self-limits is a thread nobody needed.
	/// </summary>
	private void Prune()
	{
		DateTimeOffset now = clock.GetUtcNow();

		foreach (KeyValuePair<Guid, Entry> entry in _entries)
		{
			if (now >= entry.Value.ExpiresAt)
			{
				_entries.TryRemove(entry.Key, out _);
			}
		}
	}

	private readonly record struct Entry(string RawToken, DateTimeOffset ExpiresAt);
}
