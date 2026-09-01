using DLR.Core.Contracts.Announcements;
using DLR.Server.Admin;
using DLR.Server.Data;
using DLR.Server.Data.Announcements;
using DLR.Server.Diagnostics;
using DLR.Server.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Announcements;

/// <summary>Route names for the announcement screens (§20.2).</summary>
public static class AdminAnnouncementEndpoints
{
	/// <summary>Route name for the list.</summary>
	public const string ListRouteName = "AdminAnnouncements";

	/// <summary>Route name for writing one.</summary>
	public const string CreateRouteName = "AdminCreateAnnouncement";

	/// <summary>Route name for amending one.</summary>
	public const string UpdateRouteName = "AdminUpdateAnnouncement";

	/// <summary>Route name for deleting one.</summary>
	public const string DeleteRouteName = "AdminDeleteAnnouncement";

	/// <summary>
	/// The most rows the history returns. A cap rather than a page: nothing asks for a second one,
	/// and a screen that grows past this wants paging designed for it rather than two query
	/// parameters nobody sends.
	/// </summary>
	public const int PageSize = 200;
}

/// <summary>
/// Writing what every rider sees (§20.2).
/// <para>
/// <strong>Why this controller writes when <see cref="AdminEndpoints"/> deliberately does not.</strong>
/// That controller's rule is that administration must not quietly change data somebody else owns -
/// moderation has its own surface with its own audit trail, and a second one without it would be a
/// hole. An announcement is not somebody else's data: it is content this screen authors, and there
/// is nowhere else it could come from. The audit line is written all the same.
/// </para>
/// </summary>
[ApiController]
[Authorize(Policy = AdminPolicies.Admin)]
public sealed class AdminAnnouncementController : ControllerBase
{
	/// <summary>Every announcement, live or not, newest window first.</summary>
	/// <param name="database">The context.</param>
	/// <param name="cancellationToken">Abandons the query.</param>
	/// <remarks>
	/// Expired rows are included on purpose. This is the screen that answers "what did we tell
	/// people, and when" - a list that hid everything past its expiry would answer it only for the
	/// few days a message was up.
	/// </remarks>
	[HttpGet("/api/v1/admin/announcements", Name = AdminAnnouncementEndpoints.ListRouteName)]
	[EndpointSummary("Every announcement written on this server, live or not.")]
	public async Task<ActionResult<IReadOnlyList<AdminAnnouncement>>> List(
		[FromServices] DlrDbContext database,
		CancellationToken cancellationToken) =>
		Ok(await database
			.Set<Announcement>()
			.AsNoTracking()
			.OrderByDescending(announcement => announcement.PublishFromUtc)
			.Take(AdminAnnouncementEndpoints.PageSize)
			.Select(announcement => new AdminAnnouncement(
				announcement.Id,
				announcement.Severity,
				announcement.Title,
				announcement.Body,
				announcement.PublishFromUtc,
				announcement.ExpiresUtc,
				announcement.CreatedUtc,
				announcement.CreatedBy!.UserName))
			.ToListAsync(cancellationToken));

