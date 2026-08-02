using DLR.Core.Contracts.Rides;
using DLR.Server.Data;
using DLR.Server.Data.Rides;
using DLR.Server.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Positions;

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
public static class PositionEndpoints
{
	/// <summary>Route name for publishing.</summary>
	public const string PublishRouteName = "PublishPosition";

	/// <summary>Route name for the snapshot.</summary>
	public const string SnapshotRouteName = "GetRidePositions";
}

/// <summary>Publishing a position, and reading the ride's snapshot (§5.3, §5.7).</summary>
[ApiController]
[Authorize]
public sealed class PositionController : ControllerBase
{
	// No ride id in the route, because there is none in the payload (§5.7). The server
	// decides which rides a fix lands in, by each ride's own consent flag.
	[HttpPost("/api/v1/positions", Name = PositionEndpoints.PublishRouteName)]
	[Authorize(Policy = AuthorizationPolicies.NotRestricted)]
	[EndpointSummary("Publishes one fix into every ride the rider is sharing with.")]
	public async Task<IActionResult> PublishAsync(
		[FromBody] PositionUpdate update,
		[FromServices] PositionStore positions)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		if (update.Lat is < -9_000_000 or > 9_000_000 || update.Lon is < -18_000_000 or > 18_000_000)
		{
			return new ObjectResult(new ProblemDetails
			{
				Status = StatusCodes.Status400BadRequest,
				Title = "Position out of range",
				Detail = "Latitude and longitude are degrees scaled by 100000.",
			})
			{
				StatusCode = StatusCodes.Status400BadRequest,
				ContentTypes = { "application/problem+json" },
			};
		}

		IReadOnlyList<Guid> rides = await positions.PublishAsync(userId, update);

		// An empty list is the right answer for a rider sharing with nobody, not an error. The
		// client publishes on a timer and does not need to know, or be told, which of its rides
		// consented — that is the server's job precisely so the client cannot get it wrong.
		return Ok(new PublishResult(rides));
	}

	[HttpGet("/api/v1/group-rides/{id:guid}/positions", Name = PositionEndpoints.SnapshotRouteName)]
	[EndpointSummary("Everyone in the ride who is currently sharing.")]
	public async Task<IActionResult> SnapshotAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] PositionStore positions)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		// A member who is not sharing still sees the map (§5.6). Making sharing the price of
		// seeing where everyone is would be simpler and it would be coercive — a pillion, an
		// organiser in a support van, or somebody following the route all have reason to watch
		// without broadcasting.
		bool isMember = await database
			.Set<GroupRideMember>()
			.AnyAsync(member => member.GroupRideId == id && member.UserId == userId);

		return isMember
			? Ok(await positions.SnapshotAsync(id))
			: NotFound();
	}
}
