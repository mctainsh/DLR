using DLR.Core.Contracts.Identity;
using DLR.Server.Data.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DLR.Server.Identity;

/// <summary>Signing in (§7.4, §7.14).</summary>
public static class TokenEndpoints
{
	/// <summary>Route name.</summary>
	public const string TokenRouteName = "Token";

	/// <summary>
	/// What a caller is told when the username is unknown, the password is wrong, or the
	/// account does not have a password at all. One string for all three, because any
	/// difference between them is a way to ask the server whether an account exists.
	/// </summary>
	public const string InvalidCredentials = "Invalid username or password.";

	/// <summary>Maps <c>POST /api/v1/auth/token</c>.</summary>
	public static IEndpointRouteBuilder MapToken(this IEndpointRouteBuilder endpoints)
	{
		endpoints
			.MapPost("/api/v1/auth/token", ExchangeAsync)
			.AllowAnonymous()
			.WithName(TokenRouteName)
			.WithSummary("Exchanges credentials for an access token.");

		return endpoints;
	}

	private static Task<IResult> ExchangeAsync(
		TokenRequest request,
		UserManager<AppUser> users,
		SessionFactory sessions,
		RefreshTokenService refresh,
		DummyPasswordVerifier dummy,
		ActivityTracker activity,
		HttpContext http,
		RequestThrottle throttle,
		IOptions<RateLimitOptions> limits) =>
		request.GrantType switch
		{
			GrantTypes.Password =>
				PasswordGrantAsync(request, users, sessions, dummy, http, throttle, limits),

			GrantTypes.Refresh =>
				RefreshGrantAsync(request, users, sessions, refresh, activity, throttle, limits),

			_ => Task.FromResult(Problem(
				StatusCodes.Status400BadRequest,
				"Unsupported grant type",
				$"'{request.GrantType}' is not a grant this server issues tokens for.")),
		};

	private static async Task<IResult> PasswordGrantAsync(
		TokenRequest request,
		UserManager<AppUser> users,
		SessionFactory sessions,
		DummyPasswordVerifier dummy,
		HttpContext http,
		RequestThrottle throttle,
		IOptions<RateLimitOptions> limits)
	{
		if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrEmpty(request.Password))
		{
			return Problem(
				StatusCodes.Status400BadRequest,
				"Missing credentials",
				"The password grant needs a username and a password.");
		}

		// Both §7.8 rows, and they answer different attacks: per address stops one machine
		// working through a password list, per username stops a distributed attempt on one
		// account that the address limit would never see. Non-short-circuiting `&` on purpose
		// — both counters must record the attempt, or the cheaper limit hides the other.
		// Checked before the lookup, so a throttled caller learns nothing about who exists.
		bool withinLimits =
			throttle.TryAcquire(
				$"login-ip:{http.Connection.RemoteIpAddress}",
				limits.Value.LoginPerMinutePerAddress,
				TimeSpan.FromMinutes(1))
			& throttle.TryAcquire(
				$"login-user:{request.UserName.ToUpperInvariant()}",
				limits.Value.LoginPerHourPerUserName,
				TimeSpan.FromHours(1));

		if (!withinLimits)
		{
			return Results.StatusCode(StatusCodes.Status429TooManyRequests);
		}

		AppUser? user = await users.FindByNameAsync(request.UserName);

		if (user is null)
		{
			// Not a shortcut and not defensive coding: without this the endpoint answers an
			// unknown username in a fraction of the time it answers a known one, and the
			// generic message above stops meaning anything (§7.8).

			dummy.BurnTime(request.Password);

			return Unauthorised(InvalidCredentials);
		}

		if (await users.IsLockedOutAsync(user))
		{
			return LockedOut();
		}

		if (!await users.CheckPasswordAsync(user, request.Password))
		{
			// Counts the failure, and locks the account on the fifth one (§7.8).
			await users.AccessFailedAsync(user);

			return await users.IsLockedOutAsync(user)
				? LockedOut()
				: Unauthorised(InvalidCredentials);
		}

		await users.ResetAccessFailedCountAsync(user);

		return Results.Ok(await sessions.BeginAsync(user, request.DeviceId, request.DeviceName));
	}

	/// <summary>
	/// The call a client makes at every app start, and the only reason "never sign in again"
	/// is true (§7.4).
	/// </summary>
	private static async Task<IResult> RefreshGrantAsync(
		TokenRequest request,
		UserManager<AppUser> users,
		SessionFactory sessions,
		RefreshTokenService refresh,
		ActivityTracker activity,
		RequestThrottle throttle,
		IOptions<RateLimitOptions> limits)
	{
		if (string.IsNullOrWhiteSpace(request.RefreshToken))
		{
			return Problem(
				StatusCodes.Status400BadRequest,
				"Missing refresh token",
				"The refresh grant needs a refresh token.");
		}

		RefreshOutcome outcome = await refresh.RedeemAsync(request.RefreshToken);

		if (outcome.Status is RefreshStatus.FamilyRevoked)
		{
			// Said plainly, because the client has to stop retrying and sign the rider in
			// again. §7.10 also emails a security alert here when an address is known —
			// which is where that lands, since this is the one branch that means theft.
			return Problem(
				StatusCodes.Status401Unauthorized,
				"Session ended",
				"This session was ended because its refresh token was used twice. Sign in again.");
		}

		if (outcome.Status is not RefreshStatus.Rotated || outcome.RefreshToken is null)
		{
			return Unauthorised("That refresh token is not valid.");
		}

		// Per device, and far looser than the password grant: this is an app starting up
		// rather than a human typing, and holding it to five a minute would throttle the one
		// call that makes permanent sessions work. Applied after redemption, because the
		// device is not known until the token has been looked up.
		if (!throttle.TryAcquire(
			$"refresh:{outcome.DeviceId}",
			limits.Value.RefreshPerMinutePerDevice,
			TimeSpan.FromMinutes(1)))
		{
			return Results.StatusCode(StatusCodes.Status429TooManyRequests);
		}

		AppUser? user = await users.FindByIdAsync(outcome.UserId.ToString());

		if (user is null)
		{
			// The account went away under a live session — the inactivity sweep (§7.11) is
			// the way that happens. A distinguishable reason is owed to the client so it can
			// say something better than "something went wrong".
			return Problem(
				StatusCodes.Status401Unauthorized,
				"Account no longer exists",
				"This account has been deleted.");
		}

		// The whole of §7.10's activity tracking, and it costs nothing: the client was
		// making this call at app start anyway, so there is no extra endpoint and no extra
		// round trip. The write itself is throttled to one an hour.
		await activity.RecordAsync(user.Id, outcome.DeviceId);

		return Results.Ok(sessions.Continue(user, outcome.DeviceId, outcome.RefreshToken));
	}

	/// <summary>
	/// Being locked out is stated plainly rather than folded into the generic message.
	/// <para>
	/// It leaks that the account exists — but §7.2 already publishes that, since usernames
	/// are unique handles drawn on a map and registration has to say when one is taken. What
	/// it buys is a rider who stops retyping a password that was never going to work, which
	/// is the difference between waiting fifteen minutes and filing a support request.
	/// </para>
	/// </summary>
	private static IResult LockedOut() => Problem(
		StatusCodes.Status401Unauthorized,
		"Account locked",
		"Too many failed sign-in attempts. Try again in a few minutes.");

	private static IResult Unauthorised(string detail) =>
		Problem(StatusCodes.Status401Unauthorized, "Sign-in failed", detail);

	private static IResult Problem(int status, string title, string detail) =>
		Results.Problem(new ProblemDetails
		{
			Status = status,
			Title = title,
			Detail = detail,
		});
}
