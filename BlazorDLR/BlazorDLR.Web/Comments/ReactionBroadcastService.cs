using System.Collections.Concurrent;
using DLR.Core.Contracts.Comments;
using DLR.Server.Data;
using DLR.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace DLR.Server.Comments;

/// <summary>
/// Coalesces reaction and poll changes into one hub message per comment per tick (§17.4).
/// <para>
/// <strong>Reactions are the highest-frequency, lowest-value event in the product.</strong> Twelve
/// members tapping a thumbs-up on the same photo, each tap relayed to the other eleven, is exactly
/// the O(n²) fan-out §5.3 refused for positions - and for something whose whole payload is a
/// number. So a change marks the comment dirty and the timer sends the tally, once. A count
/// arriving three seconds late has cost nobody anything.
/// </para>
/// <para>
/// The broadcast carries <see cref="ReactionCounts.Mine"/> as <strong>null</strong>, necessarily: a
/// group message has one body and "mine" is different for every connection in it. Each client
/// already knows what it sent, so this is the tally and nothing else.
/// </para>
/// </summary>
/// <param name="hub">Where the messages go.</param>
/// <param name="scopes">A scope per flush - a background service must not hold a scoped context.</param>
/// <param name="options">The coalescing interval.</param>
/// <param name="clock">The project clock, so a test drives the timer rather than waiting on it.</param>
public sealed class ReactionBroadcastService(
	IHubContext<RideHub, IRideClient> hub,
	IServiceScopeFactory scopes,
	IOptions<CommentOptions> options,
	TimeProvider clock) : BackgroundService
{
	/// <summary>Comments whose reactions changed since the last tick, onto the group to tell.</summary>
	/// <remarks>
	/// The <em>group name</em> rather than a ride id, because a comment now hangs off an adventure
	/// or off a shared route (§6.2) and this service has no business knowing which. Asking
	/// <see cref="ThreadAccess.HubGroup"/> once, at the point where the change happened, is one
	/// decision; storing an id here and re-deciding at flush time would be two, in different files.
	/// </remarks>
	private readonly ConcurrentDictionary<Guid, string> _reactions = new();

	/// <summary>Comments whose votes changed since the last tick, onto the group to tell.</summary>
	private readonly ConcurrentDictionary<Guid, string> _polls = new();

	private readonly CommentOptions _options = options.Value;

	/// <summary>Notes that a comment's reactions changed.</summary>
	/// <param name="commentId">Which comment.</param>
	/// <param name="group">Which hub group to tell - <see cref="ThreadAccess.HubGroup"/>.</param>
	public void ReactionChanged(Guid commentId, string group) => _reactions[commentId] = group;

	/// <summary>Notes that a poll's votes changed.</summary>
	/// <param name="commentId">Which comment.</param>
	/// <param name="group">Which hub group to tell.</param>
	public void PollChanged(Guid commentId, string group) => _polls[commentId] = group;

	/// <summary>
	/// Sends one message per dirty comment and clears the sets.
	/// <para>
	/// Public so a test can drive it directly. Advancing a fake clock and waiting for a
	/// <see cref="PeriodicTimer"/> is a race twice over - <c>StartAsync</c> returns as soon as
	/// <c>ExecuteAsync</c> reaches its first await, which is not necessarily after the timer
	/// exists - and SRV-22 already paid for learning that.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancellation.</param>
	public async Task FlushAsync(CancellationToken cancellationToken = default)
	{
		if (_reactions.IsEmpty && _polls.IsEmpty)
		{
			return;
		}

		// Taken and cleared before the work, so a reaction arriving mid-flush is picked up by the
		// next tick rather than lost to this one.
		List<KeyValuePair<Guid, string>> reactions = [.. _reactions];
		List<KeyValuePair<Guid, string>> polls = [.. _polls];

		foreach (KeyValuePair<Guid, string> entry in reactions)
		{
			_reactions.TryRemove(entry);
		}

		foreach (KeyValuePair<Guid, string> entry in polls)
		{
			_polls.TryRemove(entry);
		}

		using IServiceScope scope = scopes.CreateScope();

		DlrDbContext database = scope.ServiceProvider.GetRequiredService<DlrDbContext>();

		foreach ((Guid commentId, string group) in reactions)
		{
			// No block list: a group message has one body, and whose reactions each connection
			// should not see is per connection. Clients apply their own list on receipt, exactly
			// as they do for "mine".
			ReactionCounts counts = await CommentReactions.CountsAsync(
				database,
				commentId,
				forUser: null,
				hidden: null,
				cancellationToken);

			await hub.Clients.Group(group).ReactionsUpdated(commentId, counts);
		}

		foreach ((Guid commentId, string group) in polls)
		{
			PollResults? results = await CommentPolls.ResultsAsync(
				database,
				commentId,
				forUser: null,
				clock.GetUtcNow(),
				hidden: null,
				cancellationToken);

			if (results is not null)
			{
				await hub.Clients.Group(group).PollUpdated(commentId, results);
			}
		}
	}

	/// <inheritdoc />
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		using PeriodicTimer timer = new(
			TimeSpan.FromSeconds(_options.ReactionCoalesceSeconds),
			clock);

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				if (!await timer.WaitForNextTickAsync(stoppingToken))
				{
					return;
				}

				await FlushAsync(stoppingToken);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception)
			{
				// A failed tick must not take the service down. The next one re-reads the tally
				// from the database, so a dropped message costs a client three seconds of a stale
				// count and nothing else - the row is already committed.
			}
		}
	}
}
