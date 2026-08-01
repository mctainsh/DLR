using System.Security.Claims;
using DLR.Core.Contracts.Rides;
using DLR.Server.Data;
using DLR.Server.Data.Rides;
using DLR.Server.Identity;
using DLR.Server.Positions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DLR.Server.Rides;

/// <summary>
/// Consent, leaving, removal and ending — the four ways a rider stops broadcasting (§5.6).
/// <para>
/// They are grouped deliberately. Each one has the same obligation attached to it, and every one
/// of them discharges it by calling <see cref="PositionStore.StopSharingAsync"/> rather than by
/// writing its own delete.
/// </para>
/// </summary>
public static class MembershipEndpoints
{
	/// <summary>Route name for setting one's own sharing.</summary>
	public const string SharingRouteName = "SetSharing";

	/// <summary>Route name for leaving.</summary>
	public const string LeaveRouteName = "LeaveRide";

	/// <summary>Route name for removing a member.</summary>
	public const string RemoveRouteName = "RemoveRideMember";

	/// <summary>Route name for ending a ride.</summary>
	public const string EndRouteName = "EndRide";

	/// <summary>Route name for starting a ride.</summary>
	public const string StartRouteName = "StartRide";

	/// <summary>Maps the membership endpoints.</summary>
	public static IEndpointRouteBuilder MapMembership(this IEndpointRouteBuilder endpoints)
	{
		// "me", not a user id. The route itself refuses to express "set somebody else's sharing",
		// which is the §5.6 asymmetry made structural: the organiser controls the ride, the rider
		// controls their location. An endpoint taking a user id would need a guard, and a guard
		// can be removed.
		endpoints
			.MapPut("/api/v1/group-rides/{id:guid}/sharing/me", SetSharingAsync)
			.RequireAuthorization()
			.WithName(SharingRouteName)
			.WithSummary("The caller's own answer to the sharing prompt, for this ride.");

		endpoints
			.MapDelete("/api/v1/group-rides/{id:guid}/members/me", LeaveAsync)
			.RequireAuthorization()
			.WithName(LeaveRouteName)
			.WithSummary("Leaves the ride and deletes the caller's position.");

		endpoints
			.MapDelete("/api/v1/group-rides/{id:guid}/members/{userId:guid}", RemoveAsync)
			.RequireAuthorization()
			.WithName(RemoveRouteName)
			.WithSummary("Removes a member, deleting their position.");

		endpoints
			.MapPost("/api/v1/group-rides/{id:guid}/ending", EndAsync)
			.RequireAuthorization()
			.WithName(EndRouteName)
			.WithSummary("Ends the ride.");

		endpoints
			.MapPost("/api/v1/group-rides/{id:guid}/start", StartAsync)
			.RequireAuthorization()
			.WithName(StartRouteName)
			.WithSummary("Takes the ride Live, so positions begin flowing.");

		return endpoints;
	}

	/// <summary>
	/// Draft or Open → Live (§5.1), and the one place the §5.7 concurrency cap is enforced.
	/// </summary>
	private static async Task<IResult> StartAsync(
		Guid id,
		ClaimsPrincipal caller,
		DlrDbContext database,
		IOptions<RideOptions> options)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		GroupRide? ride = await database
			.Set<GroupRide>()
			.SingleOrDefaultAsync(row => row.Id == id && row.OwnerId == userId);

		if (ride is null)
		{
			return Results.NotFound();
		}

		if (ride.State == GroupRideState.Live)
		{
			return Results.NoContent();
		}

		if (ride.State is not (GroupRideState.Draft or GroupRideState.Open))
		{
			return Results.Problem(new ProblemDetails
			{
				Status = StatusCodes.Status409Conflict,
				Title = "Already ended",
				Detail = "A ride that has finished cannot be started again.",
			});
		}

		// Counted for the organiser, who is the one performing the transition. Being a *member*
		// of many rides is fine — being live in several at once is what costs, because each live
		// ride is its own inbound batch every five seconds (§5.7).
		int live = await database
			.Set<GroupRideMember>()
			.CountAsync(member =>
				member.UserId == userId
				&& member.GroupRideId != id
				&& member.Ride!.State == GroupRideState.Live);

		if (live >= options.Value.MaxConcurrentLiveRidesPerUser)
		{
			return Results.Problem(new ProblemDetails
			{
				Status = StatusCodes.Status409Conflict,
				Title = "Too many live rides",
				Detail = $"You are already live in {live} rides. End one before starting another.",
			});
		}

		ride.State = GroupRideState.Live;

		await database.SaveChangesAsync();

