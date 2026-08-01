using System.Security.Claims;
using DLR.Core.Contracts.Rides;
using DLR.Server.Data;
using DLR.Server.Data.Rides;
using DLR.Server.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Positions;

/// <summary>Publishing a position, and reading the ride's snapshot (§5.3, §5.7).</summary>
public static class PositionEndpoints
{
	/// <summary>Route name for publishing.</summary>
	public const string PublishRouteName = "PublishPosition";

	/// <summary>Route name for the snapshot.</summary>
	public const string SnapshotRouteName = "GetRidePositions";

	/// <summary>Maps the position endpoints.</summary>
	public static IEndpointRouteBuilder MapPositions(this IEndpointRouteBuilder endpoints)
	{
		// No ride id in the route, because there is none in the payload (§5.7). The server
		// decides which rides a fix lands in, by each ride's own consent flag.
		endpoints
			.MapPost("/api/v1/positions", PublishAsync)
			.RequireAuthorization(AuthorizationPolicies.NotRestricted)
			.WithName(PublishRouteName)
			.WithSummary("Publishes one fix into every ride the rider is sharing with.");

		endpoints
			.MapGet("/api/v1/group-rides/{id:guid}/positions", SnapshotAsync)
			.RequireAuthorization()
			.WithName(SnapshotRouteName)
			.WithSummary("Everyone in the ride who is currently sharing.");

		return endpoints;
	}

	private static async Task<IResult> PublishAsync(
		PositionUpdate update,
		ClaimsPrincipal caller,
		PositionStore positions)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		if (update.Lat is < -9_000_000 or > 9_000_000 || update.Lon is < -18_000_000 or > 18_000_000)
		{
			return Results.Problem(new ProblemDetails
			{
				Status = StatusCodes.Status400BadRequest,
				Title = "Position out of range",
				Detail = "Latitude and longitude are degrees scaled by 100000.",
			});
		}

		IReadOnlyList<Guid> rides = await positions.PublishAsync(userId, update);

		// An empty list is the right answer for a rider sharing with nobody, not an error. The
		// client publishes on a timer and does not need to know, or be told, which of its rides
		// consented — that is the server's job precisely so the client cannot get it wrong.
		return Results.Ok(new PublishResult(rides));
	}

	private static async Task<IResult> SnapshotAsync(
		Guid id,
		ClaimsPrincipal caller,
		DlrDbContext database,
		PositionStore positions)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		// A member who is not sharing still sees the map (§5.6). Making sharing the price of
		// seeing where everyone is would be simpler and it would be coercive — a pillion, an
		// organiser in a support van, or somebody following the route all have reason to watch
		// without broadcasting.
		bool isMember = await database
			.Set<GroupRideMember>()
			.AnyAsync(member => member.GroupRideId == id && member.UserId == userId);

		return isMember
			? Results.Ok(await positions.SnapshotAsync(id))
			: Results.NotFound();
	}
}
