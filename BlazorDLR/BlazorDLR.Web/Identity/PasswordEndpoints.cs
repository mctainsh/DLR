using DLR.Core.Contracts.Identity;
using DLR.Server.Data.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DLR.Server.Identity;

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
public static class PasswordEndpoints
{
	/// <summary>Route name for the reset request.</summary>
	public const string ForgotRouteName = "ForgotPassword";

	/// <summary>Route name for completing a reset.</summary>
	public const string ResetRouteName = "ResetPassword";

	/// <summary>Route name for an authenticated change.</summary>
	public const string ChangeRouteName = "ChangePassword";
}

/// <summary>Forgetting, resetting and changing a password (§7.7, §7.14).</summary>
[ApiController]
public sealed class PasswordController : ControllerBase
{
	/// <summary>
	/// Always <c>202</c>, whatever happened (§7.7, §7.8).
	/// <para>
	/// Three cases end here and none of them is distinguishable: no such address, an address
	/// on an account that has not confirmed it, and a link actually sent. A username is a
	/// public handle and enumeration of it is accepted; an email address is not, and this is
	/// the endpoint that would leak one.
	/// </para>
	/// </summary>
	[HttpPost("/api/v1/auth/forgot-password", Name = PasswordEndpoints.ForgotRouteName)]
	[AllowAnonymous]
	[EndpointSummary("Sends a one-hour reset link, if the address belongs to anyone.")]
	public async Task<IActionResult> ForgotAsync(
		[FromBody] ForgotPasswordRequest request,
		[FromServices] UserManager<AppUser> users,
		[FromServices] AccountEmails emails,
		[FromServices] RequestThrottle throttle,
		[FromServices] IOptions<RateLimitOptions> limits)
	{
		if (string.IsNullOrWhiteSpace(request.Email))
		{
			return Accepted();
		}

		// Per address submitted and per caller (§7.8). The first stops one mailbox being
		// buried in reset links; the second stops one machine walking a list of them. Both
		// answer 202 like everything else here - a 429 that only appeared for real addresses
		// would undo the whole point of the endpoint.
		bool withinLimits =
			throttle.TryAcquire(
				$"forgot-email:{request.Email.Trim().ToUpperInvariant()}",
				limits.Value.ForgotPerHourPerEmail,
				TimeSpan.FromHours(1))
			& throttle.TryAcquire(
				$"forgot-ip:{HttpContext.Connection.RemoteIpAddress}",
				limits.Value.ForgotPerHourPerAddress,
				TimeSpan.FromHours(1));

		if (!withinLimits)
		{
			return Accepted();
		}

		AppUser? user = await users.FindByEmailAsync(request.Email.Trim());

		// Reset requires a *confirmed* address. An unconfirmed one may belong to somebody who
		// mistyped it - or to somebody else entirely - and honouring it would turn a typo into
		// an account takeover.
		if (user is { EmailConfirmed: true })
		{
			string token = await users.GeneratePasswordResetTokenAsync(user);

			await emails.SendPasswordResetAsync(user, token);
		}

		return Accepted();
	}

	[HttpPost("/api/v1/auth/reset-password", Name = PasswordEndpoints.ResetRouteName)]
	[AllowAnonymous]
	[EndpointSummary("Sets a new password and signs out every device.")]
	public async Task<IActionResult> ResetAsync(
		[FromBody] ResetPasswordRequest request,
		[FromServices] UserManager<AppUser> users,
		[FromServices] RefreshTokenService refresh)
	{
		AppUser? user = await users.FindByIdAsync(request.UserId.ToString());

		if (user is null)
		{
			return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Link not valid", detail: InvalidLink);
		}

		IdentityResult result = await users.ResetPasswordAsync(user, request.Token, request.NewPassword);

		if (!result.Succeeded)
		{
			// A rejected password and a stale link are told apart, because they need different
			// things from the person reading: one is "choose a longer one", the other is "ask
			// for a new link". Neither says anything about whether the account exists.
			return result.Errors.Any(error => error.Code.StartsWith("Password", StringComparison.Ordinal))
				? ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
				{
					[nameof(ResetPasswordRequest.NewPassword)] =
						[.. result.Errors.Select(error => error.Description)],
				}))
				: Problem(statusCode: StatusCodes.Status400BadRequest, title: "Link not valid", detail: InvalidLink);
		}

		// Every device, including this one. The one place permanent sessions are deliberately
		// broken (§7.7): a reset is what somebody does when they think their account is
		// compromised, and leaving the other sessions alive is exactly the thing they were
		// trying to undo.
		await refresh.RevokeAllForUserAsync(user.Id, RevocationReasons.PasswordReset);

		return NoContent();
	}

	[HttpPost("/api/v1/auth/change-password", Name = PasswordEndpoints.ChangeRouteName)]
	[Authorize]
	[EndpointSummary("Changes the password and signs out every other device.")]
	public async Task<IActionResult> ChangeAsync(
		[FromBody] ChangePasswordRequest request,
		[FromServices] UserManager<AppUser> users,
		[FromServices] RefreshTokenService refresh)
	{
		if (await User.LoadAsync(users) is not { } user)
		{
			return Unauthorized();
		}

		IdentityResult result = await users.ChangePasswordAsync(
			user,
			request.CurrentPassword,
			request.NewPassword);

		if (!result.Succeeded)
		{
			return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
			{
				[Field(result)] = [.. result.Errors.Select(error => error.Description)],
			}));
		}

		// Other devices, not this one. Somebody changing their password in Settings has not
		// asked to be signed out of the phone in their hand, and doing it anyway makes the
		// safe habit annoying enough to avoid.
		await refresh.RevokeAllForUserAsync(
			user.Id,
			RevocationReasons.PasswordReset,
			exceptDeviceId: User.DeviceId());

		return NoContent();
	}

	private static string Field(IdentityResult result) =>
		result.Errors.Any(error => error.Code == "PasswordMismatch")
			? nameof(ChangePasswordRequest.CurrentPassword)
			: nameof(ChangePasswordRequest.NewPassword);

	private const string InvalidLink =
		"That reset link is not valid. It may have expired - they last one hour - or already " +
		"been used. Ask for a new one.";
}