		return Results.NoContent();
	}

	private static async Task<IResult> SetSharingAsync(
		Guid id,
		SetSharingRequest request,
		ClaimsPrincipal caller,
		DlrDbContext database,
		PositionStore positions)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		GroupRideMember? member = await database
			.Set<GroupRideMember>()
			.SingleOrDefaultAsync(row => row.GroupRideId == id && row.UserId == userId);

		if (member is null)
		{
			return Results.NotFound();
		}

		member.ShareLocation = request.Share;

		if (!request.Share)
		{
			// The delete is the feature. Stopping the broadcast alone would leave a last-known
			// position at rest, which is exactly what the rider just asked you not to keep.
			await positions.StopSharingAsync(id, userId);
		}

		await database.SaveChangesAsync();

		bool hasPosition = request.Share && await HasPositionAsync(database, id, userId);

		return Results.Ok(new SharingState(request.Share, hasPosition));
	}

	private static async Task<IResult> LeaveAsync(
		Guid id,
		ClaimsPrincipal caller,
		DlrDbContext database,
		PositionStore positions)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		GroupRideMember? member = await database
			.Set<GroupRideMember>()
			.SingleOrDefaultAsync(row => row.GroupRideId == id && row.UserId == userId);

		if (member is null)
		{
			return Results.NotFound();
		}

		if (member.Role == GroupRideRole.Owner)
		{
			return Results.Problem(new ProblemDetails
			{
				Status = StatusCodes.Status409Conflict,
				Title = "The organiser cannot leave",
				Detail = "End or cancel the ride instead — a ride nobody organises has nobody to " +
					"decide who is in it.",
			});
		}

		await positions.StopSharingAsync(id, userId);

		database.Remove(member);

		await database.SaveChangesAsync();

		return Results.NoContent();
	}

	private static async Task<IResult> RemoveAsync(
		Guid id,
		Guid userId,
		ClaimsPrincipal caller,
		DlrDbContext database,
		PositionStore positions)
	{
		if (caller.UserId() is not { } callerId)
		{
			return Results.Unauthorized();
		}

		bool canDecide = await database
			.Set<GroupRideMember>()
			.AnyAsync(row =>
				row.GroupRideId == id
				&& row.UserId == callerId
				&& (row.Role == GroupRideRole.Owner || row.Role == GroupRideRole.Leader));

		if (!canDecide)
		{
			return Results.NotFound();
		}

		GroupRideMember? member = await database
			.Set<GroupRideMember>()
			.SingleOrDefaultAsync(row => row.GroupRideId == id && row.UserId == userId);

		if (member is null)
		{
			return Results.NotFound();
		}

		if (member.Role == GroupRideRole.Owner)
		{
			return Results.Problem(new ProblemDetails
			{
				Status = StatusCodes.Status409Conflict,
				Title = "The organiser cannot be removed",
				Detail = "End or cancel the ride instead.",
			});
		}

		await positions.StopSharingAsync(id, userId);

		database.Remove(member);

		await database.SaveChangesAsync();

		// Their posts stay (§17.6). Deleting half a conversation makes the other half nonsense,
		// and an organiser who actually wants that can delete the posts explicitly.
		return Results.NoContent();
	}

	private static async Task<IResult> EndAsync(
		Guid id,
		EndRideRequest request,
		ClaimsPrincipal caller,
		DlrDbContext database,
		PositionStore positions,
		IOptions<RideOptions> options,
		TimeProvider clock)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		GroupRide? ride = await database
			.Set<GroupRide>()
			.SingleOrDefaultAsync(row => row.Id == id && row.OwnerId == userId);

		if (ride is null)
		{
			return Results.NotFound();
		}

		// A ride inside a wind-down has already ended, but there is still one thing left to do to
		// it: stop it early for everyone (§5.6). So "already ended" only refuses when there is
		// nothing left to stop.
		bool windingDown = ride.SharingEndsUtc is not null;

		if (ride.State is GroupRideState.Cancelled
			|| (ride.State is GroupRideState.Completed && !windingDown))
		{
			return Results.Problem(new ProblemDetails
			{
				Status = StatusCodes.Status409Conflict,
				Title = "Already ended",
				Detail = "This ride has already ended.",
			});
		}

		if (windingDown && request.Ending == RideEndingDto.WindDown)
		{
			// No renewal, no "add another hour". A window that can be extended is an indefinite
			// window with extra steps (§5.6), and refusing here is the only thing standing
			// between the cap and a client that simply calls this on a timer.
			return Results.Problem(new ProblemDetails
			{
				Status = StatusCodes.Status409Conflict,
				Title = "The wind-down cannot be extended",
				Detail = "Sharing already stops at a fixed time. It can be ended early, but not " +
					"lengthened.",
			});
		}

		DateTimeOffset now = clock.GetUtcNow();

		ride.State = GroupRideState.Completed;

		// Not moved by an early stop: the ride ended when it ended, and §17.6's thirty-day
		// archival counts from that moment rather than from the last thing the organiser tapped.
		ride.EndedUtc ??= now;

		if (request.Ending == RideEndingDto.WindDown)
		{
			// The organiser was asked one question and chose the second answer: let riders stop
			// themselves. Ending the ride at the pub otherwise blanks the map while three people
			// are still an hour from home in the dark (§5.6).
			//
			// Set from the server's clock and the configured cap — never from anything the
			// caller sends, which is what makes "it cannot be extended" a property of the shape
			// rather than a validation rule.
			ride.SharingEndsUtc = now.AddMinutes(Math.Max(1, options.Value.MaxWindDownMinutes));

			await database.SaveChangesAsync();

			return Results.NoContent();
		}

		// Either the default ending, or an organiser stopping a wind-down early for everyone.
		// Both mean the same thing here, which is why they are the same code path.
		ride.SharingEndsUtc = null;

		// Every position row, unconditionally. The ride is over and nobody consented to being
		// findable afterwards (§5.6).
		await positions.ClearRideAsync(id);

		await database
			.Set<GroupRideMember>()
			.Where(member => member.GroupRideId == id)
			.ExecuteUpdateAsync(member => member.SetProperty(row => row.ShareLocation, false));

		await database.SaveChangesAsync();

		return Results.NoContent();
	}

	private static Task<bool> HasPositionAsync(DlrDbContext database, Guid rideId, Guid userId) =>
		database
			.Set<Data.Positions.RiderPosition>()
			.AnyAsync(position => position.GroupRideId == rideId && position.UserId == userId);
}
