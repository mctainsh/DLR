using DLR.Server.Data;
using DLR.Server.Data.Moderation;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Moderation;

/// <summary>
/// Who may not see whom on a map (§16.5, §17.7).
/// <para>
/// <strong>Symmetric, unlike the content block <see cref="BlockList"/> applies.</strong> Hiding a
/// blocked traveller's posts one way round is a reading preference; hiding a position one way round
/// would leave the person you blocked still watching where you are, which is the case a block on a
/// live map exists for. So a block here takes both pins off both maps.
/// </para>
/// <para>
/// <strong>In memory because the broadcast path may not touch the database.</strong> A batch goes
/// out per ride every five seconds (<see cref="Hubs.RideBroadcastService"/>) and a query there would
/// put one on the hot path for every ride on every tick - the write-behind exists precisely to keep
/// it off. Blocks change at human rate and are few, so the whole table fits and the endpoints that
/// move it say so.
/// </para>
/// <para>
/// One process, like <see cref="Positions.RiderPositionCache"/> and <see cref="Hubs.RideConnections"/>:
/// a second instance holds its own positions, so it holds its own copy of this beside them.
/// </para>
/// </summary>
public sealed class BlockCache
{
	private static readonly HashSet<Guid> None = [];

	// A lock rather than concurrent dictionaries. Reads are frequent and writes are rare, but a
	// block is two edits that have to land together - the pair is meaningless half-applied, and a
	// reader between them would answer that somebody is visible when they have just been blocked.
	private readonly Lock _gate = new();

	private readonly Dictionary<Guid, HashSet<Guid>> _blocked = [];
	private readonly Dictionary<Guid, HashSet<Guid>> _blockedBy = [];

	/// <summary>
	/// Fills the cache from the table. Called once at startup, before the first batch goes out.
	/// </summary>
	/// <param name="database">The context.</param>
	/// <param name="cancellationToken">Abandons the read.</param>
	public async Task LoadAsync(DlrDbContext database, CancellationToken cancellationToken = default)
	{
		List<UserBlock> rows = await database
			.Set<UserBlock>()
			.AsNoTracking()
			.ToListAsync(cancellationToken);

		lock (_gate)
		{
			_blocked.Clear();
			_blockedBy.Clear();

			foreach (UserBlock row in rows)
				AddLocked(row.BlockerId, row.BlockedId);
		}
	}

	/// <summary>Records a block, in both directions.</summary>
	/// <param name="blockerId">Who blocked.</param>
	/// <param name="blockedId">Who was blocked.</param>
	public void Block(Guid blockerId, Guid blockedId)
	{
		lock (_gate)
			AddLocked(blockerId, blockedId);
	}

	/// <summary>
	/// Forgets one block.
	/// <para>
	/// Only the one. If the other party blocked back, their block still stands and the two remain
	/// invisible to each other - which is why the raw direction is stored rather than a single
	/// symmetric set that could not tell one unblock from both.
	/// </para>
	/// </summary>
	/// <param name="blockerId">Whose list.</param>
	/// <param name="blockedId">Who comes off it.</param>
	public void Unblock(Guid blockerId, Guid blockedId)
	{
		lock (_gate)
		{
			Drop(_blocked, blockerId, blockedId);
			Drop(_blockedBy, blockedId, blockerId);
		}
	}

	/// <summary>Forgets everything about an account - what deleting one takes with it (§7.9).</summary>
	/// <param name="userId">Whose blocks, in both directions.</param>
	public void Forget(Guid userId)
	{
		lock (_gate)
		{
			if (_blocked.TryGetValue(userId, out HashSet<Guid>? mine))
			{
				foreach (Guid other in mine)
					Drop(_blockedBy, other, userId);
			}

			if (_blockedBy.TryGetValue(userId, out HashSet<Guid>? theirs))
			{
				foreach (Guid other in theirs)
					Drop(_blocked, other, userId);
			}

			_blocked.Remove(userId);
			_blockedBy.Remove(userId);
		}
	}

	/// <summary>
	/// Everyone whose position this traveller must not be shown, and who must not be shown theirs.
	/// </summary>
	/// <param name="userId">The viewer.</param>
	/// <returns>The accounts hidden from them, empty when there are none.</returns>
	public IReadOnlySet<Guid> HiddenFrom(Guid userId)
	{
		lock (_gate)
		{
			bool mine = _blocked.TryGetValue(userId, out HashSet<Guid>? blocked);
			bool theirs = _blockedBy.TryGetValue(userId, out HashSet<Guid>? blockedBy);

			if (!mine && !theirs)
				return None;

			HashSet<Guid> hidden = blocked is null ? [] : [.. blocked];

			if (blockedBy is not null)
				hidden.UnionWith(blockedBy);

			return hidden;
		}
	}

	/// <summary>
	/// Whether any of these accounts is in a block at all - the question the broadcast asks first,
	/// once per ride per tick (§5.3).
	/// <para>
	/// <strong>Allocation-free on the path that always runs.</strong> A list and a projection rather
	/// than an <c>IEnumerable&lt;Guid&gt;</c> the caller has to <c>Select</c> into: that <c>Select</c>
	/// allocated an iterator for every ride on every tick from the moment the first block anywhere in
	/// the app existed. <paramref name="userIdOf"/> is passed as a <c>static</c> lambda, which Roslyn
	/// caches in a static field, so the delegate costs nothing per call either.
	/// </para>
	/// <para>
	/// The empty check is inside the lock with the lookups rather than a separate property in front
	/// of it - one acquisition, and no window between "is anybody blocked" and "is it one of these".
	/// </para>
	/// </summary>
	/// <typeparam name="T">Whatever the caller is holding one per rider of.</typeparam>
	/// <param name="items">The riders with a position in one ride.</param>
	/// <param name="userIdOf">Reads the account out of one of them.</param>
	/// <returns>True when at least one of them blocks or is blocked by somebody.</returns>
	public bool AnyInvolved<T>(IReadOnlyList<T> items, Func<T, Guid> userIdOf)
	{
		lock (_gate)
		{
			if (_blocked.Count == 0)
				return false;

			for (int index = 0; index < items.Count; index++)
			{
				Guid userId = userIdOf(items[index]);

				if (_blocked.ContainsKey(userId) || _blockedBy.ContainsKey(userId))
					return true;
			}

			return false;
		}
	}

	private void AddLocked(Guid blockerId, Guid blockedId)
	{
		if (!_blocked.TryGetValue(blockerId, out HashSet<Guid>? mine))
			_blocked[blockerId] = mine = [];

		mine.Add(blockedId);

		if (!_blockedBy.TryGetValue(blockedId, out HashSet<Guid>? theirs))
			_blockedBy[blockedId] = theirs = [];

		theirs.Add(blockerId);
	}

	private static void Drop(Dictionary<Guid, HashSet<Guid>> index, Guid key, Guid value)
	{
		if (index.TryGetValue(key, out HashSet<Guid>? held) && held.Remove(value) && held.Count == 0)
			index.Remove(key);
	}
}
