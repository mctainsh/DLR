using DLR.Core.Contracts.Comments;
using DLR.Server.Data;
using DLR.Server.Data.Comments;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Comments;

/// <summary>Reading poll results (§17.5).</summary>
public static class CommentPolls
{
	/// <summary>Results for one poll, or null when the comment is not one.</summary>
	/// <param name="database">The context.</param>
	/// <param name="commentId">Which comment.</param>
	/// <param name="forUser">Whose own vote to report, or null for a broadcast.</param>
	/// <param name="now">The server clock, for the closed-on-read decision.</param>
	/// <param name="hidden">Accounts the reader has blocked (§17.7).</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public static async Task<PollResults?> ResultsAsync(
		DlrDbContext database,
		Guid commentId,
		Guid? forUser,
		DateTimeOffset now,
		IReadOnlySet<Guid>? hidden = null,
		CancellationToken cancellationToken = default)
	{
		IReadOnlyDictionary<Guid, PollResults> all =
			await ResultsAsync(database, [commentId], forUser, now, hidden, cancellationToken);

		return all.TryGetValue(commentId, out PollResults? results) ? results : null;
	}

	/// <summary>Results for every poll on a page, in one round trip.</summary>
	/// <param name="database">The context.</param>
	/// <param name="commentIds">The page.</param>
	/// <param name="forUser">Whose own votes to report, or null.</param>
	/// <param name="now">The server clock.</param>
	/// <param name="hidden">
	/// Accounts the reader has blocked. Their votes are dropped from the attributed list
	/// <strong>and from the count</strong>, because §17.5 makes the two the same thing — a tally
	/// that did not match the names beside it would read as a bug.
	/// </param>
	/// <param name="cancellationToken">Cancellation.</param>
	public static async Task<IReadOnlyDictionary<Guid, PollResults>> ResultsAsync(
		DlrDbContext database,
		IReadOnlyCollection<Guid> commentIds,
		Guid? forUser,
		DateTimeOffset now,
		IReadOnlySet<Guid>? hidden = null,
		CancellationToken cancellationToken = default)
	{
		IReadOnlySet<Guid> blocked = hidden ?? new HashSet<Guid>();
		if (commentIds.Count == 0)
		{
			return new Dictionary<Guid, PollResults>();
		}

		List<Poll> polls = await database
			.Set<Poll>()
			.AsNoTracking()
			.Include(poll => poll.Comment)
			.Include(poll => poll.Options)
			.ThenInclude(option => option.Votes)
			.ThenInclude(vote => vote.User)
			.Where(poll => commentIds.Contains(poll.CommentId))
			.ToListAsync(cancellationToken);

		Dictionary<Guid, PollResults> result = [];

		foreach (Poll poll in polls)
		{
			List<PollOptionResult> options =
			[
				.. poll.Options
					.OrderBy(option => option.Ordinal)
					.Select(option => new PollOptionResult(
						option.Id,
						option.Ordinal,
						option.Text,
						option.Votes.Count(vote => !blocked.Contains(vote.UserId)),

						// Attributed, always. "Who's coming on Saturday" is the question, and an
						// anonymous tally answers a different one (§17.5).
						[
							.. option.Votes
								.Where(vote => !blocked.Contains(vote.UserId))
								.OrderBy(vote => vote.CreatedUtc)
								.Select(vote => new PollVoter(vote.UserId, vote.User!.UserName!))
						])),
			];

			List<Guid> mine = forUser is { } userId
				?
				[
					.. poll.Options
						.Where(option => option.Votes.Any(vote => vote.UserId == userId))
						.Select(option => option.Id)
				]
				: [];

			result[poll.CommentId] = new PollResults(
				poll.CommentId,
				poll.Comment?.Body,
				poll.AllowMultiple,
				poll.ClosesUtc,
				poll.ClosedUtc,

				// Computed here on every read, never stored. A background job flipping a flag
				// would leave a window in which an elapsed poll still took votes — as wide as the
				// job's interval, and widest exactly when the job is behind (§17.5).
				poll.IsClosed(now),
				options,
				mine);
		}

		return result;
	}
}
