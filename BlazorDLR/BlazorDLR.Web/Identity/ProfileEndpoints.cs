using DLR.Core.Contracts.Identity;
using DLR.Core.Display;
using DLR.Server.Data;
using DLR.Server.Data.Identity;
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
		user.MarkerColour);

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

	private static string? Trimmed(string? value) =>
		string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
