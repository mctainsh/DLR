using DLR.Core.Contracts.Rides;
using DLR.Core.Rides;
using DLR.Server.Data;
using DLR.Server.Data.Identity;
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

/// <summary>The §5.2 request-spam limits, as settings (§14.5).</summary>
public sealed class RideJoinOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "Rides";

	/// <summary>How many requests one rider may have outstanding at once.</summary>
	public int MaxPendingRequestsPerUser { get; set; } = 5;

	/// <summary>How many requests one rider may make in a day.</summary>
	public int MaxRequestsPerDayPerUser { get; set; } = 20;

	/// <summary>
	/// Failed join-code attempts per minute, per address.
	/// <para>
	/// The gap §14.5 found: §7.8 limited the auth endpoints and join <em>requests</em>, but never
	/// join-<em>code</em> submission. Six Crockford characters is about 1.07 billion
	/// combinations — impractical to guess at human speed, entirely practical for a script, and
	/// publishing the code format makes that obvious to anyone reading the repository.
	/// </para>
	/// </summary>
	public int FailedCodeAttemptsPerMinutePerAddress { get; set; } = 10;

	/// <summary>Failed join-code attempts per hour, per account.</summary>
	public int FailedCodeAttemptsPerHourPerUser { get; set; } = 30;
}

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
public static class RideEndpoints
{
	/// <summary>Route name for creating a ride.</summary>
	public const string CreateRouteName = "CreateRide";

	/// <summary>Route name for the caller's own rides.</summary>
	public const string ListRouteName = "ListMyRides";

	/// <summary>Route name for the detail.</summary>
	public const string DetailRouteName = "GetRide";

	/// <summary>Route name for joining by code.</summary>
	public const string JoinRouteName = "JoinRideByCode";

	/// <summary>Route name for the organiser's pending list.</summary>
	public const string RequestsRouteName = "ListJoinRequests";

	/// <summary>Route name for a decision.</summary>
	public const string DecideRouteName = "DecideJoinRequest";

	/// <summary>Route name for taking one's own request back.</summary>
	public const string WithdrawRouteName = "WithdrawJoinRequest";

	/// <summary>Route name for deleting a ride.</summary>
	public const string DeleteRouteName = "DeleteRide";
}

