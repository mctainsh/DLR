using System.Linq.Expressions;
using DLR.Core.Contracts.Announcements;
using DLR.Server.Data;
using DLR.Server.Data.Announcements;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Announcements;

/// <summary>
/// How the <c>announcement</c> table is read (§20.2).
/// <para>
/// <strong>The sweep deliberately does not share <see cref="Live"/>.</strong> Its window is
/// <c>(lastTick, now]</c> - what became live since it last looked - which is a different question
/// from "what is live now", and collapsing the two would make it re-send on every tick. So this
/// holds the launch endpoint's window, and the one projection both paths hand to a rider.
/// </para>
/// </summary>
public static class AnnouncementQueries
{
	/// <summary>Announcements a rider should be seeing at <paramref name="now"/>, worst first.</summary>
	/// <param name="database">The context.</param>
	/// <param name="now">The server clock (§10.4).</param>
	public static IQueryable<Announcement> Live(DlrDbContext database, DateTimeOffset now) =>
		database
			.Set<Announcement>()
			.AsNoTracking()
			.Where(announcement => announcement.PublishFromUtc <= now && announcement.ExpiresUtc > now)
			.OrderByDescending(announcement => announcement.Severity)
			.ThenBy(announcement => announcement.PublishFromUtc);

	/// <summary>The projection, as an expression so it translates rather than materialising rows.</summary>
	public static Expression<Func<Announcement, AnnouncementDto>> ToDto { get; } =
		announcement => new AnnouncementDto(
			announcement.Id,
			announcement.Severity,
			announcement.Title,
			announcement.Body,
			announcement.ExpiresUtc);
}
