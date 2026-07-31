using System.Security.Claims;
using DLR.Core.Contracts.Identity;
using DLR.Server.Data.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DLR.Server.Identity;

/// <summary>Forgetting, resetting and changing a password (§7.7, §7.14).</summary>
public static class PasswordEndpoints
{
	/// <summary>Route name for the reset request.</summary>
	public const string ForgotRouteName = "ForgotPassword";

	/// <summary>Route name for completing a reset.</summary>
	public const string ResetRouteName = "ResetPassword";

	/// <summary>Route name for an authenticated change.</summary>
	public const string ChangeRouteName = "ChangePassword";

	/// <summary>Maps the three password endpoints.</summary>
	public static IEndpointRouteBuilder MapPasswords(this IEndpointRouteBuilder endpoints)
	{
		endpoints
			.MapPost("/api/v1/auth/forgot-password", ForgotAsync)
			.AllowAnonymous()
			.WithName(ForgotRouteName)
			.WithSummary("Sends a one-hour reset link, if the address belongs to anyone.");

		endpoints
			.MapPost("/api/v1/auth/reset-password", ResetAsync)
			.AllowAnonymous()
			.WithName(ResetRouteName)
			.WithSummary("Sets a new password and signs out every device.");

		endpoints
			.MapPost("/api/v1/auth/change-password", ChangeAsync)
			.RequireAuthorization()
			.WithName(ChangeRouteName)
			.WithSummary("Changes the password and signs out every other device.");

		return endpoints;
	}

	/// <summary>
	/// Always <c>202</c>, whatever happened (§7.7, §7.8).
	/// <para>
	/// Three cases end here and none of them is distinguishable: no such address, an address
	/// on an account that has not confirmed it, and a link actually sent. A username is a
	/// public handle and enumeration of it is accepted; an email address is not, and this is
	/// the endpoint that would leak one.
	/// </para>
	/// </summary>
	private static async Task<IResult> ForgotAsync(
		ForgotPasswordRequest request,
		HttpContext http,
		UserManager<AppUser> users,
		AccountEmails emails,
		RequestThrottle throttle,
		IOptions<RateLimitOptions> limits)
	{
		if (string.IsNullOrWhiteSpace(request.Email))
		{
			return Results.Accepted();
		}

		// Per address submitted and per caller (§7.8). The first stops one mailbox being
		// buried in reset links; the second stops one machine walking a list of them. Both
		// answer 202 like everything else here — a 429 that only appeared for real addresses
		// would undo the whole point of the endpoint.
		bool withinLimits =
			throttle.TryAcquire(
				$"forgot-email:{request.Email.Trim().ToUpperInvariant()}",
				limits.Value.ForgotPerHourPerEmail,
				TimeSpan.FromHours(1))
			& throttle.TryAcquire(
				$"forgot-ip:{http.Connection.RemoteIpAddress}",
				limits.Value.ForgotPerHourPerAddress,
				TimeSpan.FromHours(1));

		if (!withinLimits)
		{
			return Results.Accepted();
		}

		AppUser? user = await users.FindByEmailAsync(request.Email.Trim());

		// Reset requires a *confirmed* address. An unconfirmed one may belong to somebody who
		// mistyped it — or to somebody else entirely — and honouring it would turn a typo into
		// an account takeover.
		if (user is { EmailConfirmed: true })
		{
			string token = await users.GeneratePasswordResetTokenAsync(user);

			await emails.SendPasswordResetAsync(user, token);
		}

		return Results.Accepted();
	}

	private static async Task<IResult> ResetAsync(
		ResetPasswordRequest request,
		UserManager<AppUser> users,
		RefreshTokenService refresh)
	{
		AppUser? user = await users.FindByIdAsync(request.UserId.ToString());

		if (user is null)
		{
			return Problem(StatusCodes.Status400BadRequest, "Link not valid", InvalidLink);
		}

		IdentityResult result = await users.ResetPasswordAsync(user, request.Token, request.NewPassword);

		if (!result.Succeeded)
		{
			// A rejected password and a stale link are told apart, because they need different
			// things from the person reading: one is "choose a longer one", the other is "ask
			// for a new link". Neither says anything about whether the account exists.
			return result.Errors.Any(error => error.Code.StartsWith("Password", StringComparison.Ordinal))
				? Results.ValidationProblem(new Dictionary<string, string[]>
				{
					[nameof(ResetPasswordRequest.NewPassword)] =
						[.. result.Errors.Select(error => error.Description)],
				})
				: Problem(StatusCodes.Status400BadRequest, "Link not valid", InvalidLink);
		}

		// Every device, including this one. The one place permanent sessions are deliberately
		// broken (§7.7): a reset is what somebody does when they think their account is
		// compromised, and leaving the other sessions alive is exactly the thing they were
		// trying to undo.
		await refresh.RevokeAllForUserAsync(user.Id, RevocationReasons.PasswordReset);

		return Results.NoContent();
	}

	private static async Task<IResult> ChangeAsync(
		ChangePasswordRequest request,
		ClaimsPrincipal caller,
		UserManager<AppUser> users,
		RefreshTokenService refresh)
	{
		if (await caller.LoadAsync(users) is not { } user)
		{
			return Results.Unauthorized();
		}

		IdentityResult result = await users.ChangePasswordAsync(
			user,
			request.CurrentPassword,
			request.NewPassword);

		if (!result.Succeeded)
		{
			return Results.ValidationProblem(new Dictionary<string, string[]>
			{
				[Field(result)] = [.. result.Errors.Select(error => error.Description)],
			});
		}

		// Other devices, not this one. Somebody changing their password in Settings has not
		// asked to be signed out of the phone in their hand, and doing it anyway makes the
		// safe habit annoying enough to avoid.
		await refresh.RevokeAllForUserAsync(
			user.Id,
			RevocationReasons.PasswordReset,
			exceptDeviceId: caller.DeviceId());

		return Results.NoContent();
	}

	private static string Field(IdentityResult result) =>
		result.Errors.Any(error => error.Code == "PasswordMismatch")
			? nameof(ChangePasswordRequest.CurrentPassword)
			: nameof(ChangePasswordRequest.NewPassword);

	private const string InvalidLink =
		"That reset link is not valid. It may have expired — they last one hour — or already " +
		"been used. Ask for a new one.";

	private static IResult Problem(int status, string title, string detail) =>
		Results.Problem(new ProblemDetails { Status = status, Title = title, Detail = detail });
}
