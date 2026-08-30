using DLR.Core.Contracts.Admin;
using DLR.Server.Data;
using DLR.Server.Data.Comments;
using DLR.Server.Data.Identity;
using DLR.Server.Data.Markers;
using DLR.Server.Data.Photos;
using DLR.Server.Data.Rides;
using DLR.Server.Data.Tracks;
using DLR.Server.Diagnostics;
using DLR.Server.Identity;
using DLR.Server.Positions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Admin;

/// <summary>Route names for the administration screens (§14.6).</summary>
public static class AdminEndpoints
{
	/// <summary>Route name for the account list.</summary>
	public const string UsersRouteName = "AdminUsers";

	/// <summary>Route name for the server log.</summary>
	public const string LogsRouteName = "AdminLogs";

	/// <summary>Route name for the service statistics.</summary>
	public const string StatsRouteName = "AdminStats";

	/// <summary>Route name for deleting an account.</summary>
	public const string DeleteUserRouteName = "AdminDeleteUser";

	/// <summary>The most accounts one page may carry, whatever a caller asks for.</summary>
	public const int MaxPageSize = 200;
}

/// <summary>
/// What the people running the server can see about it (§14.6).
/// <para>
/// <strong>Every route here reads except one.</strong> There is still no endpoint to suspend an
/// account, edit a ride or delete a photograph, for the reason this section used to give for
/// allowing nothing at all: moderation has its own surface with its own audit trail, and an
/// administration screen that could quietly change data would be a second one without it.
/// </para>
/// <para>
/// <see cref="DeleteUser"/> is the exception, and it is one because erasure is not moderation.
/// §10.2 obliges this server to delete an account on request, and a rider who has lost their
/// password cannot reach <c>DELETE /api/v1/me</c> to ask — leaving the operator to do it with a
/// database client, unlogged and by hand. So it writes a line through <see cref="ServerEvents"/>
/// rather than being silent, and it shares its implementation with the rider's own delete rather
/// than being a second erasure that might miss something.
/// </para>
/// <para>
/// The whole controller is behind <see cref="AdminPolicies.Admin"/> — see that policy for why the
/// roster is read per request rather than carried in the token.
/// </para>
/// </summary>
[ApiController]
[Authorize(Policy = AdminPolicies.Admin)]
public sealed class AdminController : ControllerBase
{
	/// <summary>
	/// Every account, with what it has put into the service.
	/// </summary>
	/// <param name="database">The context.</param>
	/// <param name="roster">Marks which rows are administrators.</param>
	/// <param name="search">Filters by username, case-insensitively. Blank returns everybody.</param>
	/// <param name="skip">How many rows to step over.</param>
	/// <param name="take">How many to return, clamped to <see cref="AdminEndpoints.MaxPageSize"/>.</param>
	/// <param name="cancellationToken">Abandons the query.</param>
	/// <returns>One row per account, most recently active first.</returns>
	/// <remarks>
	/// One query, with the counts as correlated sub-selects rather than as joins or a second round
	/// trip per row. A page of 50 accounts is one statement; the obvious alternative — load the
	/// accounts, then count each one's tracks — is 50 statements, and it is the kind of thing that
	/// looks fine on a developer's four-row database.
	/// </remarks>
	[HttpGet("/api/v1/admin/users", Name = AdminEndpoints.UsersRouteName)]
	[EndpointSummary("Every account, with its activity and content counts.")]
	public async Task<ActionResult<IReadOnlyList<AdminUserRow>>> Users(
		[FromServices] DlrDbContext database,
		[FromServices] AdminRoster roster,
		[FromServices] RiderPositionCache cache,
		[FromQuery] string? search,
		[FromQuery] int skip,
		[FromQuery] int take,
		CancellationToken cancellationToken)
	{
		int limit = take <= 0 ? 50 : Math.Min(take, AdminEndpoints.MaxPageSize);

		IQueryable<AppUser> accounts = database.Set<AppUser>();

		if (!string.IsNullOrWhiteSpace(search))
		{
			string pattern = $"%{Escape(search.Trim())}%";

			// ILIKE through EF.Functions rather than ToLower().Contains(), which cannot use an
			// index and would table-scan every account on every keystroke.
			accounts = accounts.Where(user => EF.Functions.ILike(user.UserName!, pattern, EscapeCharacter));
		}

		var rows = await accounts
			.OrderByDescending(user => user.LastActiveUtc)
			.ThenBy(user => user.UserName)
			.Skip(Math.Max(0, skip))
			.Take(limit)
			.Select(user => new
			{
				user.Id,
				user.UserName,
				user.Email,
				user.EmailConfirmed,
				user.CreatedUtc,
				user.LastActiveUtc,
				user.PositionsRecorded,
				Adventures = database.Set<GroupRide>().Count(ride => ride.OwnerId == user.Id),
				Routes = database.Set<Track>().Count(track => track.OwnerId == user.Id),
				Posts = database.Set<RideComment>().Count(comment => comment.AuthorId == user.Id),
				Photos = database.Set<Photo>().Count(photo => photo.OwnerId == user.Id),
				Markers = database.Set<Marker>().Count(marker => marker.CreatedByUserId == user.Id),
				Devices = database.Set<Device>().Count(device => device.UserId == user.Id),
				Seconds = database
					.Set<Track>()
					.Where(track => track.OwnerId == user.Id)
					.Sum(track => track.DurationS),
			})
			.ToListAsync(cancellationToken);

		// Read once for the page rather than per row: the roster is an options-monitor lookup and a
		// scan, and a 200-row page would otherwise repeat both 200 times to answer one bool column.
		IReadOnlySet<string> admins = roster.Everyone();

		// From the cache, because that is the only place a position is (§5.5) — and read once for
		// the page for the same reason the roster above is: a correlated count per row would be a
		// scan each, to answer one column.
		Dictionary<Guid, int> held = [];

		foreach (Guid rideId in cache.RideIds())
		{
			foreach (Guid riderId in cache.RiderIds(rideId))
			{
				held[riderId] = held.GetValueOrDefault(riderId) + 1;
			}
		}

		// Projected by hand, and it has to be: ApiSurfaceRules forbids an AppUser reaching a
		// response factory, because the password hash and the security stamp travel with it.
		return Ok(rows
			.Select(row => new AdminUserRow(
				UserId: row.Id,
				UserName: row.UserName ?? string.Empty,
				Email: row.Email,
				EmailConfirmed: row.EmailConfirmed,
				CreatedUtc: row.CreatedUtc,
				LastActiveUtc: row.LastActiveUtc,
				PositionsRecorded: row.PositionsRecorded,
				PositionsHeld: held.GetValueOrDefault(row.Id),
				Adventures: row.Adventures,
				Routes: row.Routes,
				Posts: row.Posts,
				Photos: row.Photos,
				Markers: row.Markers,
				TrackedHours: Math.Round((row.Seconds ?? 0) / 3600.0, 1),
				Devices: row.Devices,
				IsAdmin: admins.Contains(row.UserName ?? string.Empty)))
			.ToList());
	}

