using DLR.Core.Contracts.Rides;
using DLR.Server.Data;
using DLR.Server.Data.Rides;
using DLR.Server.Hubs;
using DLR.Server.Identity;
using DLR.Server.Positions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DLR.Server.Rides;

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
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

	/// <summary>Route name for the content switches.</summary>
	public const string PermissionsRouteName = "SetRidePermissions";

	/// <summary>A ride's switches as the wire sees them.</summary>
	internal static RidePermissions Describe(GroupRide ride) => new(
		ride.AllowMemberMarkers,
		ride.AllowMemberComments,
		ride.AllowMemberPhotos);
}

/// <summary>
/// Consent, leaving, removal and ending — the four ways a rider stops broadcasting (§5.6).
/// <para>
/// They are grouped deliberately. Each one has the same obligation attached to it, and every one
/// of them discharges it by calling <see cref="PositionStore.StopSharingAsync"/> rather than by
/// writing its own delete.
/// </para>
/// </summary>
[ApiController]
[Authorize]
public sealed class MembershipController : ControllerBase
{
	/// <summary>
	/// Draft or Open → Live (§5.1), and the one place the §5.7 concurrency cap is enforced.
	/// </summary>
	[HttpPost("/api/v1/group-rides/{id:guid}/start", Name = MembershipEndpoints.StartRouteName)]
	[EndpointSummary("Takes the ride Live, so positions begin flowing.")]
	public async Task<IActionResult> StartAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] IOptions<RideOptions> options)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		GroupRide? ride = await database
			.Set<GroupRide>()
			.SingleOrDefaultAsync(row => row.Id == id && row.OwnerId == userId);

		if (ride is null)
		{
			return NotFound();
		}

		if (ride.State == GroupRideState.Live)
		{
			return NoContent();
		}

		if (ride.State is not (GroupRideState.Draft or GroupRideState.Open))
		{
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "Already ended",
				detail: "An adventure that has finished cannot be started again.");
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
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "Too many live adventures",
				detail: $"You are already live in {live} adventures. End one before starting another.");
		}

		ride.State = GroupRideState.Live;

		await database.SaveChangesAsync();

		return NoContent();
	}

	/// <summary>
	/// The organiser's three content switches (§5.8).
	/// <para>
	/// A whole-object PUT rather than three toggles, so the organiser's screen sends what it shows
	/// and two switches changed in one gesture cannot half-apply.
	/// </para>
	/// </summary>
	// Changeable at any time during the ride's life (§5.8), which is why this is its own
	// endpoint rather than a field on ride creation.
	[HttpPut("/api/v1/group-rides/{id:guid}/permissions", Name = MembershipEndpoints.PermissionsRouteName)]
	[EndpointSummary("Sets what ordinary members may add.")]
	public async Task<IActionResult> SetPermissionsAsync(
		[FromRoute] Guid id,
		[FromBody] RidePermissions request,
		[FromServices] DlrDbContext database,
		[FromServices] IHubContext<RideHub, IRideClient> hub)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		GroupRideMember? membership = await database
			.Set<GroupRideMember>()
			.Include(member => member.Ride)
			.SingleOrDefaultAsync(member => member.GroupRideId == id && member.UserId == userId);

		// 404 rather than 403 for somebody who is not in the ride at all, the same way every other
		// ride route answers: a ride id is shareable, so a distinguishable refusal is an oracle.
		if (membership?.Ride is null)
		{
			return NotFound();
		}

		if (membership.Role is not (GroupRideRole.Owner or GroupRideRole.Leader))
		{
			return Problem(
				statusCode: StatusCodes.Status403Forbidden,
				title: "Not yours to set",
				detail: "The organiser decides what members may add.");
		}

		GroupRide ride = membership.Ride;

		// Nothing is deleted here, and that is the rule rather than an omission (§5.8). Turning a
		// switch off stops new content; the markers and comments already posted stay exactly where
		// they are. Same reasoning as §7.3's profile sharing — revoking a permission is not an
		// instruction to destroy what was already permitted.
		ride.AllowMemberMarkers = request.AllowMemberMarkers;
		ride.AllowMemberComments = request.AllowMemberComments;
		ride.AllowMemberPhotos = request.AllowMemberPhotos;

		await database.SaveChangesAsync();

		RidePermissions permissions = MembershipEndpoints.Describe(ride);

		await hub.Clients.Group(RideHub.Group(id)).RidePermissionsChanged(permissions);

		return Ok(permissions);
	}

	// "me", not a user id. The route itself refuses to express "set somebody else's sharing",
	// which is the §5.6 asymmetry made structural: the organiser controls the ride, the rider
	// controls their location. An endpoint taking a user id would need a guard, and a guard
	// can be removed.
	[HttpPut("/api/v1/group-rides/{id:guid}/sharing/me", Name = MembershipEndpoints.SharingRouteName)]
	[EndpointSummary("The caller's own answer to the sharing prompt, for this ride.")]
	public async Task<IActionResult> SetSharingAsync(
		[FromRoute] Guid id,
		[FromBody] SetSharingRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] PositionStore positions)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		GroupRideMember? member = await database
			.Set<GroupRideMember>()
			.SingleOrDefaultAsync(row => row.GroupRideId == id && row.UserId == userId);

		if (member is null)
		{
			return NotFound();
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

		return Ok(new SharingState(request.Share, hasPosition));
	}

	[HttpDelete("/api/v1/group-rides/{id:guid}/members/me", Name = MembershipEndpoints.LeaveRouteName)]
	[EndpointSummary("Leaves the ride and deletes the caller's position.")]
	public async Task<IActionResult> LeaveAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] PositionStore positions)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		GroupRideMember? member = await database
			.Set<GroupRideMember>()
			.SingleOrDefaultAsync(row => row.GroupRideId == id && row.UserId == userId);

		if (member is null)
		{
			return NotFound();
		}

		if (member.Role == GroupRideRole.Owner)
		{
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "The organiser cannot leave",
				detail: "End or cancel the adventure instead — an adventure nobody organises has nobody to " +
					"decide who is in it.");
		}

		await positions.StopSharingAsync(id, userId);

		database.Remove(member);

		await database.SaveChangesAsync();

		return NoContent();
	}

	[HttpDelete("/api/v1/group-rides/{id:guid}/members/{userId:guid}", Name = MembershipEndpoints.RemoveRouteName)]
	[EndpointSummary("Removes a member, deleting their position.")]
	public async Task<IActionResult> RemoveAsync(
		[FromRoute] Guid id,
		[FromRoute] Guid userId,
		[FromServices] DlrDbContext database,
		[FromServices] PositionStore positions)
	{
		if (User.UserId() is not { } callerId)
		{
			return Unauthorized();
		}

		bool canDecide = await database
			.Set<GroupRideMember>()
			.AnyAsync(row =>
				row.GroupRideId == id
				&& row.UserId == callerId
				&& (row.Role == GroupRideRole.Owner || row.Role == GroupRideRole.Leader));

		if (!canDecide)
		{
			return NotFound();
		}

		GroupRideMember? member = await database
			.Set<GroupRideMember>()
			.SingleOrDefaultAsync(row => row.GroupRideId == id && row.UserId == userId);

		if (member is null)
		{
			return NotFound();
		}

		if (member.Role == GroupRideRole.Owner)
		{
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "The organiser cannot be removed",
				detail: "End or cancel the adventure instead.");
		}

		await positions.StopSharingAsync(id, userId);

		database.Remove(member);

		await database.SaveChangesAsync();

		// Their posts stay (§17.6). Deleting half a conversation makes the other half nonsense,
		// and an organiser who actually wants that can delete the posts explicitly.
		return NoContent();
	}

	[HttpPost("/api/v1/group-rides/{id:guid}/ending", Name = MembershipEndpoints.EndRouteName)]
	[EndpointSummary("Ends the ride.")]
	public async Task<IActionResult> EndAsync(
		[FromRoute] Guid id,
		[FromBody] EndRideRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] PositionStore positions,
		[FromServices] IOptions<RideOptions> options,
		[FromServices] TimeProvider clock)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		GroupRide? ride = await database
			.Set<GroupRide>()
			.SingleOrDefaultAsync(row => row.Id == id && row.OwnerId == userId);

		if (ride is null)
		{
			return NotFound();
		}

		// A ride inside a wind-down has already ended, but there is still one thing left to do to
		// it: stop it early for everyone (§5.6). So "already ended" only refuses when there is
		// nothing left to stop.
		bool windingDown = ride.SharingEndsUtc is not null;

		if (ride.State is GroupRideState.Cancelled
			|| (ride.State is GroupRideState.Completed && !windingDown))
		{
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "Already ended",
				detail: "This adventure has already ended.");
		}

		if (windingDown && request.Ending == RideEndingDto.WindDown)
		{
			// No renewal, no "add another hour". A window that can be extended is an indefinite
			// window with extra steps (§5.6), and refusing here is the only thing standing
			// between the cap and a client that simply calls this on a timer.
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "The wind-down cannot be extended",
				detail: "Sharing already stops at a fixed time. It can be ended early, but not lengthened.");
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

			return NoContent();
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

		return NoContent();
	}

	private static Task<bool> HasPositionAsync(DlrDbContext database, Guid rideId, Guid userId) =>
		database
			.Set<Data.Positions.RiderPosition>()
			.AnyAsync(position => position.GroupRideId == rideId && position.UserId == userId);
}