/// <summary>Creating a ride, joining one, and deciding who is in (§5.2).</summary>
[ApiController]
[Authorize]
public sealed class RideController : ControllerBase
{
	// Creating and joining are the social surface §7.8's ladder shuts, and the two places
	// its restriction actually bites. Recording a solo ride stays open.
	[HttpPost("/api/v1/group-rides", Name = RideEndpoints.CreateRouteName)]
	[Authorize(Policy = AuthorizationPolicies.NotRestricted)]
	[EndpointSummary("Creates a ride and its join code.")]
	public async Task<IActionResult> CreateAsync(
		[FromBody] CreateRideRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] PositionStore positions,
		[FromServices] TimeProvider clock)
	{
		if (User.UserId() is not { } ownerId)
		{
			return Unauthorized();
		}

		if (string.IsNullOrWhiteSpace(request.Name))
		{
			return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Name required", detail: "An adventure needs a name.");
		}

		GroupRide ride = new()
		{
			Id = Guid.NewGuid(),
			OwnerId = ownerId,
			Name = request.Name.Trim(),
			Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
			StartUtc = request.StartUtc,
			State = GroupRideState.Open,
			JoinPolicy = request.JoinPolicy == JoinPolicyDto.Open ? JoinPolicy.Open : JoinPolicy.Approval,
			MemberCap = request.MemberCap is > 0 and <= 500 ? request.MemberCap.Value : GroupRide.DefaultMemberCap,
			CreatedUtc = clock.GetUtcNow(),
		};

		// The organiser is a member from the start, so every "is this person in the ride" check
		// has one answer rather than two.
		ride.Members.Add(new GroupRideMember
		{
			GroupRideId = ride.Id,
			UserId = ownerId,
			Role = GroupRideRole.Owner,
			JoinedUtc = clock.GetUtcNow(),
		});

		database.Add(ride);

		// Retried rather than pre-checked. At 32⁶ a collision is rare enough that a lookup on
		// every ride costs more than the retry ever will.
		for (int attempt = 1; ; attempt++)
		{
			ride.JoinCode = JoinCode.Generate();

			try
			{
				await database.SaveChangesAsync();

				break;
			}
			catch (DbUpdateException) when (attempt < 5)
			{
				database.ChangeTracker.Entries<GroupRide>()
					.First(entry => entry.Entity.Id == ride.Id)
					.State = EntityState.Added;
			}
		}

		return Created(
			$"/api/v1/group-rides/{ride.Id}",
			await DescribeAsync(database, positions, ride.Id, ownerId));
	}

	/// <summary>
	/// The join-code path (§5.2), and the endpoint §14.5 says must not ship without a limit.
	/// </summary>
	[HttpPost("/api/v1/group-rides/join", Name = RideEndpoints.JoinRouteName)]
	[Authorize(Policy = AuthorizationPolicies.NotRestricted)]
	[EndpointSummary("Joins by code, or asks to, depending on the ride's policy.")]
	public async Task<IActionResult> JoinAsync(
		[FromBody] JoinByCodeRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] RequestThrottle throttle,
		[FromServices] RideNotifications notifications,
		[FromServices] RideMembers members,
		[FromServices] IOptions<RideJoinOptions> options,
		[FromServices] TimeProvider clock)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		RideJoinOptions limits = options.Value;

		string? code = JoinCode.Normalise(request.Code);

		GroupRide? ride = code is null
			? null
			: await database
				.Set<GroupRide>()
				.Include(row => row.Members)
				.SingleOrDefaultAsync(row => row.JoinCode == code);

		if (ride is null || ride.State is GroupRideState.Cancelled or GroupRideState.Archived)
		{
			// Counting *failures*, not attempts (§14.5). A rider who joins ten rides in a
			// minute is using the product; a client trying ten codes a minute is enumerating
			// them, and only the second should ever meet a limit.
			bool withinLimits =
				throttle.TryAcquire(
					$"join-code-ip:{HttpContext.Connection.RemoteIpAddress}",
					limits.FailedCodeAttemptsPerMinutePerAddress,
					TimeSpan.FromMinutes(1))
				& throttle.TryAcquire(
					$"join-code-user:{userId}",
					limits.FailedCodeAttemptsPerHourPerUser,
					TimeSpan.FromHours(1));

			return withinLimits
				? Problem(statusCode: StatusCodes.Status404NotFound, title: "No such adventure", detail: "That join code does not match an adventure.")
				: StatusCode(StatusCodes.Status429TooManyRequests);
		}

		if (ride.Members.Any(member => member.UserId == userId))
		{
			return Ok(new JoinResult(ride.Id, Joined: true, RequestId: null));
		}

		bool blocked = await database
			.Set<GroupRideJoinRequest>()
			.AnyAsync(row => row.GroupRideId == ride.Id && row.UserId == userId && row.Blocked);

		if (blocked)
		{
			// The same answer an unknown code gets. Telling somebody they have been blocked
			// hands them the one fact the organiser was trying not to have a conversation about.
			return Problem(statusCode: StatusCodes.Status404NotFound, title: "No such adventure", detail: "That join code does not match an adventure.");
		}

		if (ride.Members.Count >= ride.MemberCap)
		{
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "Adventure is full",
				detail: $"This adventure has reached its limit of {ride.MemberCap} members.");
		}

		if (ride.JoinPolicy == JoinPolicy.Open)
		{
			GroupRideMember joined = new()
			{
				GroupRideId = ride.Id,
				UserId = userId,
				Role = GroupRideRole.Rider,
				JoinedUtc = clock.GetUtcNow(),
			};

			database.Add(joined);

			await database.SaveChangesAsync();

			// After the commit, so the ride is never told about a member the database does not
			// have. The people already on it are watching a list this row belongs in (§5.3).
			await members.AnnounceJoinedAsync(ride.Id, joined);

			return Ok(new JoinResult(ride.Id, Joined: true, RequestId: null));
		}

		return await RequestAsync(ride, userId, request.Message, database, throttle, notifications, limits, clock);
	}

	private async Task<IActionResult> RequestAsync(
		GroupRide ride,
		Guid userId,
		string? message,
		DlrDbContext database,
		RequestThrottle throttle,
		RideNotifications notifications,
		RideJoinOptions limits,
		TimeProvider clock)
	{
		GroupRideJoinRequest? existing = await database
			.Set<GroupRideJoinRequest>()
			.SingleOrDefaultAsync(row =>
				row.GroupRideId == ride.Id
				&& row.UserId == userId
				&& row.Status == JoinRequestStatus.Pending);

		if (existing is not null)
		{
			return Ok(new JoinResult(ride.Id, Joined: false, existing.Id));
		}

		// Without this, "request to join" is an invitation to pester every ride in the system
		// (§5.2). Counted from rows rather than from memory, for the same reason the §7.8
		// ladder is — a restart must not be a way to start again.
		int pending = await database
			.Set<GroupRideJoinRequest>()
			.CountAsync(row => row.UserId == userId && row.Status == JoinRequestStatus.Pending);

		if (pending >= limits.MaxPendingRequestsPerUser)
		{
			return Problem(
				statusCode: StatusCodes.Status429TooManyRequests,
				title: "Too many pending requests",
				detail: $"You already have {pending} requests waiting. Wait for an answer before asking " +
				"to join another adventure.");
		}

		if (!throttle.TryAcquire(
			$"join-request-day:{userId}",
			limits.MaxRequestsPerDayPerUser,
			TimeSpan.FromDays(1)))
		{
			return StatusCode(StatusCodes.Status429TooManyRequests);
		}

		GroupRideJoinRequest created = new()
		{
			Id = Guid.NewGuid(),
			GroupRideId = ride.Id,
			UserId = userId,
			Status = JoinRequestStatus.Pending,
			Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim(),
			RequestedUtc = clock.GetUtcNow(),
		};

		database.Add(created);

		await database.SaveChangesAsync();

		await notifications.RequestReceivedAsync(ride, created);

		return Ok(new JoinResult(ride.Id, Joined: false, created.Id));
	}

	[HttpGet("/api/v1/group-rides", Name = RideEndpoints.ListRouteName)]
	[EndpointSummary("The rides the caller is in, split by organised or joined.")]
	public async Task<IActionResult> ListAsync([FromServices] DlrDbContext database)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		// One query: every ride the caller is a member of, with the caller's role. Split
		// server-side so the client renders two sections without a second round trip.
		var rows = await database
			.Set<GroupRideMember>()
			.AsNoTracking()
			.Where(member => member.UserId == userId)
			.OrderByDescending(member => member.Ride!.StartUtc)
			.Select(member => new
			{
				member.Ride!.Id,
				member.Ride.Name,
				member.Ride.StartUtc,
				member.Ride.State,
				IsOrganiser = member.Role == GroupRideRole.Owner,
				MemberCount = member.Ride.Members.Count,
				member.Ride.JoinCode,
			})
			.ToListAsync();

		List<RideSummary> organised = [];
		List<RideSummary> joined = [];

		foreach (var row in rows)
		{
			RideSummary summary = new(
				row.Id,
				row.Name,
				row.StartUtc,
				(RideStateDto)row.State,
				row.IsOrganiser,
				row.MemberCount,

				// Every member sees the code, not only the organiser. A rider who has joined has
				// already been let in, and withholding the code only stopped them telling a friend
				// how to follow along — which is what they most want to do at the start line (§5.2).
				row.JoinCode);

			(row.IsOrganiser ? organised : joined).Add(summary);
		}

		// A second query rather than a join onto the first: a pending requester has no member row,
		// which is exactly the fact the ride screens rely on (§5.2), so there is nothing to widen
		// the query above to reach. Most callers have none of these and the query is an index hit
		// on (user_id, status).
		//
		// Projected to WaitingRide and not to RideSummary, and that is the whole point of the type:
		// the join code and the member count are a member's, and this caller is not one yet.
		// Projected to an anonymous type and mapped afterwards, exactly as the query above is. The
		// enum cast is why: GroupRideState and RideStateDto are two different types, and Npgsql has
		// no translation for a cast between them — done inline it compiles and then 500s at runtime.
		var asked = await database
			.Set<GroupRideJoinRequest>()
			.AsNoTracking()
			.Where(row => row.UserId == userId && row.Status == JoinRequestStatus.Pending)
			.OrderByDescending(row => row.RequestedUtc)
			.Select(row => new
			{
				row.GroupRideId,
				RequestId = row.Id,
				row.Ride!.Name,
				row.Ride.StartUtc,
				row.Ride.State,
				row.RequestedUtc,
			})
			.ToListAsync();

		List<WaitingRide> waiting = [.. asked.Select(row => new WaitingRide(
			row.GroupRideId,
			row.RequestId,
			row.Name,
			row.StartUtc,
			(RideStateDto)row.State,
			row.RequestedUtc))];

		return Ok(new MyRides(organised, joined, waiting));
	}

	[HttpGet("/api/v1/group-rides/{id:guid}", Name = RideEndpoints.DetailRouteName)]
	[EndpointSummary("A ride and its members.")]
	public async Task<IActionResult> DetailAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] PositionStore positions)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		bool isMember = await database
			.Set<GroupRideMember>()
			.AnyAsync(member => member.GroupRideId == id && member.UserId == userId);

		// Membership is the only access model (§5.2). A ride id is shareable, so a
		// non-member's answer must not depend on whether the ride exists.
		return isMember
			? Ok(await DescribeAsync(database, positions, id, userId))
			: NotFound();
	}

	[HttpGet("/api/v1/group-rides/{id:guid}/join-requests", Name = RideEndpoints.RequestsRouteName)]
	[EndpointSummary("Requests waiting on the organiser.")]
	public async Task<IActionResult> RequestsAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		if (!await CanDecideAsync(database, id, userId))
		{
			return NotFound();
		}

		List<JoinRequestSummary> pending = await database
			.Set<GroupRideJoinRequest>()
			.AsNoTracking()
			.Where(row => row.GroupRideId == id && row.Status == JoinRequestStatus.Pending)
			.OrderBy(row => row.RequestedUtc)
			.Select(row => new JoinRequestSummary(
				row.Id,
				row.UserId,
				row.User!.UserName!,
				row.Message,
				row.RequestedUtc))
			.ToListAsync();

		return Ok(pending);
	}

	[HttpPost("/api/v1/group-rides/{id:guid}/join-requests/{requestId:guid}", Name = RideEndpoints.DecideRouteName)]
	[EndpointSummary("Admits or declines a request.")]
	public async Task<IActionResult> DecideAsync(
		[FromRoute] Guid id,
		[FromRoute] Guid requestId,
		[FromBody] DecideJoinRequest decision,
		[FromServices] DlrDbContext database,
		[FromServices] RideNotifications notifications,
		[FromServices] RideMembers members,
		[FromServices] TimeProvider clock)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		if (!await CanDecideAsync(database, id, userId))
		{
			return NotFound();
		}

		GroupRideJoinRequest? request = await database
			.Set<GroupRideJoinRequest>()
			.SingleOrDefaultAsync(row =>
				row.Id == requestId
				&& row.GroupRideId == id
				&& row.Status == JoinRequestStatus.Pending);

		if (request is null)
		{
			return NotFound();
		}

		GroupRide ride = await database
			.Set<GroupRide>()
			.Include(row => row.Members)
			.SingleAsync(row => row.Id == id);

		DateTimeOffset now = clock.GetUtcNow();

		// Null on a decline, which is the whole of the difference: declining answers a request
		// and changes nobody's membership, so there is nothing for the ride to be told.
		GroupRideMember? admitted = null;

		if (decision.Admit)
		{
			if (ride.Members.Count >= ride.MemberCap)
			{
				return Problem(
					statusCode: StatusCodes.Status409Conflict,
					title: "Adventure is full",
					detail: $"This adventure has reached its limit of {ride.MemberCap} members.");
			}

			admitted = new GroupRideMember
			{
				GroupRideId = id,
				UserId = request.UserId,
				Role = GroupRideRole.Rider,
				JoinedUtc = now,
			};

			database.Add(admitted);
		}

		request.Status = decision.Admit ? JoinRequestStatus.Approved : JoinRequestStatus.Declined;
		request.DecidedUtc = now;
		request.DecidedBy = userId;

		// A block only means anything on a decline. Recorded on the request row rather than as
		// its own table: the decision and the block are the same act.
		request.Blocked = !decision.Admit && decision.Block;

		await database.SaveChangesAsync();

		await notifications.DecisionMadeAsync(ride, request, decision.Admit);

		if (admitted is not null)
		{
			// The same announcement the open-code path makes, for the same reason: from the ride's
			// point of view these are one event — somebody is on it now — and they arrived by two
			// different doors (§5.2).
			await members.AnnounceJoinedAsync(id, admitted);
		}

		return NoContent();
	}

	/// <summary>
	/// The asker takes their own request back (§5.2).
	/// <para>
	/// <c>DELETE</c> on the same path <see cref="DecideAsync"/> answers with <c>POST</c>: one
	/// request, two people who may act on it, and the verb is what says which of them is acting.
	/// </para>
	/// <para>
	/// <strong>The row is deleted, not marked.</strong> There is no <c>Withdrawn</c> status and
	/// there should not be one — nobody decided anything, and recording a decline the organiser
	/// never made would put a lie in the table the organiser reads. Deleting also frees the
	/// pending slot, which is the point: a rider who changed their mind is not spending one of
	/// their allowance on it.
	/// </para>
	/// <para>
	/// <strong>It is not a way out of a block.</strong> Only a <c>Pending</c> row can be withdrawn,
	/// and a block lives on a <c>Declined</c> one — so the row that stops somebody asking again
	/// survives this call untouched. Nor does it refund the daily throttle, which counts attempts
	/// rather than rows (§14.5), so asking and withdrawing in a loop buys nothing.
	/// </para>
	/// </summary>
	[HttpDelete("/api/v1/group-rides/{id:guid}/join-requests/{requestId:guid}", Name = RideEndpoints.WithdrawRouteName)]
	[EndpointSummary("Takes back a join request the caller made and nobody has answered.")]
	public async Task<IActionResult> WithdrawAsync(
		[FromRoute] Guid id,
		[FromRoute] Guid requestId,
		[FromServices] DlrDbContext database,
		[FromServices] RideNotifications notifications)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		// The caller's own id is in the predicate, not checked after the fetch: this is the whole
		// of the authorisation, and somebody else's request must be as invisible here as a request
		// that does not exist. A ride id travels in links (§5.2), so the two answer alike.
		GroupRideJoinRequest? request = await database
			.Set<GroupRideJoinRequest>()
			.SingleOrDefaultAsync(row =>
				row.Id == requestId
				&& row.GroupRideId == id
				&& row.UserId == userId
				&& row.Status == JoinRequestStatus.Pending);

		if (request is null)
		{
			// Already answered, already withdrawn, or never theirs. NoContent rather than NotFound
			// on the first two: the caller asked for this request to be gone and it is gone, and a
			// rider who taps Withdraw a half-second after the organiser taps Admit should not be
			// shown a failure for a race they cannot see. Their next load says which way it went.
			return NoContent();
		}

		GroupRide? ride = await database
			.Set<GroupRide>()
			.SingleOrDefaultAsync(row => row.Id == id);

		database.Remove(request);

		await database.SaveChangesAsync();

		if (ride is not null)
		{
			// The organiser's badge is counting this one. Nothing is e-mailed — an organiser does
			// not need a message every time somebody changes their mind, and the count going down
			// is the whole of what they need to know.
			await notifications.RequestWithdrawnAsync(ride, request);
		}

		return NoContent();
	}

	[HttpDelete("/api/v1/group-rides/{id:guid}", Name = RideEndpoints.DeleteRouteName)]
	[EndpointSummary("Deletes one of the caller's own adventures and everything hanging off it. Irreversible.")]
	public async Task<IActionResult> DeleteAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] PositionStore positions,
		CancellationToken cancellationToken)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		// Owner only, and matched in the same query as the lookup so a member of somebody else's
		// ride gets the same answer as a stranger. §5.2's rule that a ride id is shareable is
		// what makes that necessary: 403 here would confirm the ride exists to anyone holding one.
		GroupRide? ride = await database
			.Set<GroupRide>()
			.AsNoTracking()
			.SingleOrDefaultAsync(row => row.Id == id && row.OwnerId == userId, cancellationToken);

		if (ride is null)
		{
			// Including a second press of the same button. The organiser asked for it to be gone
			// and it is, but answering 204 to any id at all would make this a way to probe.
			return NotFound();
		}

		// The one refusal. Deleting a ride in progress takes the map out from under everybody
		// still on the road — §5.6 already gives the organiser the verb for that moment, and it
		// is End, which stops the sharing without destroying the day's thread and markers.
		if (ride.State is GroupRideState.Live)
		{
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "This adventure is in progress",
				detail: "End the adventure first. Deleting one that is running would blank the map "
					+ "for every rider still out there.");
		}

		// Before the row goes, because the cascade clears the table and not the §5.5 cache in
		// front of it — a fix left in memory would keep answering for a ride that no longer exists.
		await positions.ClearRideAsync(id);

		// Members, join requests, routes, markers, comments and their reactions and polls all
		// reach group_ride through ON DELETE CASCADE, so this one statement is the whole delete.
		// Photographs are the exception, and deliberately not handled here: a photo row belongs
		// to the account that uploaded it (§16.6), not to the ride it was shown in, and it
		// outlives a deleted comment for the same reason. Deleting them from this endpoint would
		// make one ride's delete reach into another ride's thread whenever the same image was
		// posted twice.
		await database
			.Set<GroupRide>()
			.Where(row => row.Id == id && row.OwnerId == userId)
			.ExecuteDeleteAsync(cancellationToken);

		return NoContent();
	}

	private static Task<bool> CanDecideAsync(DlrDbContext database, Guid rideId, Guid userId) =>
		database.Set<GroupRideMember>()
			.AnyAsync(member =>
				member.GroupRideId == rideId
				&& member.UserId == userId
				&& (member.Role == GroupRideRole.Owner || member.Role == GroupRideRole.Leader));

	private static async Task<RideDetail> DescribeAsync(
		DlrDbContext database,
		PositionStore positions,
		Guid rideId,
		Guid callerId)
	{
		GroupRide ride = await database
			.Set<GroupRide>()
			.AsNoTracking()
			.Include(row => row.Members)
			.ThenInclude(member => member.User)
			.SingleAsync(row => row.Id == rideId);

		bool isOrganiser = ride.OwnerId == callerId;

		// Who actually has a fix. Kept apart from the sharing flag because "not sharing" and
		// "no signal" mean different things to somebody waiting at a junction (§5.6). Read from
		// the cache, not the table: a rider who published four seconds ago has not been flushed
		// yet, and showing them as no-signal would be wrong for as long as the flush period.
		IReadOnlySet<Guid> located = await positions.LocatedAsync(rideId);

		// The third reason a member can have no pin (§10.1). Read here rather than inferred from the
		// empty position, because "at home and has said so" is a different fact from "in a tunnel" and
		// a client that had to guess between them would guess wrong at exactly the wrong moment.
		IReadOnlySet<Guid> hidden = positions.PrivateRiders();

		return new RideDetail(
			ride.Id,
			ride.Name,
			ride.Description,
			ride.StartUtc,
			(RideStateDto)ride.State,
			ride.JoinPolicy == JoinPolicy.Open ? JoinPolicyDto.Open : JoinPolicyDto.Approval,
			ride.MemberCap,
			ride.Members.Count,
			isOrganiser,

			// Sent to every member, not only the organiser — same rule as the list (§5.2). The
			// organiser still controls admission on an approval ride; on an open one the code is a
			// convenience the people already in the ride are trusted with.
			ride.JoinCode,

			// Sent to everybody, unlike the join code. A client that does not know a switch is off
			// draws a compose surface that produces a 403 when used, which reads as a broken app
			// rather than as a decision the organiser made (§5.8).
			MembershipEndpoints.Describe(ride),

			[.. ride.Members
				.OrderBy(member => member.JoinedUtc)
				.Select(member => new RideMemberSummary(
					member.UserId,
					member.User!.UserName!,
					member.Role.ToString(),
					member.JoinedUtc,
					member.ShareLocation,
					located.Contains(member.UserId),

					// How this member's marker is painted on the live map (§16.3). Sent with the
					// member rather than with their position: the position batch goes out every
					// tick and this changes about as often as a username does.
					member.User.MarkerColour,
					hidden.Contains(member.UserId)))]);
	}
}

