using DLR.Core.Contracts.Identity;

namespace DLR.Server.Identity;

/// <summary>Registration (§7.2, §7.14).</summary>
/// <remarks>
/// The rules live in <see cref="RegistrationService"/> because a browser registers through
/// <see cref="WebAuthEndpoints"/> as well, and the §7.8 ladder is the last thing that should exist
/// in two copies. What is left here is the mobile shape: a JSON body in, a token pair out.
/// </remarks>
public static class RegistrationEndpoints
{
	/// <summary>Route name, so the username-immutability guard can exempt this one endpoint.</summary>
	public const string RegisterRouteName = "Register";

	/// <summary>Maps <c>POST /api/v1/auth/register</c>.</summary>
	public static IEndpointRouteBuilder MapRegistration(this IEndpointRouteBuilder endpoints)
	{
		endpoints
			.MapPost("/api/v1/auth/register", RegisterAsync)
			.AllowAnonymous()
			.WithName(RegisterRouteName)
			.WithSummary("Creates an account from a username, a password and an optional email.");

		return endpoints;
	}

	private static async Task<IResult> RegisterAsync(
		RegisterRequest request,
		HttpContext http,
		RegistrationService registrations,
		SessionFactory sessions)
	{
		RegistrationOutcome outcome = await registrations.RegisterAsync(request, http);

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
		return Results.Created(
			(string?)null,
			await sessions.BeginAsync(outcome.User!, request.DeviceId, request.DeviceName));
	}
}
