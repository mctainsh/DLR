using DLR.Server.Data;
using DLR.Server.Data.Moderation;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Moderation;

/// <summary>
/// Who a rider has hidden (§16.5, §17.7).
/// <para>
/// One place, because <em>every</em> read of authored content has to apply it — the thread, a
/// single comment, reaction tallies, poll voters, a ride's markers. Four copies of "and not from
/// somebody I blocked" is how one of them ends up not having it, and the one that ends up not
/// having it is the one nobody opens.
/// </para>
/// </summary>
public static class BlockList
{
	/// <summary>
	/// The accounts this rider has blocked.
	/// <para>
	/// One-directional: blocking hides <em>their</em> content from <em>you</em>. The person blocked
	/// is told nothing and loses nothing — a block that announced itself would turn a quiet "I would
	/// rather not read this person" into the confrontation it exists to avoid.
	/// </para>
	/// </summary>
	/// <param name="database">The context.</param>
	/// <param name="blockerId">Whose list.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public static async Task<IReadOnlySet<Guid>> HiddenFromAsync(
		DlrDbContext database,
		Guid blockerId,
		CancellationToken cancellationToken = default) =>
		(await database
			.Set<UserBlock>()
			.AsNoTracking()
			.Where(block => block.BlockerId == blockerId)
			.Select(block => block.BlockedId)
			.ToListAsync(cancellationToken))
			.ToHashSet();
}