	/// <summary>
	/// The tail of the server's log for one day (§14.6).
	/// </summary>
	/// <param name="reader">Reads the file. It owns the directory — see its note on why.</param>
	/// <param name="day">Which day, <c>yyyy-MM-dd</c> in UTC. Omitted reads the newest available.</param>
	/// <param name="level">Lowest level to include, or omitted for everything.</param>
	/// <param name="take">How many lines, newest first.</param>
	/// <param name="databaseCommands">
	/// Whether EF Core's statement lines are included. Omitted means yes — the log as written is
	/// the honest default for a caller that did not ask for a filter. The screen asks for
	/// <c>false</c>, because filtering here rather than after the page arrives is what lets
	/// <paramref name="take"/> buy a day of the interesting lines instead of a few minutes of SQL.
	/// </param>
	/// <param name="cancellationToken">Abandons the read.</param>
	/// <returns>The page, empty when file logging is off or that day has no file.</returns>
	/// <remarks>
	/// A date, never a filename. The reader builds every path it opens from this value, which is
	/// what stops the endpoint becoming "return the contents of a file of your choosing" for
	/// whoever the roster happens to name.
	/// </remarks>
	[HttpGet("/api/v1/admin/logs", Name = AdminEndpoints.LogsRouteName)]
	[EndpointSummary("The newest entries in the server's log file.")]
	public async Task<ActionResult<AdminLogPage>> Logs(
		[FromServices] ServerLogReader reader,
		[FromQuery] DateOnly? day,
		[FromQuery] string? level,
		[FromQuery] int take,
		[FromQuery] bool? databaseCommands,
		CancellationToken cancellationToken) =>
		Ok(await reader.ReadAsync(
			day, take <= 0 ? 200 : take, level, databaseCommands ?? true, cancellationToken));

