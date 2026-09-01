using DLR.Core.Contracts.Comments;
using DLR.Server.Data;
using DLR.Server.Data.Comments;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Comments;

/// <summary>
/// Reading reaction tallies (§17.4).
/// <para>
/// One place, because the thread, the single-comment response and the coalesced broadcast all
/// need the same shape and would otherwise each build it - and the one that drifted would be the
/// broadcast, which nothing looks at directly.
/// </para>
/// </summary>
public static class CommentReactions
{
	/// <summary>Counts for one comment.</summary>
	/// <param name="database">The context.</param>
	/// <param name="commentId">Which comment.</param>
	/// <param name="forUser">Whose own reaction to report, or null for a broadcast.</param>
	/// <param name="hidden">Accounts the reader has blocked, whose reactions do not count (§17.7).</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public static async Task<ReactionCounts> CountsAsync(
		DlrDbContext database,
		Guid commentId,
		Guid? forUser,
		IReadOnlySet<Guid>? hidden = null,
		CancellationToken cancellationToken = default)
	{
		IReadOnlyDictionary<Guid, ReactionCounts> all =
			await CountsAsync(database, [commentId], forUser, hidden, cancellationToken);

		return all.TryGetValue(commentId, out ReactionCounts? counts) ? counts : ReactionCounts.None;
	}

	/// <summary>
	/// Counts for a whole page of comments, in one round trip.
	/// <para>
	/// A page at a time rather than a query per comment: a thread page is fifty posts, and fifty
	/// round trips to render one screen is the N+1 that makes a fast feature feel broken.
	/// </para>
	/// </summary>
	/// <param name="database">The context.</param>
	/// <param name="commentIds">The page.</param>
	/// <param name="forUser">Whose own reaction to report, or null.</param>
	/// <param name="hidden">
	/// Accounts the reader has blocked. Their reactions are excluded from the tally, not merely
	/// from a list of names - §17.7 says blocking hides a person's reactions, and a count that
	/// still included them would be the one place their presence leaked through.
	/// </param>
	/// <param name="cancellationToken">Cancellation.</param>
	public static async Task<IReadOnlyDictionary<Guid, ReactionCounts>> CountsAsync(
		DlrDbContext database,
		IReadOnlyCollection<Guid> commentIds,
		Guid? forUser,
		IReadOnlySet<Guid>? hidden = null,
		CancellationToken cancellationToken = default)
	{
		List<Guid> blocked = hidden is null ? [] : [.. hidden];
		if (commentIds.Count == 0)
		{
			return new Dictionary<Guid, ReactionCounts>();
		}

		// Grouped in the database. Pulling the rows back and counting them here would move a
		// ride's worth of narrow rows across the wire to produce six integers.
		var tallies = await database
			.Set<CommentReaction>()
			.AsNoTracking()
			.Where(reaction =>
				commentIds.Contains(reaction.CommentId)
				&& !blocked.Contains(reaction.UserId))
			.GroupBy(reaction => new { reaction.CommentId, reaction.Reaction })
			.Select(group => new
			{
				group.Key.CommentId,
				group.Key.Reaction,
				Count = group.Count(),
			})
			.ToListAsync(cancellationToken);

		Dictionary<Guid, string> mine = [];

		if (forUser is { } userId)
		{
			mine = await database
				.Set<CommentReaction>()
				.AsNoTracking()
				.Where(reaction => commentIds.Contains(reaction.CommentId) && reaction.UserId == userId)
				.ToDictionaryAsync(
					reaction => reaction.CommentId,
					reaction => reaction.Reaction,
					cancellationToken);
		}

		Dictionary<Guid, ReactionCounts> result = [];

		foreach (Guid commentId in commentIds)
		{
			Dictionary<string, int> counts = tallies
				.Where(tally => tally.CommentId == commentId)
				.ToDictionary(tally => tally.Reaction, tally => tally.Count, StringComparer.Ordinal);

			result[commentId] = new ReactionCounts(
				counts,
				mine.TryGetValue(commentId, out string? own) ? own : null);
		}

		return result;
	}
}
