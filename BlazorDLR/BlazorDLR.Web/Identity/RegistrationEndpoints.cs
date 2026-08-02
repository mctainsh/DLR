using DLR.Core.Contracts.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DLR.Server.Identity;

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
/// <remarks>
/// The rules live in <see cref="RegistrationService"/> because a browser registers through
/// <see cref="WebAuthController"/> as well, and the §7.8 ladder is the last thing that should exist
/// in two copies. What is left here is the mobile shape: a JSON body in, a token pair out.
/// </remarks>
public static class RegistrationEndpoints
{
	/// <summary>Route name, so the username-immutability guard can exempt this one endpoint.</summary>
	public const string RegisterRouteName = "Register";
}

/// <summary>Registration (§7.2, §7.14).</summary>
[ApiController]
public sealed class RegistrationController : ControllerBase
{
	[HttpPost("/api/v1/auth/register", Name = RegistrationEndpoints.RegisterRouteName)]
	[AllowAnonymous]
	[EndpointSummary("Creates an account from a username, a password and an optional email.")]
	public async Task<IActionResult> RegisterAsync(
		[FromBody] RegisterRequest request,
		[FromServices] RegistrationService registrations,
		[FromServices] SessionFactory sessions)
	{
		RegistrationOutcome outcome = await registrations.RegisterAsync(request, HttpContext);

		if (outcome.Problem is { } problem)
		{
			return problem;
		}

		// Registering signs you in — §7.2's flow ends "issue access + permanent refresh token",
		// not "now go and log in". Sending someone who just chose a password to a login screen
		// to type it again is the kind of thing that reads as a bug because it is one.
		//
		// No Location header: there is deliberately no endpoint that resolves an account to
		// a profile (§7.14), so there is no URI to point at. 201 still says what happened.
		return Created(
			(string?)null,
			await sessions.BeginAsync(outcome.User!, request.DeviceId, request.DeviceName));
	}
}