	/// <summary>
	/// What the service is doing right now (§5.5, §7.10).
	/// </summary>
	/// <param name="database">The context.</param>
	/// <param name="cache">Who has a live position, for the only "active" number that means "now".</param>
	/// <param name="meter">The per-minute graph.</param>
	/// <param name="clock">Anchors the three activity windows (§10.4).</param>
	/// <param name="cancellationToken">Abandons the query.</param>
	/// <returns>The counts and the day's per-minute series.</returns>
	[HttpGet("/api/v1/admin/stats", Name = AdminEndpoints.StatsRouteName)]
	[EndpointSummary("Account activity, live rides and GPS fixes per minute.")]
	public async Task<ActionResult<AdminStats>> Stats(
		[FromServices] DlrDbContext database,
		[FromServices] RiderPositionCache cache,
		[FromServices] PositionActivityMeter meter,
		[FromServices] TimeProvider clock,
		CancellationToken cancellationToken)
	{
		DateTimeOffset now = clock.GetUtcNow();

		// One round trip for the four account numbers. Four counts of the same table with different
		// predicates is four scans; summing conditionals is one.
		var accounts = await database
			.Set<AppUser>()
			.GroupBy(_ => 1)
			.Select(all => new
			{
				Total = all.Count(),
				Day = all.Count(user => user.LastActiveUtc >= now.AddDays(-1)),
				Week = all.Count(user => user.LastActiveUtc >= now.AddDays(-7)),
				Month = all.Count(user => user.LastActiveUtc >= now.AddDays(-30)),
			})
			.FirstOrDefaultAsync(cancellationToken);

		// Distinct riders, not rows: somebody sharing with three adventures is one person out on a
		// road, and this is the number that answers "is anybody riding right now".
		HashSet<Guid> sharing = [];

		// RideIds already drops the rides holding nothing, so its count is the second figure.
		IReadOnlyList<Guid> rideIds = cache.RideIds();

		foreach (Guid rideId in rideIds)
		{
			// RiderIds rather than ForRide: the latter copies every rider's PositionEntry out of the
			// cache, and this loop wants the keys and throws the values away.
			foreach (Guid userId in cache.RiderIds(rideId))
			{
				sharing.Add(userId);
			}
		}

		IReadOnlyList<int> perMinute = meter.PerMinute(out DateTimeOffset windowStart);

		return Ok(new AdminStats(
			UsersTotal: accounts?.Total ?? 0,
			ActiveLastDay: accounts?.Day ?? 0,
			ActiveLastWeek: accounts?.Week ?? 0,
			ActiveLastMonth: accounts?.Month ?? 0,
			RidersSharingNow: sharing.Count,
			RidesSharingNow: rideIds.Count,
			PositionsPerMinute: perMinute,
			WindowStartUtc: windowStart,
			MeterStartedUtc: meter.StartedUtc));
	}

