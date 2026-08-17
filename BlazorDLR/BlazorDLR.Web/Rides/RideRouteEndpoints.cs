using DLR.Core.Contracts.Rides;
using DLR.Core.Tracks;
using DLR.Server.Data;
using DLR.Server.Data.Rides;
using DLR.Server.Data.Tracks;
using DLR.Server.Hubs;
using DLR.Server.Identity;
using DLR.Server.Positions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DLR.Server.Rides;

/// <summary>Route names, so a caller can be generated against them rather than against strings.</summary>
public static class RideRouteEndpoints
{
	/// <summary>Route name for a ride's planned routes.</summary>
	public const string ListRouteName = "ListRideRoutes";

	/// <summary>Route name for attaching one.</summary>
	public const string AddRouteName = "AddRideRoute";

	/// <summary>Route name for detaching one.</summary>
	public const string RemoveRouteName = "RemoveRideRoute";
}

/// <summary>
/// The planned routes of a group ride (§5.2, §5.4).
/// <para>
/// <strong>A set, not a single route.</strong> The outline's <c>PUT /group-rides/{id}/route</c>
/// could only express "this ride has one line on it", and a real day out is commonly two or
/// three — the short option and the long option, the way out and the way home. Attaching is
/// therefore additive and removal is by track id.
/// </para>
/// <para>
/// <strong>Reading is membership, writing is the organiser.</strong> Everybody in the ride needs
/// the lines to draw the map; deciding which lines those are is the same authority as §5.8's
/// content switches, so it is the same owner-or-leader check.
/// </para>
/// </summary>
[ApiController]
[Authorize]
public sealed class RideRouteController : ControllerBase
{
	[HttpGet("/api/v1/group-rides/{id:guid}/routes", Name = RideRouteEndpoints.ListRouteName)]
	[EndpointSummary("A ride's planned routes and the lines the map draws, oldest attachment first.")]
	public async Task<IActionResult> ListAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		bool isMember = await database
			.Set<GroupRideMember>()
			.AnyAsync(member => member.GroupRideId == id && member.UserId == userId);

		// Membership is the whole access model (§5.2), and it is what makes this endpoint the only
		// way a member ever sees a route somebody else owns: GET /tracks/{id} is owner-scoped and
		// answers 404 to everybody else, which is correct there and useless here.
		if (!isMember)
		{
			return NotFound();
		}