/// <summary>
/// Telling a ride that somebody is now on it (§5.2, §5.3).
/// <para>
/// Its own service rather than three more <c>[FromServices]</c> parameters on each of the two
/// endpoints that admit people: building the member row the way the list draws it needs the
/// account (for the handle and the marker colour) and the position store (for whether they are
/// inside their own private area), and doing that twice is how the two doors into a ride come to
/// describe the same rider differently.
/// </para>
/// </summary>
/// <param name="database">For the account behind the membership row.</param>
/// <param name="positions">For the one member flag that is not on the row.</param>
/// <param name="hub">The live connections.</param>
/// <param name="logger">Where a failed announcement is recorded.</param>
public sealed class RideMembers(
	DlrDbContext database,
	PositionStore positions,
	IHubContext<RideHub, IRideClient> hub,
	ILogger<RideMembers> logger)
{
	/// <summary>Tells everybody already on the ride that the membership grew.</summary>
	/// <param name="rideId">Which ride.</param>
	/// <param name="member">The row that was just committed.</param>
	public async Task AnnounceJoinedAsync(Guid rideId, GroupRideMember member)
	{
		AppUser? user = await database.Users.FindAsync(member.UserId);

		if (user is null)
		{
			// The foreign key says this cannot happen. Nothing useful to announce without a handle
			// to announce, and the membership itself is already recorded.
			return;
		}

		await hub.AnnounceJoinedAsync(
			rideId,
			new RideMemberSummary(
				member.UserId,
				user.UserName!,
				member.Role.ToString(),
				member.JoinedUtc,

				// Off, and read from the row rather than assumed: joining a ride and agreeing to
				// broadcast are separate decisions and the second one defaults to off (§5.6).
				member.ShareLocation,

				// Nobody has a fix in a ride they joined a moment ago. Stated rather than looked
				// up, because the lookup is ride-scoped and could only ever answer false here.
				HasPosition: false,

				// How their marker is painted on the live map (§16.3).
				user.MarkerColour,

				// The one flag that is neither on the membership row nor false by construction: a
				// private area is a property of the rider, not of this ride, so somebody can join
				// from their own kitchen and be hidden from the first moment (§10.1).
				positions.PrivateRiders().Contains(member.UserId)),
			logger);
	}
}

