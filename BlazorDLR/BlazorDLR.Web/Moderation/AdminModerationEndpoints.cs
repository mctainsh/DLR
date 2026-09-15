using DLR.Core.Contracts.Moderation;
using DLR.Server.Admin;
using DLR.Server.Comments;
using DLR.Server.Data;
using DLR.Server.Data.Comments;
using DLR.Server.Data.Identity;
using DLR.Server.Data.Markers;
using DLR.Server.Data.Moderation;
using DLR.Server.Diagnostics;
using DLR.Server.Hubs;
using DLR.Server.Markers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DLR.Server.Moderation;

/// <summary>Route names for the operator's review queue (§17.7).</summary>
public static class AdminModerationEndpoints
{
	/// <summary>Route name for the open queue.</summary>
	public const string QueueRouteName = "AdminReportQueue";

	/// <summary>Route name for clearing content.</summary>
	public const string RestoreRouteName = "AdminRestoreReported";

	/// <summary>Route name for deleting content.</summary>
	public const string RemoveRouteName = "AdminRemoveReported";

	/// <summary>The most reports one page carries. The queue is meant to be emptied, not paged.</summary>
	public const int MaxQueueSize = 200;
}

/// <summary>
/// The other half of holding reported content (§17.7, §10.2).
/// <para>
/// <strong>Without this the hold is a censorship button.</strong> A single report hides a post or a
/// marker from everybody the moment it is filed (see <see cref="ReportHold"/>), which is what App
/// Store guideline 1.2 asks for and is also, unmitigated, a way for any account to silence any
/// other. What makes that trade acceptable is that clearing a bad report is one tap and the terms
/// commit to doing it within 24 hours - so this controller is not an administrative nicety, it is
/// the part that makes the hold defensible.
/// </para>
/// <para>
/// <strong>It writes, which the read-only administration controller does not</strong> - see
/// <c>AdminController</c>'s note that moderation has its own surface with its own audit trail.
/// This is that surface: every decision goes through <see cref="ServerEvents"/>, naming the
/// operator, what was judged and which way it went.
/// </para>
/// </summary>
[ApiController]
[Authorize(Policy = AdminPolicies.Admin)]
public sealed class AdminModerationController : ControllerBase
{
	[HttpGet("/api/v1/admin/reports", Name = AdminModerationEndpoints.QueueRouteName)]
	[EndpointSummary("Reported content waiting to be reviewed.")]
	public async Task<IActionResult> QueueAsync(
		[FromServices] DlrDbContext database,
		CancellationToken cancellationToken)
	{
		// Oldest first. The queue is a promise about response time, so the one that has been
		// waiting longest is the one the promise is closest to breaking on.
		//
		// Projected rather than Include'd: the reporter is wanted for one column, and AppUser is
		// thirty of them including the password hash. Nothing behind /api/v1/admin should carry
		// that across the wire when a name would do (ApiSurfaceRules).
		var open = await database
			.Set<ContentReport>()
			.AsNoTracking()
			.Where(report => report.ResolvedUtc == null)
			.OrderBy(report => report.CreatedUtc)
			.Take(AdminModerationEndpoints.MaxQueueSize)
			.Select(report => new
			{
				report.Id,
				report.TargetKind,
				report.TargetId,
				report.GroupRideId,
				report.AuthorId,
				report.Reason,
				report.ContentSnapshot,
				report.CreatedUtc,
				ReportedByUserName = report.ReportedBy!.UserName,
			})
			.ToListAsync(cancellationToken);

		if (open.Count == 0)
		{
			return Ok(Array.Empty<OpenReport>());
		}

		// The authors in one query rather than an Include per row: AuthorId is deliberately not a
		// foreign key (see ContentReport), so EF cannot navigate to it and there is nothing to join.
		List<Guid> authorIds =
		[
			.. open
				.Where(report => report.AuthorId is not null)
				.Select(report => report.AuthorId!.Value)
				.Distinct(),
		];

		// Two columns, not the row. ToDictionaryAsync's selectors are delegates rather than
		// expressions, so without the Select they run after a SELECT * has already been materialised.
		Dictionary<Guid, string> authors = await database
			.Set<AppUser>()
			.AsNoTracking()
			.Where(user => authorIds.Contains(user.Id))
			.Select(user => new { user.Id, Name = user.UserName! })
			.ToDictionaryAsync(row => row.Id, row => row.Name, cancellationToken);

		Dictionary<Guid, int> perTarget = open
			.GroupBy(report => report.TargetId)
			.ToDictionary(group => group.Key, group => group.Count());

		return Ok(open
			.Select(report => new OpenReport(
				report.Id,
				report.TargetKind.ToString(),
				report.TargetId,
				report.AuthorId is { } authorId && authors.TryGetValue(authorId, out string? name) ? name : null,
				report.ReportedByUserName ?? "a deleted account",
				report.Reason,
				report.ContentSnapshot,
				report.CreatedUtc,
				perTarget[report.TargetId]))
			.ToList());
	}

