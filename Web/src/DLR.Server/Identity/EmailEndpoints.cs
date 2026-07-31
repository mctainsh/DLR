using System.Security.Claims;
using DLR.Core.Contracts.Identity;
using DLR.Server.Data.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace DLR.Server.Identity;

/// <summary>Adding a recovery address, and confirming it (§7.7, §7.14).</summary>
public static class EmailEndpoints
{
	/// <summary>Route name for setting an address.</summary>
	public const string SetEmailRouteName = "SetEmail";

	/// <summary>Route name for confirming one.</summary>
	public const string ConfirmEmailRouteName = "ConfirmEmail";

	/// <summary>Route name for resending the link.</summary>
	public const string ResendConfirmationRouteName = "ResendConfirmation";

	/// <summary>Maps the three email endpoints.</summary>
	public static IEndpointRouteBuilder MapEmail(this IEndpointRouteBuilder endpoints)
	{
		endpoints
			.MapPost("/api/v1/auth/email", SetAsync)
			.RequireAuthorization()
			.WithName(SetEmailRouteName)
			.WithSummary("Records a recovery address and sends a 24-hour confirmation link.");

		endpoints
			.MapPost("/api/v1/auth/confirm-email", ConfirmAsync)
			.AllowAnonymous()
			.WithName(ConfirmEmailRouteName)
			.WithSummary("Confirms an address and returns a fresh session.");

		endpoints
			.MapPost("/api/v1/auth/resend-confirmation", ResendAsync)
			.RequireAuthorization()
			.WithName(ResendConfirmationRouteName)
			.WithSummary("Sends the confirmation link again.");

		return endpoints;
	}

	private static async Task<IResult> SetAsync(
		SetEmailRequest request,
		ClaimsPrincipal caller,
		UserManager<AppUser> users,
		AccountEmails emails)
	{
		if (await caller.LoadAsync(users) is not { } user)
		{
			return Results.Unauthorized();
		}

		if (string.IsNullOrWhiteSpace(request.Email))
		{
			return Problem(StatusCodes.Status400BadRequest, "Missing address", "An address is required.");
		}

		string address = request.Email.Trim();

		// Set unconfirmed. Recovery is enabled by confirming, never by typing — an address
		// somebody mistyped, or somebody else's address typed deliberately, must not become a
		// path into this account (§7.7).
		user.Email = address;
		user.EmailConfirmed = false;

		IdentityResult stored = await users.UpdateAsync(user);

		if (!stored.Succeeded)
		{
			return Results.ValidationProblem(new Dictionary<string, string[]>
			{
				[nameof(SetEmailRequest.Email)] = [.. stored.Errors.Select(error => error.Description)],
			});
		}

		string token = await users.GenerateEmailConfirmationTokenAsync(user);

		await emails.SendConfirmationAsync(user, address, token);

		return Results.Accepted();
	}

	private static async Task<IResult> ConfirmAsync(
		ConfirmEmailRequest request,
		UserManager<AppUser> users,
		SessionFactory sessions)
	{
		AppUser? user = await users.FindByIdAsync(request.UserId.ToString());

		if (user is null)
		{
			return Problem(StatusCodes.Status400BadRequest, "Link not valid", InvalidLink);
		}

		IdentityResult confirmed = await users.ConfirmEmailAsync(user, request.Token);

		if (!confirmed.Succeeded)
		{
			return Problem(StatusCodes.Status400BadRequest, "Link not valid", InvalidLink);
		}

		// Fresh tokens, because confirming changes what the account is allowed to do: §7.8's
		// ladder drops the `rst` claim once an address is confirmed, and a client holding the
		// old access token would stay restricted for another fifteen minutes with no
		// explanation.
		return Results.Ok(await sessions.BeginAsync(user, request.DeviceId, request.DeviceName));
	}

	private static async Task<IResult> ResendAsync(
		ClaimsPrincipal caller,
		UserManager<AppUser> users,
		AccountEmails emails)
	{
		if (await caller.LoadAsync(users) is not { } user)
		{
			return Results.Unauthorized();
		}

		// Accepted either way. Whether an address is present and unconfirmed is something the
		// caller already knows about their own account, but answering identically keeps this
		// endpoint from becoming a probe if it is ever reachable another way.
		if (!string.IsNullOrWhiteSpace(user.Email) && !user.EmailConfirmed)
		{
			string token = await users.GenerateEmailConfirmationTokenAsync(user);

			await emails.SendConfirmationAsync(user, user.Email, token);
		}

		return Results.Accepted();
	}

	private const string InvalidLink =
		"That confirmation link is not valid. It may have expired, or already been used. " +
		"Ask for a new one from Settings.";

	private static IResult Problem(int status, string title, string detail) =>
		Results.Problem(new ProblemDetails { Status = status, Title = title, Detail = detail });
}

/// <summary>Loading the caller's account from the <c>sub</c> claim.</summary>
public static class CallerAccount
{
	/// <summary>The account behind a bearer token, or null if it no longer exists.</summary>
	/// <param name="caller">The authenticated principal.</param>
	/// <param name="users">The user store.</param>
	public static async Task<AppUser?> LoadAsync(this ClaimsPrincipal caller, UserManager<AppUser> users) =>
		caller.UserId() is { } userId ? await users.FindByIdAsync(userId.ToString()) : null;
}
