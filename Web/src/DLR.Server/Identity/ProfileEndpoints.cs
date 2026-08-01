using System.Security.Claims;
using DLR.Core.Contracts.Identity;
using DLR.Server.Data;
using DLR.Server.Data.Identity;
using DLR.Server.Data.Rides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Identity;

/// <summary>The three optional fields and their three switches (§7.3, §7.14).</summary>
public static class ProfileEndpoints
{
	/// <summary>Route name for reading one's own profile.</summary>
	public const string GetRouteName = "GetProfile";

	/// <summary>Route name for updating it.</summary>
	public const string UpdateRouteName = "UpdateProfile";

	/// <summary>Route name for reading another rider's shared fields.</summary>
	public const string SharedRouteName = "GetSharedProfile";

	/// <summary>Maps the profile endpoints.</summary>
	public static IEndpointRouteBuilder MapProfile(this IEndpointRouteBuilder endpoints)
	{
		endpoints
			.MapGet("/api/v1/me/profile", GetAsync)
			.RequireAuthorization()
			.WithName(GetRouteName)
			.WithSummary("The caller's own profile values and sharing switches.");

		endpoints
			.MapPut("/api/v1/me/profile", UpdateAsync)
			.RequireAuthorization()
			.WithName(UpdateRouteName)
			.WithSummary("Updates the optional fields and their sharing switches.");

		endpoints
			.MapGet("/api/v1/users/{id:guid}/profile", GetSharedAsync)
			.RequireAuthorization()
			.WithName(SharedRouteName)
			.WithSummary("Another rider's shared fields, if a live ride is shared with them.");

		return endpoints;
	}

	/// <summary>
	/// The only route by which one rider's fields reach another (§7.3).
	/// <para>
	/// It answers <see cref="SharedProfile.Empty"/> rather than 404 for a stranger. A 404 would
	/// make this endpoint a membership oracle — ask about an account, learn whether you share a
	/// ride with it — and the empty profile is indistinguishable from a co-member who shares
	/// nothing, which is the common case anyway since all three switches default off.
	/// </para>
	/// </summary>
	private static async Task<IResult> GetSharedAsync(
		Guid id,
		ClaimsPrincipal caller,
		DlrDbContext database)
	{
		if (caller.UserId() is not { } viewerId)
		{
			return Results.Unauthorized();
		}

		AppUser? owner = await database.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == id);

		if (owner is null)
		{
			return Results.Ok(SharedProfile.Empty);
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

		return Results.Ok(SharedProfile.For(owner, sharesActiveRide));
	}

	private static async Task<IResult> GetAsync(ClaimsPrincipal caller, UserManager<AppUser> users) =>
		await caller.LoadAsync(users) is { } user
			? Results.Ok(Describe(user))
			: Results.Unauthorized();

	private static async Task<IResult> UpdateAsync(
		UpdateProfileRequest request,
		ClaimsPrincipal caller,
		UserManager<AppUser> users)
	{
		if (await caller.LoadAsync(users) is not { } user)
		{
			return Results.Unauthorized();
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

		IdentityResult result = await users.UpdateAsync(user);

		return result.Succeeded
			? Results.Ok(Describe(user))
			: Results.ValidationProblem(new Dictionary<string, string[]>
			{
				[string.Empty] = [.. result.Errors.Select(error => error.Description)],
			});
	}

	private static OwnProfile Describe(AppUser user) => new(
		user.DisplayName,
		user.PhoneNumber,
		user.Email,
		user.EmailConfirmed,
		user.ShareDisplayName,
		user.SharePhoneNumber,
		user.ShareEmail);

	private static string? Trimmed(string? value) =>
		string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
