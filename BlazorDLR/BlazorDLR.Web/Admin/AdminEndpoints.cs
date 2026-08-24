using DLR.Core.Contracts.Admin;
using DLR.Server.Data;
using DLR.Server.Data.Comments;
using DLR.Server.Data.Identity;
using DLR.Server.Data.Markers;
using DLR.Server.Data.Photos;
using DLR.Server.Data.Positions;
using DLR.Server.Data.Rides;
using DLR.Server.Data.Tracks;
using DLR.Server.Diagnostics;
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

	/// <summary>The most accounts one page may carry, whatever a caller asks for.</summary>
	public const int MaxPageSize = 200;
}

/// <summary>
/// What the people running the server can see about it (§14.6).
/// <para>
/// <strong>Every route here is read-only.</strong> There is no endpoint to suspend an account,
/// edit a ride or delete a photograph, and that is deliberate rather than unfinished: moderation
/// already has its own surface with its own audit trail, and an administration screen that could
/// quietly change data would be a second one without it.
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
		[FromQuery] string? search,
		[FromQuery] int skip,
		[FromQuery] int take,
		CancellationToken cancellationToken)
	{
		int limit = take <= 0 ? 50 : Math.Min(take, AdminEndpoints.MaxPageSize);

		IQueryable<AppUser> accounts = database.Set<AppUser>();

		if (!string.IsNullOrWhiteSpace(search))
		{
			string pattern = $"%{search.Trim()}%";

			// ILIKE through EF.Functions rather than ToLower().Contains(), which cannot use an
			// index and would table-scan every account on every keystroke.
			accounts = accounts.Where(user => EF.Functions.ILike(user.UserName!, pattern));
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
				Held = database.Set<RiderPosition>().Count(position => position.UserId == user.Id),
				Seconds = database
					.Set<Track>()
					.Where(track => track.OwnerId == user.Id)
					.Sum(track => track.DurationS),
			})
			.ToListAsync(cancellationToken);

		// Read once for the page rather than per row: the roster is an options-monitor lookup and a
		// scan, and a 200-row page would otherwise repeat both 200 times to answer one bool column.
		IReadOnlySet<string> admins = roster.Everyone();

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
				PositionsHeld: row.Held,
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
		CancellationToken cancellationToken) =>
		Ok(await reader.ReadAsync(day, take <= 0 ? 200 : take, level, cancellationToken));

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

		int liveRides = await database
			.Set<GroupRide>()
			.CountAsync(ride => ride.State == GroupRideState.Live, cancellationToken);

		// Distinct riders, not rows: somebody sharing with three adventures is one person out on a
		// road, and this is the number that answers "is anybody riding right now".
		HashSet<Guid> sharing = [];

		foreach (Guid rideId in cache.RideIds())
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
			LiveRides: liveRides,
			PositionsPerMinute: perMinute,
			WindowStartUtc: windowStart,
			MeterStartedUtc: meter.StartedUtc));
	}
}