		return Ok(await DescribeAllAsync(database, id));
	}

	[HttpPost("/api/v1/group-rides/{id:guid}/routes", Name = RideRouteEndpoints.AddRouteName)]
	[EndpointSummary("Attaches one of the caller's tracks to the ride as a planned route.")]
	public async Task<IActionResult> AddAsync(
		[FromRoute] Guid id,
		[FromBody] AddRideRouteRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] IHubContext<RideHub, IRideClient> hub,
		[FromServices] IOptions<RideOptions> options,
		[FromServices] TimeProvider clock)
	{
		if (await AuthoriseWriteAsync(database, id) is { } refusal)
		{
			return refusal;
		}

		Guid userId = User.UserId()!.Value;

		// The caller's own track, and 404 rather than 403 for anybody else's — the same answer
		// GET /tracks/{id} gives (§15.4). A shared route is handed over as a GPX and imported;
		// that round trip is the copy feature, so nothing here reaches into another library.
		Track? track = await database
			.Set<Track>()
			.AsNoTracking()
			.SingleOrDefaultAsync(row => row.Id == request.TrackId && row.OwnerId == userId);

		if (track is null)
		{
			return Problem(
				statusCode: StatusCodes.Status404NotFound,
				title: "No such track",
				detail: "That track is not one of yours. Import a copy of it if somebody sent it to you.");
		}

		if (!track.IsFullyUploaded)
		{
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "Still uploading",
				detail: "This track is still being uploaded at full resolution. Attach it once that finishes.");
		}

		bool already = await database
			.Set<GroupRideRoute>()
			.AnyAsync(route => route.GroupRideId == id && route.TrackId == request.TrackId);

		if (already)
		{
			// Idempotent rather than a 409: the second tap of a button whose first tap was not
			// acknowledged is the common way this happens, and it has already got what it asked for.
			return Ok((await DescribeAllAsync(database, id)).Single(route => route.TrackId == request.TrackId));
		}

		List<int> positions = await database
			.Set<GroupRideRoute>()
			.Where(route => route.GroupRideId == id)
			.Select(route => route.Position)
			.ToListAsync();

		if (positions.Count >= options.Value.MaxRoutesPerRide)
		{
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "Too many routes",
				detail: $"This adventure already has {positions.Count} routes, which is the limit. Remove one " +
					"before adding another.");
		}

		database.Add(new GroupRideRoute
		{
			GroupRideId = id,
			TrackId = request.TrackId,

			// One past the highest in use, not the count: removing the middle route of three
			// leaves a gap, and reusing a number would put the next attachment somewhere in the
			// middle of a list whose first entry §5.4 treats as the ride's line.
			Position = positions.Count == 0 ? 0 : positions.Max() + 1,
			AddedByUserId = userId,
			AddedUtc = clock.GetUtcNow(),
		});

		await database.SaveChangesAsync();

		await hub.Clients.Group(RideHub.Group(id)).RideRoutesChanged(id);

		IReadOnlyList<RideRoute> routes = await DescribeAllAsync(database, id);

		RideRoute added = routes.Single(route => route.TrackId == request.TrackId);

		return Created($"/api/v1/group-rides/{id}/routes/{request.TrackId}", added);
	}

	[HttpDelete("/api/v1/group-rides/{id:guid}/routes/{trackId:guid}", Name = RideRouteEndpoints.RemoveRouteName)]
	[EndpointSummary("Detaches a planned route. The track itself is untouched.")]
	public async Task<IActionResult> RemoveAsync(
		[FromRoute] Guid id,
		[FromRoute] Guid trackId,
		[FromServices] DlrDbContext database,
		[FromServices] IHubContext<RideHub, IRideClient> hub)
	{
		if (await AuthoriseWriteAsync(database, id) is { } refusal)
		{
			return refusal;
		}

		GroupRideRoute? route = await database
			.Set<GroupRideRoute>()
			.SingleOrDefaultAsync(row => row.GroupRideId == id && row.TrackId == trackId);

		if (route is null)
		{
			return NotFound();
		}

		// The attachment goes; the track does not. Detaching a route from a ride is not an
		// instruction to destroy the owner's copy of it — same rule as §5.8's switches, where
		// revoking a permission never deletes what was already permitted.
		database.Remove(route);

		await database.SaveChangesAsync();

		await hub.Clients.Group(RideHub.Group(id)).RideRoutesChanged(id);

		return NoContent();
	}

	/// <summary>
	/// The owner-or-leader check, and the ride-state one behind it. Returns null when the caller
	/// may write, and the response to send when they may not.
	/// </summary>
	private async Task<IActionResult?> AuthoriseWriteAsync(DlrDbContext database, Guid rideId)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		GroupRideMember? membership = await database
			.Set<GroupRideMember>()
			.Include(member => member.Ride)
			.SingleOrDefaultAsync(member => member.GroupRideId == rideId && member.UserId == userId);

		// 404 for somebody not in the ride at all, the same way every other ride route answers: a
		// ride id is shareable, so a distinguishable refusal is an oracle for who is in which ride.
		if (membership?.Ride is null)
		{
			return NotFound();
		}

		if (membership.Role is not (GroupRideRole.Owner or GroupRideRole.Leader))
		{
			return Problem(
				statusCode: StatusCodes.Status403Forbidden,
				title: "Not yours to set",
				detail: "The organiser decides which routes an adventure has.");
		}

		// Draft, Open and Live all accept a change. A ride that has finished does not: its routes
		// are part of what happened, and §5.4's gap list is the only thing that reads them while
		// Live — adding an option mid-ride is a normal thing for an organiser to do, and it moves
		// nobody, because the oldest attachment stays the line riders are projected against.
		if (membership.Ride.State is not (GroupRideState.Draft or GroupRideState.Open or GroupRideState.Live))
		{
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "This adventure has ended",
				detail: "The routes of a finished adventure are part of the record of it.");
		}

		return null;
	}

	/// <summary>
	/// Every route on a ride, in order, with its line already encoded (§15.5).
	/// <para>
	/// The whole set on every call rather than one row: the caller is either drawing the map or
	/// has just changed the set, and both want all of it. The lines come from the stored
	/// <em>simplified</em> polyline, which is what the map draws everywhere else.
	/// </para>
	/// </summary>
	private static async Task<IReadOnlyList<RideRoute>> DescribeAllAsync(DlrDbContext database, Guid rideId)
	{
		var rows = await database
			.Set<GroupRideRoute>()
			.AsNoTracking()
			.Where(route => route.GroupRideId == rideId)
			.OrderBy(route => route.Position)
			.Select(route => new
			{
				route.TrackId,
				route.AddedUtc,
				route.AddedByUserId,
				AddedByUserName = route.AddedBy!.UserName!,
				route.Track!.Name,
				route.Track.DistanceM,
				route.Track.PointCount,
				route.Track.SimplifiedPolyline,
				route.Track.BoundsMinLat,
				route.Track.BoundsMinLon,
				route.Track.BoundsMaxLat,
				route.Track.BoundsMaxLon,
			})
			.ToListAsync();

		List<RideRoute> routes = new(rows.Count);

		foreach (var row in rows)
		{
			using MemoryStream simplified = new(row.SimplifiedPolyline);

			TrackGeometry geometry = TrackBlobCodec.Read(simplified);

			routes.Add(new RideRoute(
				row.TrackId,
				row.Name,
				row.DistanceM,
				row.PointCount,
				PolylineCodec.EncodePoints(geometry.Points),

				// Null for a track with no points at all, so a client frames the map on the other
				// routes rather than on a box at (0, 0) off the coast of Africa.
				geometry.Points.Count == 0
					? null
					: new TrackBounds(row.BoundsMinLat, row.BoundsMinLon, row.BoundsMaxLat, row.BoundsMaxLon),
				row.AddedUtc,
				row.AddedByUserId,
				row.AddedByUserName));
		}

		return routes;
	}
}