/// <summary>
/// Telling people what happened to a request (§5.2).
/// <para>
/// Two transports, and they answer different questions. The <strong>hub</strong> reaches the
/// people who decide, on the screens they already have open, and is what keeps the waiting count
/// on the live map and the info page true without a reload (§5.3). <strong>E-mail</strong>
/// reaches whoever is not looking — including the asker, who is not on the ride yet and so is in
/// no hub group at all.
/// </para>
/// <para>
/// §5.2 specifies push, which is Phase 2 work. When it arrives this is still where it attaches.
/// Email goes only where an address is known — silently impossible otherwise, which is another
/// line in §7.2's trade-off.
/// </para>
/// <para>
/// <strong>Neither transport may undo what has been recorded.</strong> The row is committed by
/// the time anything here runs, so a hub that has gone away and a mail server that is down are
/// both logged and swallowed (§7.12). It also means the two are independent: an organiser with
/// no e-mail address still gets the live count.
/// </para>
/// </summary>
/// <param name="database">For looking up who to tell.</param>
/// <param name="hub">The live connections.</param>
/// <param name="email">The transport for whoever is not looking.</param>
/// <param name="logger">Where a failed notification is recorded.</param>
public sealed class RideNotifications(
	DlrDbContext database,
	IHubContext<RideHub, IRideClient> hub,
	IEmailSender email,
	ILogger<RideNotifications> logger)
{
	/// <summary>Tells the people who decide that somebody is waiting.</summary>
	/// <param name="ride">The ride.</param>
	/// <param name="request">The row that was just written.</param>
	public async Task RequestReceivedAsync(GroupRide ride, GroupRideJoinRequest request)
	{
		AppUser? requester = await database.Users.FindAsync(request.UserId);

		if (requester is null)
		{
			// The foreign key says this cannot happen. Returning rather than throwing, because the
			// request is already committed and there is nothing useful left to say about it.
			return;
		}

		// Before the e-mail, and not conditional on it. An organiser who never confirmed an address
		// still has the app open, and the badge is the notification for them.
		await AnnounceAsync(
			ride.Id,
			client => client.JoinRequestReceived(
				ride.Id,
				new JoinRequestSummary(
					request.Id,
					request.UserId,
					requester.UserName!,
					request.Message,
					request.RequestedUtc)));

		AppUser? organiser = await database.Users.FindAsync(ride.OwnerId);

		if (organiser?.Email is null)
		{
			return;
		}

		await SendAsync(
			organiser.Email,
			$"{requester.UserName} wants to join {ride.Name}",
			$"""
			{requester.UserName} has asked to join {ride.Name}.

			Open the ride to admit or decline them. Nobody enters a ride you organise without
			you saying so.
			""",
			ride.Id);
	}

	/// <summary>
	/// Tells the rider what the organiser decided, and takes the answered request off the
	/// deciders' count.
	/// </summary>
	/// <param name="ride">The ride.</param>
	/// <param name="request">The request that was answered.</param>
	/// <param name="admitted">Whether they are in.</param>
	public async Task DecisionMadeAsync(GroupRide ride, GroupRideJoinRequest request, bool admitted)
	{
		// The deciders, not the rider — see IRideClient.JoinRequestDecided. This is the half that
		// stops an organiser's second device, or a co-leader's, from going on showing a number that
		// has already been answered. The rider is told by e-mail, below.
		await AnnounceAsync(
			ride.Id,
			client => client.JoinRequestDecided(ride.Id, new JoinResult(ride.Id, admitted, request.Id)));

		AppUser? rider = await database.Users.FindAsync(request.UserId);

		if (rider?.Email is null)
		{
			return;
		}

		await SendAsync(
			rider.Email,
			admitted ? $"You are in: {ride.Name}" : $"Your request to join {ride.Name}",
			admitted
				? $"""
					{ride.Name} is yours to open now.

					Sharing your location is a separate decision and it is off until you turn it
					on — joining a ride does not start broadcasting.
					"""
				: $"""
					The organiser of {ride.Name} did not admit you this time.
					""",
			ride.Id);
	}

	/// <summary>
	/// Takes a withdrawn request off the deciders' count (§5.2).
	/// <para>
	/// Hub only, no e-mail. The other two events here are about a decision somebody is waiting on;
	/// this one is somebody quietly changing their mind, and an organiser who is not looking at the
	/// app does not need to be told about it at all — the list will simply be one shorter when they
	/// next open it.
	/// </para>
	/// </summary>
	/// <param name="ride">The ride.</param>
	/// <param name="request">The request that was taken back.</param>
	public Task RequestWithdrawnAsync(GroupRide ride, GroupRideJoinRequest request) =>
		AnnounceAsync(ride.Id, client => client.JoinRequestWithdrawn(ride.Id, request.Id));

	/// <summary>
	/// Sends one message to everybody who may decide who is on this ride (§5.2).
	/// <para>
	/// Swallowing, on <see cref="SendAsync"/>'s reasoning and §7.12's rule: the request or the
	/// decision is already in the database, and a hub that cannot deliver must not turn a committed
	/// write into a 500 for the person who made it. The cost of a lost message is one stale badge
	/// until the next load re-counts, which is what <c>RideSession.RefreshJoinRequestsAsync</c> is
	/// for — §5.3's rule holds here as everywhere: the snapshot is authoritative and this is the
	/// delta on top.
	/// </para>
	/// </summary>
	/// <param name="rideId">Which ride's deciders.</param>
	/// <param name="send">What to send them.</param>
	private async Task AnnounceAsync(Guid rideId, Func<IRideClient, Task> send)
	{
		try
		{
			await send(hub.Clients.Group(RideHub.DecidersGroup(rideId)));
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "Could not announce a join request for {RideId}.", rideId);
		}
	}

	private async Task SendAsync(string to, string subject, string body, Guid rideId)
	{
		try
		{
			await email.SendAsync(new EmailMessage(to, subject, body.ReplaceLineEndings("\n")));
		}
		catch (Exception exception)
		{
			// A decision that has already been recorded is not undone by a mail server being
			// down (§7.12). The rider sees it next time they open the ride.
			logger.LogError(exception, "Could not send a ride notification for {RideId}.", rideId);
		}
	}
}