	[HttpPost("/api/v1/admin/reports/{id:guid}/restore", Name = AdminModerationEndpoints.RestoreRouteName)]
	[EndpointSummary("Clears a report and puts the content back.")]
	public Task<IActionResult> RestoreAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] TimeProvider clock,
		[FromServices] ServerEvents events,
		[FromServices] IHubContext<RideHub, IRideClient> hub,
		[FromServices] IOptions<CommentOptions> comments,
		CancellationToken cancellationToken) =>
		JudgeAsync(database, clock, events, hub, comments.Value, id, remove: false, cancellationToken);

	[HttpPost("/api/v1/admin/reports/{id:guid}/remove", Name = AdminModerationEndpoints.RemoveRouteName)]
	[EndpointSummary("Deletes the reported content and clears the report.")]
	public Task<IActionResult> RemoveAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] TimeProvider clock,
		[FromServices] ServerEvents events,
		[FromServices] IHubContext<RideHub, IRideClient> hub,
		[FromServices] IOptions<CommentOptions> comments,
		CancellationToken cancellationToken) =>
		JudgeAsync(database, clock, events, hub, comments.Value, id, remove: true, cancellationToken);

	/// <summary>
	/// Both decisions, because they differ in one step and share five.
	/// <para>
	/// <strong>Every open report on the same content is resolved, not only the one pressed.</strong>
	/// The hold is "has an unresolved report", so clearing one of three would put the content back
	/// nowhere and leave an operator pressing Restore at a row that never changes.
	/// </para>
	/// </summary>
	private async Task<IActionResult> JudgeAsync(
		DlrDbContext database,
		TimeProvider clock,
		ServerEvents events,
		IHubContext<RideHub, IRideClient> hub,
		CommentOptions limits,
		Guid reportId,
		bool remove,
		CancellationToken cancellationToken)
	{
		ContentReport? report = await database
			.Set<ContentReport>()
			.AsNoTracking()
			.SingleOrDefaultAsync(row => row.Id == reportId, cancellationToken);

		if (report is null)
		{
			return NotFound();
		}

		if (report.ResolvedUtc is not null)
		{
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "Already dealt with",
				detail: "Another operator has judged this one. Reload the queue.");
		}

		if (remove)
		{
			await DeleteContentAsync(database, report, cancellationToken);
		}

		List<ContentReport> siblings = await database
			.Set<ContentReport>()
			.Where(row => row.TargetKind == report.TargetKind
				&& row.TargetId == report.TargetId
				&& row.ResolvedUtc == null)
			.ToListAsync(cancellationToken);

		DateTimeOffset now = clock.GetUtcNow();

		foreach (ContentReport sibling in siblings)
		{
			sibling.ResolvedUtc = now;
		}

		await database.SaveChangesAsync(cancellationToken);

		// Both broadcasts wait for the commit, and for the same reason: until it lands, the next
		// read would still disagree with whatever the clients had just been told.
		await (remove
			? AnnounceRemovedAsync(hub, report)
			: AnnounceRestoredAsync(database, hub, limits, report, cancellationToken));

		// The audit trail the read-only administration controller says moderation has to carry.
		// Which way it went is the part somebody asks about a month later.
		events.Note(
			ServerEvents.Areas.Moderation,
			$"{User.Identity?.Name ?? "an administrator"} {(remove ? "removed" : "restored")} a reported "
			+ $"{report.TargetKind.ToString().ToLowerInvariant()}, closing {siblings.Count} report(s).");

		return Ok(new ReportResolved(siblings.Count, remove));
	}

	/// <summary>
	/// Puts cleared content back on every open client (§17.7).
	/// <para>
	/// <strong>The inverse of the message a report sends, and the reason the hold is defensible.</strong>
	/// Reporting broadcasts a removal, so without this an operator clearing a bad report changes
	/// nothing anybody can see until they reopen the adventure - a falsely reported rider stays
	/// silenced for the life of every session already running, and a restore that had stopped
	/// working would look exactly the same as one that worked.
	/// </para>
	/// <para>
	/// A post goes back as <c>CommentRestored</c> rather than <c>CommentPosted</c> because the
	/// latter is what raises a notification; see that message. A marker has no such problem, so it
	/// reuses <c>MarkerAdded</c>, which clients already treat as an upsert.
	/// </para>
	/// </summary>
	private static async Task AnnounceRestoredAsync(
		DlrDbContext database,
		IHubContext<RideHub, IRideClient> hub,
		CommentOptions limits,
		ContentReport report,
		CancellationToken cancellationToken)
	{
		// Only content that was in an adventure. A shared route's thread has no ride group to send
		// to, and its readers pick the post up on their next read.
		if (report.GroupRideId is not { } rideId)
		{
			return;
		}

		// Gone since it was reported - an organiser deleted it while it sat in the queue. The
		// report still had to close; there is simply nothing to put back.
		if (!await StillExistsAsync(database, report, cancellationToken))
		{
			return;
		}

		if (report.TargetKind == ReportTargetKind.Comment)
		{
			await hub.Clients
				.Group(RideHub.Group(rideId))
				.CommentRestored(await CommentController.DescribeAsync(database, limits, report.TargetId));
		}
		else
		{
			await hub.Clients
				.Group(RideHub.Group(rideId))
				.MarkerAdded(rideId, await MarkerController.DescribeAsync(database, report.TargetId));
		}
	}

	private static Task<bool> StillExistsAsync(
		DlrDbContext database,
		ContentReport report,
		CancellationToken cancellationToken) =>
		report.TargetKind == ReportTargetKind.Comment
			? database.Set<RideComment>().AnyAsync(row => row.Id == report.TargetId, cancellationToken)
			: database.Set<Marker>().AnyAsync(row => row.Id == report.TargetId, cancellationToken);

	/// <summary>
	/// Marks the content a report is about for deletion, in the same save as the report's closure.
	/// <para>
	/// A row that has already gone is not an error: an organiser may have deleted the comment
	/// themselves while it sat in the queue, and the report still has to close. The snapshot on the
	/// report is what the decision was made from either way, which is the whole reason it is stored.
	/// </para>
	/// </summary>
	private static async Task DeleteContentAsync(
		DlrDbContext database,
		ContentReport report,
		CancellationToken cancellationToken)
	{
		object? content = report.TargetKind == ReportTargetKind.Comment
			? await database.Set<RideComment>()
				.SingleOrDefaultAsync(row => row.Id == report.TargetId, cancellationToken)
			: await database.Set<Marker>()
				.SingleOrDefaultAsync(row => row.Id == report.TargetId, cancellationToken);

		if (content is not null)
		{
			database.Remove(content);
		}
	}

	/// <summary>
	/// Tells every open client the content has gone - the same message an ordinary delete sends, so
	/// nothing about it says a report was involved.
	/// </summary>
	private static async Task AnnounceRemovedAsync(
		IHubContext<RideHub, IRideClient> hub,
		ContentReport report)
	{
		// Only for content that was in an adventure. A shared route's thread has no ride group to
		// send to, and its readers pick the removal up on their next read - which the hold has been
		// answering all along anyway.
		if (report.GroupRideId is not { } rideId)
		{
			return;
		}

		if (report.TargetKind == ReportTargetKind.Comment)
		{
			await hub.Clients.Group(RideHub.Group(rideId)).CommentRemoved(report.TargetId);
		}
		else
		{
			await hub.Clients.Group(RideHub.Group(rideId)).MarkerRemoved(rideId, report.TargetId);
		}
	}
}