	/// <summary>Writes one.</summary>
	/// <param name="request">What it says and when it runs.</param>
	/// <param name="database">The context.</param>
	/// <param name="clock">Stamps the row (§10.4).</param>
	/// <param name="events">The audit line.</param>
	/// <param name="cancellationToken">Abandons the write.</param>
	/// <remarks>
	/// It is not sent from here. <see cref="AnnouncementBroadcastService"/> picks it up on its next
	/// sweep, which is the same path a scheduled one takes - see that class for why there is only
	/// one.
	/// </remarks>
	[HttpPost("/api/v1/admin/announcements", Name = AdminAnnouncementEndpoints.CreateRouteName)]
	[EndpointSummary("Writes an announcement. Riders see it from its publish-from instant.")]
	public async Task<ActionResult<AdminAnnouncement>> Create(
		[FromBody] AdminAnnouncementRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] TimeProvider clock,
		[FromServices] ServerEvents events,
		CancellationToken cancellationToken)
	{
		if (Refuse(request) is { } refusal) return refusal;

		DateTimeOffset now = clock.GetUtcNow();

		Announcement announcement = new()
		{
			Id = Guid.NewGuid(),
			Severity = request.Severity,
			Title = request.Title.Trim(),
			Body = request.Body.Trim(),
			PublishFromUtc = request.PublishFromUtc,
			ExpiresUtc = request.ExpiresUtc,
			CreatedUtc = now,
			CreatedByUserId = User.UserId(),
		};

		database.Add(announcement);
		await database.SaveChangesAsync(cancellationToken);

		string caller = Caller();

		events.Note(
			ServerEvents.Areas.Admin,
			$"{caller} wrote the announcement \"{announcement.Title}\", live from "
				+ $"{announcement.PublishFromUtc:u} until {announcement.ExpiresUtc:u}.");

		return Ok(new AdminAnnouncement(
			announcement.Id,
			announcement.Severity,
			announcement.Title,
			announcement.Body,
			announcement.PublishFromUtc,
			announcement.ExpiresUtc,
			announcement.CreatedUtc,
			caller));
	}

	/// <summary>Amends one.</summary>
	/// <param name="id">Which announcement.</param>
	/// <param name="request">What it should say now.</param>
	/// <param name="database">The context.</param>
	/// <param name="events">The audit line.</param>
	/// <param name="cancellationToken">Abandons the write.</param>
	/// <remarks>
	/// <strong>An amendment does not reach a rider who has already cleared it.</strong> A device
	/// records a dismissal by id, and the id does not change - so correcting the wording of a
	/// message that has been up for an hour corrects it for the people who have not seen it yet.
	/// To reach everybody again, delete this one and write another.
	/// </remarks>
	[HttpPut("/api/v1/admin/announcements/{id:guid}", Name = AdminAnnouncementEndpoints.UpdateRouteName)]
	[EndpointSummary("Amends an announcement. Riders who cleared it do not see it again.")]
	public async Task<IActionResult> Update(
		[FromRoute] Guid id,
		[FromBody] AdminAnnouncementRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] ServerEvents events,
		CancellationToken cancellationToken)
	{
		if (Refuse(request) is { } refusal) return refusal;

		Announcement? announcement = await database
			.Set<Announcement>()
			.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);

		if (announcement is null) return NotFound();

		announcement.Severity = request.Severity;
		announcement.Title = request.Title.Trim();
		announcement.Body = request.Body.Trim();
		announcement.PublishFromUtc = request.PublishFromUtc;
		announcement.ExpiresUtc = request.ExpiresUtc;

		await database.SaveChangesAsync(cancellationToken);

		events.Note(
			ServerEvents.Areas.Admin,
			$"{Caller()} amended the announcement \"{announcement.Title}\".");

		return NoContent();
	}

	/// <summary>Removes one.</summary>
	/// <param name="id">Which announcement.</param>
	/// <param name="database">The context.</param>
	/// <param name="events">The audit line.</param>
	/// <param name="cancellationToken">Abandons the write.</param>
	/// <remarks>
	/// It stops being served from the next launch check, and stops being swept. A rider whose app
	/// already has it on screen keeps it there until they clear it - there is no unsend, and
	/// inventing one would mean a second hub message for a case nobody is waiting on.
	/// </remarks>
	[HttpDelete("/api/v1/admin/announcements/{id:guid}", Name = AdminAnnouncementEndpoints.DeleteRouteName)]
	[EndpointSummary("Removes an announcement.")]
	public async Task<IActionResult> Delete(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] ServerEvents events,
		CancellationToken cancellationToken)
	{
		Announcement? announcement = await database
			.Set<Announcement>()
			.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);

		if (announcement is null) return NotFound();

		database.Remove(announcement);
		await database.SaveChangesAsync(cancellationToken);

		events.Note(
			ServerEvents.Areas.Admin,
			$"{Caller()} removed the announcement \"{announcement.Title}\".");

		return NoContent();
	}

	/// <summary>
	/// The refusals both writes share, or null when the request is fit to store.
	/// </summary>
	/// <param name="request">What was sent.</param>
	private ObjectResult? Refuse(AdminAnnouncementRequest request)
	{
		if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Body))
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "Nothing to say",
				detail: "An announcement needs a heading and a message.");
		}

		if (request.Title.Trim().Length > AnnouncementLimits.MaxTitleChars
			|| request.Body.Trim().Length > AnnouncementLimits.MaxBodyChars)
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "Too long",
				detail: $"A heading is at most {AnnouncementLimits.MaxTitleChars} characters and a "
					+ $"message at most {AnnouncementLimits.MaxBodyChars}.");
		}

		// A window that never opens is a message that silently never appears, and the screen that
		// wrote it would show it sitting in the list looking fine.
		if (request.ExpiresUtc <= request.PublishFromUtc)
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "That window never opens",
				detail: "An announcement has to expire after it starts appearing, not before.");
		}

		return null;
	}

	/// <summary>
	/// Who is doing this, for the log. From the token's own claim rather than
	/// <c>Identity.Name</c>: inbound claim mapping is off and the name is spelled <c>unm</c>
	/// (§7.4), so the framework's lookup finds nothing here.
	/// </summary>
	private string Caller() =>
		User.FindFirst(DlrClaims.UserName)?.Value ?? "an administrator";
}
