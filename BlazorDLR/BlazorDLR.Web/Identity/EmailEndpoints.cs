using System.Security.Claims;
using DLR.Core.Contracts.Identity;
using DLR.Server.Data.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace DLR.Server.Identity;

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
public static class EmailEndpoints
{
	/// <summary>Route name for setting an address.</summary>
	public const string SetEmailRouteName = "SetEmail";

	/// <summary>Route name for confirming one.</summary>
	public const string ConfirmEmailRouteName = "ConfirmEmail";

	/// <summary>Route name for resending the link.</summary>
	public const string ResendConfirmationRouteName = "ResendConfirmation";
}

/// <summary>Adding a recovery address, and confirming it (§7.7, §7.14).</summary>
[ApiController]
public sealed class EmailController : ControllerBase
{
	[HttpPost("/api/v1/auth/email", Name = EmailEndpoints.SetEmailRouteName)]
	[Authorize]
	[EndpointSummary("Records a recovery address and sends a 24-hour confirmation link.")]
	public async Task<IActionResult> SetAsync(
		[FromBody] SetEmailRequest request,
		[FromServices] UserManager<AppUser> users,
		[FromServices] AccountEmails emails)
	{
		if (await User.LoadAsync(users) is not { } user)
		{
			return Unauthorized();
		}

		if (string.IsNullOrWhiteSpace(request.Email))
		{
			return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Missing address", detail: "An address is required.");
		}

		string address = request.Email.Trim();

		// Set unconfirmed. Recovery is enabled by confirming, never by typing - an address
		// somebody mistyped, or somebody else's address typed deliberately, must not become a
		// path into this account (§7.7).
		user.Email = address;
		user.EmailConfirmed = false;

		IdentityResult stored = await users.UpdateAsync(user);

		if (!stored.Succeeded)
		{
			return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
			{
				[nameof(SetEmailRequest.Email)] = [.. stored.Errors.Select(error => error.Description)],
			}));
		}

		string token = await users.GenerateEmailConfirmationTokenAsync(user);

		await emails.SendConfirmationAsync(user, address, token);

		return Accepted();
	}

	[HttpPost("/api/v1/auth/confirm-email", Name = EmailEndpoints.ConfirmEmailRouteName)]
	[AllowAnonymous]
	[EndpointSummary("Confirms an address and returns a fresh session.")]
	public async Task<IActionResult> ConfirmAsync(
		[FromBody] ConfirmEmailRequest request,
		[FromServices] UserManager<AppUser> users,
		[FromServices] SessionFactory sessions)
	{
		AppUser? user = await users.FindByIdAsync(request.UserId.ToString());

		if (user is null)
		{
			return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Link not valid", detail: InvalidLink);
		}

		IdentityResult confirmed = await users.ConfirmEmailAsync(user, request.Token);

		if (!confirmed.Succeeded)
		{
			return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Link not valid", detail: InvalidLink);
		}

		// Fresh tokens, because confirming changes what the account is allowed to do: §7.8's
		// ladder drops the `rst` claim once an address is confirmed, and a client holding the
		// old access token would stay restricted for another fifteen minutes with no
		// explanation.
		return Ok(await sessions.BeginAsync(user, request.DeviceId, request.DeviceName));
	}

	[HttpPost("/api/v1/auth/resend-confirmation", Name = EmailEndpoints.ResendConfirmationRouteName)]
	[Authorize]
	[EndpointSummary("Sends the confirmation link again.")]
	public async Task<IActionResult> ResendAsync(
		[FromServices] UserManager<AppUser> users,
		[FromServices] AccountEmails emails)
	{
		if (await User.LoadAsync(users) is not { } user)
		{
			return Unauthorized();
		}

		// Accepted either way. Whether an address is present and unconfirmed is something the
		// caller already knows about their own account, but answering identically keeps this
		// endpoint from becoming a probe if it is ever reachable another way.
		if (!string.IsNullOrWhiteSpace(user.Email) && !user.EmailConfirmed)
		{
			string token = await users.GenerateEmailConfirmationTokenAsync(user);

			await emails.SendConfirmationAsync(user, user.Email, token);
		}

		return Accepted();
	}

	private const string InvalidLink =
		"That confirmation link is not valid. It may have expired, or already been used. " +
		"Ask for a new one from Settings.";
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
