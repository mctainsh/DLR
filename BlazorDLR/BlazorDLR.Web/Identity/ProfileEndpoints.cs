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

	private static string? Trimmed(string? value) =>
		string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
