using System.Security.Claims;
using DLR.Core.Contracts.Identity;
using DLR.Server.Data.Identity;
using Microsoft.AspNetCore.Identity;

namespace DLR.Server.Identity;

/// <summary>The three optional fields and their three switches (§7.3, §7.14).</summary>
public static class ProfileEndpoints
{
	/// <summary>Route name for reading one's own profile.</summary>
	public const string GetRouteName = "GetProfile";

	/// <summary>Route name for updating it.</summary>
	public const string UpdateRouteName = "UpdateProfile";

	/// <summary>Maps the two profile endpoints.</summary>
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

		return endpoints;
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
