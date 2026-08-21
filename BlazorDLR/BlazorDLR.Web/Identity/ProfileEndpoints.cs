using DLR.Core.Contracts.Identity;
using DLR.Core.Display;
using DLR.Server.Data;
using DLR.Server.Data.Identity;
using DLR.Server.Data.Photos;
using DLR.Server.Data.Rides;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Identity;

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
public static class ProfileEndpoints
{
	/// <summary>Route name for reading one's own profile.</summary>
	public const string GetRouteName = "GetProfile";

	/// <summary>Route name for updating it.</summary>
	public const string UpdateRouteName = "UpdateProfile";

	/// <summary>Route name for reading another rider's shared fields.</summary>
	public const string SharedRouteName = "GetSharedProfile";

	/// <summary>Route name for reading the caller's own home private area (§10.1).</summary>
	public const string PrivateAreaRouteName = "GetPrivateArea";

	/// <summary>Route name for placing or moving it.</summary>
	public const string SetPrivateAreaRouteName = "SetPrivateArea";

	/// <summary>Route name for removing it.</summary>
	public const string ClearPrivateAreaRouteName = "ClearPrivateArea";

	/// <summary>Route name for setting the caller's profile photograph (§7.3).</summary>
	public const string SetAvatarRouteName = "SetAvatar";

	/// <summary>Route name for removing it.</summary>
	public const string ClearAvatarRouteName = "ClearAvatar";

	/// <summary>Route name for the batch avatar lookup every screen that draws names makes.</summary>
	public const string AvatarsRouteName = "GetRiderAvatars";
}