	/// <summary>
	/// Deletes somebody else's account, with everything it owns (§14.6, §6.3).
	/// </summary>
	/// <param name="id">Which account.</param>
	/// <param name="request">The handle the caller last saw against <paramref name="id"/>.</param>
	/// <param name="database">The context.</param>
	/// <param name="roster">Who may not be deleted this way.</param>
	/// <param name="deletion">The one implementation of erasure (§6.3).</param>
	/// <param name="events">The audit line.</param>
	/// <param name="cancellationToken">Abandons the delete.</param>
	/// <remarks>
	/// <para>
	/// <strong>An account on the roster cannot be deleted here, and that is a security guard
	/// rather than deference.</strong> The roster names administrators by username, and deleting
	/// an account frees its username for anybody to register (§7.2) — so deleting a fellow
	/// administrator turns the roster entry into a trap that promotes whoever claims the name
	/// next. Take them off the roster in configuration first, and the deletion is then an ordinary
	/// one.
	/// </para>
	/// <para>
	/// <strong>No password, and the caller's own account is refused.</strong> An administrator
	/// cannot know somebody else's password, so the re-entry <c>DELETE /api/v1/me</c> demands
	/// cannot apply here — which is exactly why deleting yourself is sent back to that endpoint
	/// rather than made easy from a screen full of other people's rows.
	/// </para>
	/// </remarks>
	[HttpDelete("/api/v1/admin/users/{id:guid}", Name = AdminEndpoints.DeleteUserRouteName)]
	[EndpointSummary("Deletes an account, its content and its blobs. Irreversible.")]
	public async Task<IActionResult> DeleteUser(
		[FromRoute] Guid id,
		[FromBody] AdminDeleteUserRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] AdminRoster roster,
		[FromServices] Account.AccountDeletion deletion,
		[FromServices] ServerEvents events,
		CancellationToken cancellationToken)
	{
		if (User.UserId() == id)
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "Not from here",
				detail: "Deleting your own account is done from Settings, where it asks for your password.");
		}

		AppUser? target = await database
			.Set<AppUser>()
			.AsNoTracking()
			.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);

		if (target is null)
		{
			return NotFound();
		}

		if (roster.IsAdmin(target.UserName))
		{
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "Administrator account",
				detail: $"{target.UserName} is named in this server's Admins roster. Remove them from "
					+ "the configuration first — deleting the account while it is listed would free "
					+ "the username for somebody else to register and inherit the roster entry.");
		}

		// Both halves have to describe the same account. The list is searched and paged, so a row
		// on a stale screen can point at an id whose name has moved on under it.
		if (!string.Equals(target.UserName, request.UserName?.Trim(), StringComparison.OrdinalIgnoreCase))
		{
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "That is not who you were looking at",
				detail: "The account behind that row has changed since the list was loaded. Reload and try again.");
		}

		await deletion.DeleteAsync(id, cancellationToken);

		// The one write this controller does, so it is the one thing here worth a line in the log
		// an administrator reads afterwards. Both names, because "who deleted whom" is the whole
		// of the question anybody asks about it later.
		events.Note(
			ServerEvents.Areas.Admin,
			$"{User.Identity?.Name ?? "an administrator"} deleted the account {target.UserName}.");

		return NoContent();
	}

	/// <summary>The backslash, as the escape character handed to <c>ILIKE</c>.</summary>
	private const string EscapeCharacter = "\\";

	/// <summary>
	/// Escapes what an administrator typed so <c>%</c> and <c>_</c> are the characters they typed
	/// rather than wildcards. Without this, a search for <c>_</c> matches every one-character
	/// username and one for <c>%</c> matches every account there is.
	/// </summary>
	/// <param name="value">The trimmed search text.</param>
	private static string Escape(string value) => value
		.Replace("\\", "\\\\", StringComparison.Ordinal)
		.Replace("%", "\\%", StringComparison.Ordinal)
		.Replace("_", "\\_", StringComparison.Ordinal);
}
