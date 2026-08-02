using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Photos;
using DLR.Core.Markers;
using DLR.Server.Data;
using DLR.Server.Data.Markers;
using DLR.Server.Data.Photos;
using DLR.Server.Data.Rides;
using DLR.Server.Data.Tracks;
using DLR.Server.Hubs;
using DLR.Server.Identity;
using DLR.Server.Moderation;
using DLR.Server.Rides;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DLR.Server.Markers;

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
public static class MarkerEndpoints
{
	/// <summary>Route name for creation.</summary>
	public const string CreateRouteName = "CreateMarker";

	/// <summary>Route name for the list.</summary>
	public const string ListRouteName = "ListMarkers";

	/// <summary>Route name for an edit.</summary>
	public const string UpdateRouteName = "UpdateMarker";

	/// <summary>Route name for a deletion.</summary>
	public const string DeleteRouteName = "DeleteMarker";

	/// <summary>Route name for attaching a photo.</summary>
	public const string AttachPhotoRouteName = "AttachMarkerPhoto";
}

/// <summary>Placing, editing and removing markers (§16.1, §16.5, §16.6).</summary>
[ApiController]
[Authorize]
public sealed class MarkerController : ControllerBase
{
	[HttpPost("/api/v1/markers", Name = MarkerEndpoints.CreateRouteName)]
	[Authorize(Policy = AuthorizationPolicies.NotRestricted)]
	[EndpointSummary("Places a marker on a track or a ride.")]
	public async Task<IActionResult> CreateAsync(
		[FromBody] CreateMarkerRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] IHubContext<RideHub, IRideClient> hub,
		[FromServices] RequestThrottle throttle,
		[FromServices] IOptions<MarkerOptions> options,
		[FromServices] TimeProvider clock)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		MarkerOptions limits = options.Value;

		// Checked here as well as in the database, so a caller gets a 400 that says which rule
		// they broke rather than a 500 from a constraint violation (§16.1).
		if (request.TrackId is null == request.GroupRideId is null)
		{
			return ProblemResult(
				StatusCodes.Status400BadRequest,
				"Exactly one parent",
				"A marker hangs off a track or a group ride — one of them, and not both.");
		}

		if (Validate(request.Lat, request.Lon, request.Icon, request.Title, request.DirectionDeg, limits)
			is { } invalid)
		{
			return invalid;
		}

		if (!throttle.TryAcquire(
			$"marker-create:{userId}",
			limits.CreatesPerHourPerUser,
			TimeSpan.FromHours(1)))
		{
			return StatusCode(StatusCodes.Status429TooManyRequests);
		}

		IActionResult? refused = request.GroupRideId is { } rideId
			? await CheckRideAsync(database, rideId, userId, limits)
			: await CheckTrackAsync(database, request.TrackId!.Value, userId, limits);

		if (refused is not null)
		{
			return refused;
		}

		DateTimeOffset now = clock.GetUtcNow();

		Marker marker = new()
		{
			Id = Guid.NewGuid(),
			TrackId = request.TrackId,
			GroupRideId = request.GroupRideId,
			CreatedByUserId = userId,
			Lat = request.Lat,
			Lon = request.Lon,
			DirectionDeg = request.DirectionDeg,
			Icon = request.Icon.Trim(),
			Title = MarkerText.Clean(request.Title)!,
			Note = MarkerText.Clean(request.Note),
			CreatedUtc = now,
			UpdatedUtc = now,
		};

		database.Add(marker);

		await database.SaveChangesAsync();

		MarkerDto dto = await DescribeAsync(database, marker.Id);

		// A discrete authored event, not a telemetry tick — so it travels as its own message
		// rather than folded into the 5 s position batch, where dropping one would be data loss
		// rather than a skipped frame (§16.6).
		if (marker.GroupRideId is { } broadcastTo)
		{
			await hub.Clients.Group(RideHub.Group(broadcastTo)).MarkerAdded(dto);
		}

		return Created($"/api/v1/markers/{marker.Id}", dto);
	}

	[HttpGet("/api/v1/group-rides/{id:guid}/markers", Name = MarkerEndpoints.ListRouteName)]
	[EndpointSummary("Every marker on a ride.")]
	public async Task<IActionResult> RideMarkersAsync(
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

		if (!isMember)
		{
			return NotFound();
		}

		// Blocking hides markers as well as posts (§16.5). Both halves matter: hiding one and not
		// the other leaves the person you blocked writing on the map you are reading.
		IReadOnlySet<Guid> hidden = await BlockList.HiddenFromAsync(database, userId);

		// Ordered before projecting, not after: ordering the DTO makes the translator carry the
		// author join through a sort over a constructed type, which it declines to do.
		List<MarkerDto> markers = await Project(database
				.Set<Marker>()
				.Where(marker => marker.GroupRideId == id && !hidden.Contains(marker.CreatedByUserId))
				.OrderBy(marker => marker.CreatedUtc))
			.ToListAsync();

		return Ok(markers);
	}

	[HttpPut("/api/v1/markers/{id:guid}", Name = MarkerEndpoints.UpdateRouteName)]
	[EndpointSummary("Edits a marker.")]
	public async Task<IActionResult> UpdateAsync(
		[FromRoute] Guid id,
		[FromBody] UpdateMarkerRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] IHubContext<RideHub, IRideClient> hub,
		[FromServices] IOptions<MarkerOptions> options,
		[FromServices] TimeProvider clock)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		MarkerOptions limits = options.Value;

		Marker? marker = await database.Set<Marker>().SingleOrDefaultAsync(row => row.Id == id);

		if (marker is null || !await CanReadAsync(database, marker, userId))
		{
			return NotFound();
		}

		if (!await CanWriteAsync(database, marker, userId))
		{
			return ProblemResult(
				StatusCodes.Status403Forbidden,
				"Not yours to edit",
				"A marker is edited by the person who placed it, or by the organiser.");
		}

		if (Validate(request.Lat, request.Lon, request.Icon, request.Title, request.DirectionDeg, limits)
			is { } invalid)
		{
			return invalid;
		}

		marker.Lat = request.Lat;
		marker.Lon = request.Lon;
		marker.DirectionDeg = request.DirectionDeg;
		marker.Icon = request.Icon.Trim();
		marker.Title = MarkerText.Clean(request.Title)!;
		marker.Note = MarkerText.Clean(request.Note);
		marker.UpdatedUtc = clock.GetUtcNow();

		await database.SaveChangesAsync();

		MarkerDto dto = await DescribeAsync(database, marker.Id);

		if (marker.GroupRideId is { } broadcastTo)
		{
			await hub.Clients.Group(RideHub.Group(broadcastTo)).MarkerUpdated(dto);
		}

		return Ok(dto);
	}

	[HttpDelete("/api/v1/markers/{id:guid}", Name = MarkerEndpoints.DeleteRouteName)]
	[EndpointSummary("Removes a marker.")]
	public async Task<IActionResult> DeleteAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] IHubContext<RideHub, IRideClient> hub)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		Marker? marker = await database.Set<Marker>().SingleOrDefaultAsync(row => row.Id == id);

		if (marker is null || !await CanReadAsync(database, marker, userId))
		{
			return NotFound();
		}

		if (!await CanWriteAsync(database, marker, userId))
		{
			return ProblemResult(
				StatusCodes.Status403Forbidden,
				"Not yours to delete",
				"A marker is removed by the person who placed it, or by the organiser.");
		}

		Guid? rideId = marker.GroupRideId;

		database.Remove(marker);

		await database.SaveChangesAsync();

		if (rideId is { } broadcastTo)
		{
			await hub.Clients.Group(RideHub.Group(broadcastTo)).MarkerRemoved(id);
		}

		return NoContent();
	}

	// Separate from creation, because the photograph is taken where there is no signal and
	// the marker has to appear on everyone's map before it (§16.4).
	[HttpPatch("/api/v1/markers/{id:guid}/photo", Name = MarkerEndpoints.AttachPhotoRouteName)]
	[EndpointSummary("Attaches an uploaded photo to a marker, or detaches the one that is there.")]
	public async Task<IActionResult> AttachPhotoAsync(
		[FromRoute] Guid id,
		[FromBody] AttachPhotoRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] IHubContext<RideHub, IRideClient> hub,
		[FromServices] TimeProvider clock)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		Marker? marker = await database.Set<Marker>().SingleOrDefaultAsync(row => row.Id == id);

		if (marker is null || !await CanReadAsync(database, marker, userId))
		{
			return NotFound();
		}

		if (!await CanWriteAsync(database, marker, userId))
		{
			return ProblemResult(
				StatusCodes.Status403Forbidden,
				"Not yours to edit",
				"A marker is edited by the person who placed it, or by the organiser.");
		}

		if (request.PhotoId is { } photoId)
		{
			// Attaching is where AllowMemberPhotos bites, not the upload (§5.8). A photo is a
			// standalone resource with no ride context — it is taken at the top of a hill and
			// uploaded whenever there is signal — so the ride's switch can only be consulted at
			// the moment the image is bound to something in that ride.
			if (marker.GroupRideId is { } photoRideId
				&& await MembershipAsync(database, photoRideId, userId) is { Ride: not null } membership
				&& !RideContentPermissions.Allows(membership.Ride, membership.Role, RideContent.Photo))
			{
				return RideContentPermissions.Refuse(RideContent.Photo);
			}

			// Their own upload, not merely one that exists. Otherwise a guessed identifier would
			// republish somebody else's photograph under a marker of the caller's choosing.
			bool ownsIt = await database
				.Set<Photo>()
				.AnyAsync(photo => photo.Id == photoId && photo.OwnerId == userId);

			if (!ownsIt)
			{
				return ProblemResult(
					StatusCodes.Status404NotFound,
					"No such photo",
					"Upload the image first, and attach one you uploaded.");
			}
		}

		marker.PhotoId = request.PhotoId;
		marker.UpdatedUtc = clock.GetUtcNow();

		await database.SaveChangesAsync();

		MarkerDto dto = await DescribeAsync(database, marker.Id);

		if (marker.GroupRideId is { } broadcastTo)
		{
			await hub.Clients.Group(RideHub.Group(broadcastTo)).MarkerUpdated(dto);
		}

		return Ok(dto);
	}

	/// <summary>The ride path: membership, lifecycle, and the two caps (§16.5).</summary>
	private static async Task<IActionResult?> CheckRideAsync(
		DlrDbContext database,
		Guid rideId,
		Guid userId,
		MarkerOptions limits)
	{
		GroupRideMember? membership = await database
			.Set<GroupRideMember>()
			.Include(member => member.Ride)
			.SingleOrDefaultAsync(member => member.GroupRideId == rideId && member.UserId == userId);

		if (membership?.Ride is null)
		{
			// Any admitted member may place one — the useful marker is "gravel across the whole
			// corner at the 40 km mark", and the person who found it is whoever hit it first.
			return ProblemResult(
				StatusCodes.Status403Forbidden,
				"Not a member",
				"Markers on a ride are placed by the people in it.");
		}

		// Before and after the ride as well as during it. The only state that forbids it is
		// Archived, which is read-only for the same reason the thread is (§5.1).
		if (membership.Ride.State is GroupRideState.Archived)
		{
			return ProblemResult(
				StatusCodes.Status409Conflict,
				"Ride is archived",
				"An archived ride is read-only.");
		}

		// The organiser may have switched member markers off (§5.8). Checked here rather than at
		// the top, because "you are not in this ride" and "the organiser turned this off" are
		// different answers and a non-member must get the first one.
		if (!RideContentPermissions.Allows(membership.Ride, membership.Role, RideContent.Marker))
		{
			return RideContentPermissions.Refuse(RideContent.Marker);
		}

		int onRide = await database.Set<Marker>().CountAsync(marker => marker.GroupRideId == rideId);

		if (onRide >= limits.MaxPerGroupRide)
		{
			return ProblemResult(
				StatusCodes.Status409Conflict,
				"Ride is full of markers",
				$"This ride already has {onRide} markers.");
		}

		int byMember = await database
			.Set<Marker>()
			.CountAsync(marker => marker.GroupRideId == rideId && marker.CreatedByUserId == userId);

		if (byMember >= limits.MaxPerMemberPerRide)
		{
			return ProblemResult(
				StatusCodes.Status409Conflict,
				"Too many of your markers",
				$"You have already added {byMember} markers to this ride.");
		}

		return null;
	}

	/// <summary>The track path: ownership and one cap (§16.5).</summary>
	private static async Task<IActionResult?> CheckTrackAsync(
		DlrDbContext database,
		Guid trackId,
		Guid userId,
		MarkerOptions limits)
	{
		bool owns = await database
			.Set<Track>()
			.AnyAsync(track => track.Id == trackId && track.OwnerId == userId);

		if (!owns)
		{
			return new NotFoundResult();
		}

		int onTrack = await database.Set<Marker>().CountAsync(marker => marker.TrackId == trackId);

		return onTrack >= limits.MaxPerTrack
			? ProblemResult(
				StatusCodes.Status409Conflict,
				"Track is full of markers",
				$"This track already has {onTrack} markers.")
			: null;
	}

	/// <summary>The caller's membership row with its ride, or null if they are not in it.</summary>
	private static Task<GroupRideMember?> MembershipAsync(
		DlrDbContext database,
		Guid rideId,
		Guid userId) =>
		database
			.Set<GroupRideMember>()
			.Include(member => member.Ride)
			.SingleOrDefaultAsync(member => member.GroupRideId == rideId && member.UserId == userId);

	private static async Task<bool> CanReadAsync(DlrDbContext database, Marker marker, Guid userId) =>
		marker.GroupRideId is { } rideId
			? await database
				.Set<GroupRideMember>()
				.AnyAsync(member => member.GroupRideId == rideId && member.UserId == userId)
			: await database
				.Set<Track>()
				.AnyAsync(track => track.Id == marker.TrackId && track.OwnerId == userId);

	/// <summary>The author, or the organiser (§16.5).</summary>
	private static async Task<bool> CanWriteAsync(DlrDbContext database, Marker marker, Guid userId)
	{
		if (marker.CreatedByUserId == userId)
		{
			return true;
		}

		return marker.GroupRideId is { } rideId
			&& await database
				.Set<GroupRideMember>()
				.AnyAsync(member =>
					member.GroupRideId == rideId
					&& member.UserId == userId
					&& (member.Role == GroupRideRole.Owner || member.Role == GroupRideRole.Leader));
	}

	private static IActionResult? Validate(
		int lat,
		int lon,
		string icon,
		string title,
		short? direction,
		MarkerOptions limits)
	{
		if (lat is < -9_000_000 or > 9_000_000 || lon is < -18_000_000 or > 18_000_000)
		{
			return ProblemResult(
				StatusCodes.Status400BadRequest,
				"Position out of range",
				"Latitude and longitude are degrees scaled by 100000.");
		}

		// Null is a bearing-less marker and is fine; a *present* one has to be a real bearing.
		if (direction is < 0 or > 359)
		{
			return ProblemResult(
				StatusCodes.Status400BadRequest,
				"Direction out of range",
				"A direction is 0 to 359 degrees from true north, or absent.");
		}

		// Length and character set, not membership (§16.2). An icon this version cannot draw is
		// stored anyway, so a newer client's marker survives a round trip through an older server.
		if (!MarkerIcons.IsStorable(icon))
		{
			return ProblemResult(
				StatusCodes.Status400BadRequest,
				"Bad icon key",
				$"An icon key is up to {MarkerIcons.MaxLength} lowercase letters, digits and hyphens.");
		}

		string? cleaned = MarkerText.Clean(title);

		if (cleaned is null)
		{
			return ProblemResult(
				StatusCodes.Status400BadRequest,
				"Title required",
				"A marker needs a label to render beside its icon.");
		}

		return cleaned.Length > limits.TitleMaxChars
			? ProblemResult(
				StatusCodes.Status400BadRequest,
				"Title too long",
				$"A title is at most {limits.TitleMaxChars} characters — it is drawn on the map, " +
				"and a longer one covers the road it refers to.")
			: null;
	}

	private static async Task<MarkerDto> DescribeAsync(DlrDbContext database, Guid markerId) =>
		await Project(database.Set<Marker>().Where(marker => marker.Id == markerId)).SingleAsync();

	private static IQueryable<MarkerDto> Project(IQueryable<Marker> markers) =>
		markers.AsNoTracking().Select(marker => new MarkerDto(
			marker.Id,
			marker.TrackId,
			marker.GroupRideId,
			marker.Lat,
			marker.Lon,
			marker.Icon,
			marker.Title,
			marker.Note,
			marker.DirectionDeg,
			marker.PhotoId,
			marker.CreatedByUserId,
			marker.CreatedBy!.UserName!,
			marker.CreatedUtc,
			marker.UpdatedUtc));

	private static ObjectResult ProblemResult(int status, string title, string detail) =>
		new(new ProblemDetails { Status = status, Title = title, Detail = detail })
		{
			StatusCode = status,
			ContentTypes = { "application/problem+json" },
		};
}