/// <summary>The three optional fields and their three switches (§7.3, §7.14).</summary>
[ApiController]
[Authorize]
public sealed class ProfileController : ControllerBase
{
	/// <summary>
	/// The only route by which one rider's fields reach another (§7.3).
	/// <para>
	/// It answers <see cref="SharedProfile.Empty"/> rather than 404 for a stranger. A 404 would
	/// make this endpoint a membership oracle — ask about an account, learn whether you share a
	/// ride with it — and the empty profile is indistinguishable from a co-member who shares
	/// nothing, which is the common case anyway since all three switches default off.
	/// </para>
	/// </summary>
	[HttpGet("/api/v1/users/{id:guid}/profile", Name = ProfileEndpoints.SharedRouteName)]
	[EndpointSummary("Another rider's shared fields, if a live ride is shared with them.")]
	public async Task<IActionResult> GetSharedAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database)
	{
		if (User.UserId() is not { } viewerId)
		{
			return Unauthorized();
		}

		AppUser? owner = await database.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == id);

		if (owner is null)
		{
			return Ok(SharedProfile.Empty);
		}

		// "Currently", and the ride must not have ended. Profile sharing deliberately does *not*
		// follow the position wind-down (§5.6): that window exists so people can watch each other
		// get home, and there is no equivalent reason to keep a phone number visible for two more
		// hours.
		bool sharesActiveRide = viewerId != id
			&& await database
				.Set<GroupRideMember>()
				.AnyAsync(mine =>
					mine.UserId == viewerId
					&& mine.Ride!.State != GroupRideState.Completed
					&& mine.Ride.State != GroupRideState.Archived
					&& mine.Ride.State != GroupRideState.Cancelled
					&& database.Set<GroupRideMember>().Any(theirs =>
						theirs.GroupRideId == mine.GroupRideId && theirs.UserId == id));

		return Ok(SharedProfile.For(owner, sharesActiveRide));
	}

	[HttpGet("/api/v1/me/profile", Name = ProfileEndpoints.GetRouteName)]
	[EndpointSummary("The caller's own profile values and sharing switches.")]
	public async Task<IActionResult> GetAsync([FromServices] UserManager<AppUser> users) =>
		await User.LoadAsync(users) is { } user
			? Ok(Describe(user))
			: Unauthorized();

	[HttpPut("/api/v1/me/profile", Name = ProfileEndpoints.UpdateRouteName)]
	[EndpointSummary("Updates the optional fields and their sharing switches.")]
	public async Task<IActionResult> UpdateAsync(
		[FromBody] UpdateProfileRequest request,
		[FromServices] UserManager<AppUser> users)
	{
		if (await User.LoadAsync(users) is not { } user)
		{
			return Unauthorized();
		}

		// Before anything is written. A colour that is not #rrggbb is a client bug, and defaulting
		// it quietly would leave a rider retrying a setting that never had a chance of sticking
		// (§16.3). Blank is not a bug — it is how they go back to the default.
		if (!MarkerColours.TryNormalise(request.MarkerColour, out string? markerColour))
		{
			return new BadRequestObjectResult(new ValidationProblemDetails(new Dictionary<string, string[]>
			{
				[nameof(UpdateProfileRequest.MarkerColour)] = ["A marker colour must be #rrggbb, or absent for the default."],
			}))
			{
				ContentTypes = { "application/problem+json" },
			};
		}

		user.DisplayName = Trimmed(request.DisplayName);

		// Identity's own column, reused. PhoneNumberConfirmed is never touched here and must
		// never be read as a gate: SMS verification needs a paid provider the €4 budget does
		// not want, and an SMS reset path would add an account-takeover surface for no
		// benefit. A future contributor who sees that column will otherwise assume
		// verification happened somewhere (§7.3).
		user.PhoneNumber = Trimmed(request.PhoneNumber);

		// Recording and sharing are separate decisions, and turning a switch off never deletes
		// the value. That matters most for email: it remains the recovery address (§7.7) even
		// while hidden from other riders.
		user.ShareDisplayName = request.ShareDisplayName;
		user.SharePhoneNumber = request.SharePhoneNumber;
		user.ShareEmail = request.ShareEmail;

		// No switch of its own: this one is how the rider appears on a map their co-members are
		// already looking at, not a fact about them that could sensibly be withheld (§16.3).
		user.MarkerColour = markerColour;

		IdentityResult result = await users.UpdateAsync(user);

		return result.Succeeded
			? Ok(Describe(user))
			: new BadRequestObjectResult(new ValidationProblemDetails(new Dictionary<string, string[]>
			{
				[string.Empty] = [.. result.Errors.Select(error => error.Description)],
			}))
			{
				ContentTypes = { "application/problem+json" },
			};
	}

	private static OwnProfile Describe(AppUser user) => new(
		user.DisplayName,
		user.PhoneNumber,
		user.Email,
		user.EmailConfirmed,
		user.ShareDisplayName,
		user.SharePhoneNumber,
		user.ShareEmail,
		user.MarkerColour,
		user.AvatarPhotoId);

	// -- Home private area (§10.1) ------------------------------------------------------------
	//
	// Its own sub-resource rather than three more fields on PUT /me/profile, and the separation
	// is load-bearing rather than tidy. That endpoint takes a whole UpdateProfileRequest and
	// writes every field on it, so an area carried inside it would be cleared by any client that
	// had not been taught about it — a rider editing their display name in an older build would
	// silently lose the circle around their house. A privacy control must not be deletable as a
	// side effect of an unrelated save.
	//
	// Nothing here has a sharing switch, because there is nothing to switch: no route anywhere in
	// the server answers with somebody else's area. GetSharedAsync above projects to SharedProfile,
	// which has no field for one.

	/// <summary>
	/// The caller's own area, or the fact that they have none. Only ever their own — the route has
	/// no user id in it, deliberately.
	/// </summary>
	[HttpGet("/api/v1/me/private-area", Name = ProfileEndpoints.PrivateAreaRouteName)]
	[EndpointSummary("The caller's home private area, inside which their device publishes nothing.")]
	public async Task<IActionResult> GetPrivateAreaAsync([FromServices] UserManager<AppUser> users)
	{
		if (await User.LoadAsync(users) is not { } user)
		{
			return Unauthorized();
		}

		return Ok(Area(user));
	}

	/// <summary>
	/// Places or moves it.
	/// <para>
	/// The radius is clamped rather than refused and the centre is refused rather than clamped —
	/// <c>PrivateAreaSettings.Normalised</c>, the same call the phone makes before it sends, so
	/// the two cannot disagree about what was stored. A number outside the offered range is a
	/// rider typing in a box; a centre that is not on the earth is a broken client, and quietly
	/// picking a point for it would put a circle somewhere nobody chose.
	/// </para>
	/// </summary>
	[HttpPut("/api/v1/me/private-area", Name = ProfileEndpoints.SetPrivateAreaRouteName)]
	[EndpointSummary("Places or moves the caller's home private area.")]
	public async Task<IActionResult> SetPrivateAreaAsync(
		[FromBody] PrivateAreaSettings request,
		[FromServices] UserManager<AppUser> users)
	{
		if (await User.LoadAsync(users) is not { } user)
		{
			return Unauthorized();
		}

		if (request.Normalised() is not { } area)
		{
			return new BadRequestObjectResult(new ValidationProblemDetails(new Dictionary<string, string[]>
			{
				[nameof(PrivateAreaSettings.Latitude)] = ["A private area needs a centre on the earth."],
			}))
			{
				ContentTypes = { "application/problem+json" },
			};
		}

		user.PrivateAreaLat = area.Latitude;
		user.PrivateAreaLon = area.Longitude;
		user.PrivateAreaRadiusM = area.RadiusM;

		IdentityResult result = await users.UpdateAsync(user);

		// The stored values, not the posted ones: the radius may have been clamped on the way in,
		// and a screen that reports a number the account is not holding is the kind of lie this
		// feature cannot afford.
		return result.Succeeded ? Ok(Area(user)) : Failed(result);
	}

	/// <summary>
	/// Forgets it, so the account shares from everywhere again. Idempotent — an account with no
	/// area is a 200 and not a 404, because the caller is asking for a state and not for a row.
	/// </summary>
	[HttpDelete("/api/v1/me/private-area", Name = ProfileEndpoints.ClearPrivateAreaRouteName)]
	[EndpointSummary("Removes the caller's home private area.")]
	public async Task<IActionResult> ClearPrivateAreaAsync([FromServices] UserManager<AppUser> users)
	{
		if (await User.LoadAsync(users) is not { } user)
		{
			return Unauthorized();
		}

		// All three together. Two of them without the third is a row that HasPrivateArea reads as
		// unset while still holding a coordinate nobody asked us to keep.
		user.PrivateAreaLat = null;
		user.PrivateAreaLon = null;
		user.PrivateAreaRadiusM = null;

		IdentityResult result = await users.UpdateAsync(user);

		return result.Succeeded ? Ok(PrivateAreaResponse.None) : Failed(result);
	}

	private static PrivateAreaResponse Area(AppUser user) =>
		user is { PrivateAreaLat: { } latitude, PrivateAreaLon: { } longitude, PrivateAreaRadiusM: { } radius }
			? new PrivateAreaResponse(new PrivateAreaSettings(latitude, longitude, radius))
			: PrivateAreaResponse.None;

	private static BadRequestObjectResult Failed(IdentityResult result) =>
		new(new ValidationProblemDetails(new Dictionary<string, string[]>
		{
			[string.Empty] = [.. result.Errors.Select(error => error.Description)],
		}))
		{
			ContentTypes = { "application/problem+json" },
		};

	// -- Profile photograph (§7.3, §16.4) -----------------------------------------------------
	//
	// Its own sub-resource, for the reason the private area above is one: PUT /me/profile replaces
	// the whole profile, so an avatar carried inside it would be cleared by any client that had not
	// been taught about it. A rider editing their phone number in an older build must not lose
	// their photograph as a side effect.
	//
	// There is no sharing switch here and there is no route that withholds it, because the
	// photograph exists to sit beside the username and the username is already readable by every
	// signed-in rider (§7.2). Adding one is the consent; DELETE is how it is withdrawn.

	/// <summary>
	/// Sets the photograph shown beside the caller's username.
	/// </summary>
	/// <remarks>
	/// The image must be one the caller uploaded. Anything else is a 404 rather than a silent
	/// no-op — MarkerController's reasoning, and it bites harder here: a guessed identifier would
	/// otherwise put somebody else's face beside the caller's name on every screen in the app.
	/// </remarks>
	[HttpPut("/api/v1/me/avatar", Name = ProfileEndpoints.SetAvatarRouteName)]
	[Authorize(Policy = AuthorizationPolicies.NotRestricted)]
	[EndpointSummary("Sets the photograph shown beside the caller's username.")]
	public async Task<IActionResult> SetAvatarAsync(
		[FromBody] SetAvatarRequest request,
		[FromServices] UserManager<AppUser> users,
		[FromServices] DlrDbContext database,
		CancellationToken cancellationToken)
	{
		if (await User.LoadAsync(users) is not { } user)
		{
			return Unauthorized();
		}

		bool ownsIt = await database
			.Set<Photo>()
			.AnyAsync(photo => photo.Id == request.PhotoId && photo.OwnerId == user.Id, cancellationToken);

		if (!ownsIt)
		{
			return Problem(
				statusCode: StatusCodes.Status404NotFound,
				title: "No such photo",
				detail: "Upload the image first, and set one you uploaded.");
		}

		user.AvatarPhotoId = request.PhotoId;

		IdentityResult result = await users.UpdateAsync(user);

		return result.Succeeded ? Ok(Describe(user)) : Failed(result);
	}

	/// <summary>
	/// Removes it, so the caller's name is drawn on its own again. Idempotent — an account with no
	/// photograph is a 200 and not a 404, because the caller is asking for a state and not for a row.
	/// </summary>
	/// <remarks>
	/// The <c>photo</c> row is deliberately left alone. It is the rider's own upload and may be
	/// attached to a marker or a comment as well; the §7.11 sweep collects it once nothing points
	/// at it, which is the same contract every other photo reference in the project has.
	/// </remarks>
	[HttpDelete("/api/v1/me/avatar", Name = ProfileEndpoints.ClearAvatarRouteName)]
	[EndpointSummary("Removes the photograph shown beside the caller's username.")]
	public async Task<IActionResult> ClearAvatarAsync([FromServices] UserManager<AppUser> users)
	{
		if (await User.LoadAsync(users) is not { } user)
		{
			return Unauthorized();
		}

		user.AvatarPhotoId = null;

		IdentityResult result = await users.UpdateAsync(user);

		return result.Succeeded ? Ok(Describe(user)) : Failed(result);
	}

	/// <summary>
	/// The photographs for a screenful of usernames, in one request (§7.3).
	/// </summary>
	/// <remarks>
	/// <para>
	/// <strong>A batch, and it has to be.</strong> A ride thread, a member list and a browse page
	/// all draw dozens of names at once; one request per name would turn opening a screen into
	/// forty round trips over a phone connection.
	/// </para>
	/// <para>
	/// <strong>Every name asked about gets a row, including names that do not exist.</strong> A
	/// caller that had to tell "no photograph" from "no such account" by the absence of a row would
	/// be holding a username oracle, and a client that could not cache the negative answer would
	/// ask again on every render. Both problems go away by answering for the question rather than
	/// for the row.
	/// </para>
	/// </remarks>
	[HttpGet("/api/v1/users/avatars", Name = ProfileEndpoints.AvatarsRouteName)]
	[EndpointSummary("The profile photographs for a list of usernames. One request per screen, not per name.")]
	public async Task<IActionResult> GetAvatarsAsync(
		[FromQuery] string? names,
		[FromServices] DlrDbContext database,
		CancellationToken cancellationToken)
	{
		if (User.UserId() is null)
		{
			return Unauthorized();
		}

		// Truncated rather than refused: a client asking about too many names should get the first
		// hundred avatars, not an error that leaves a screen with none.
		List<string> wanted =
		[
			.. (names ?? string.Empty)
				.Split(AvatarLookup.Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.Take(AvatarLookup.MaxNames),
		];

		if (wanted.Count == 0)
		{
			return Ok(Array.Empty<RiderAvatarDto>());
		}

		// Matched on the normalised column, which is what Identity keys uniqueness on — a caller
		// holding "davesmith" off a cached row must find the account stored as "DaveSmith" (§7.2).
		List<string> normalised = [.. wanted.Select(name => name.ToUpperInvariant())];

		Dictionary<string, Guid?> found = await database
			.Users
			.AsNoTracking()
			.Where(user => user.NormalizedUserName != null && normalised.Contains(user.NormalizedUserName))
			.Select(user => new { user.NormalizedUserName, user.AvatarPhotoId })
			.ToDictionaryAsync(row => row.NormalizedUserName!, row => row.AvatarPhotoId, StringComparer.Ordinal, cancellationToken);

		// Echoed back under the name that was asked about, not the stored one. The caller is going
		// to look the answer up by the string it already holds, and handing it a different casing
		// would be a cache that never hits.
		return Ok(wanted
			.Select(name => new RiderAvatarDto(
				name,
				found.TryGetValue(name.ToUpperInvariant(), out Guid? photoId) ? photoId : null))
			.ToList());
	}

	private static string? Trimmed(string? value) =>
		string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
