using System.Net;
using DLR.Core.Contracts.Identity;
using DLR.Server.Data.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DLR.Server.Identity;

/// <summary>Registration (§7.2, §7.14).</summary>
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
		UserManager<AppUser> users,
		SessionFactory sessions,
		RegistrationLadder ladder,
		RequestThrottle throttle,
		AccountEmails emails,
		IOptions<RateLimitOptions> limits,
		TimeProvider clock)
	{
		IPAddress? client = http.Connection.RemoteIpAddress;

		if (!throttle.TryAcquire(
			$"register:{client}",
			limits.Value.RegisterPerHourPerAddress,
			TimeSpan.FromHours(1)))
		{
			// A ceiling above the ladder rather than a substitute for it (§7.8): the ladder
			// decides what an account costs, this decides how fast anyone may try.
			return Results.StatusCode(StatusCodes.Status429TooManyRequests);
		}

		bool emailRequired = await ladder.RequiresEmailAsync(client);

		if (emailRequired && string.IsNullOrWhiteSpace(request.Email))
		{
			return Results.ValidationProblem(
				new Dictionary<string, string[]>
				{
					[nameof(RegisterRequest.Email)] =
					[
						"An email address is required to register from this connection, and " +
						"must be confirmed before you can create or join a group ride.",
					],
				},
				title: "Email address required");
		}

		AppUser user = new()
		{
			UserName = request.UserName,

			// The address that registered this account, for the ladder to count (§7.8). The
			// nightly job nulls it after 30 days — long enough to throttle with, short enough
			// not to be a standing record of where people signed up (§7.11).
			CreatedByIp = client,
			CreatedUtc = clock.GetUtcNow(),

			// Past the threshold, so the social surface stays shut until an address is
			// confirmed. Recording a solo ride is not affected.
			RequiresEmailConfirmation = emailRequired,

			// Stamped at creation rather than left to default. The column is not nullable, so
			// the alternative is year 0001 — an account that reads as two thousand years idle
			// to the §7.11 sweep from the moment it exists.
			LastActiveUtc = clock.GetUtcNow(),

			// Empty is not a value. Identity normalises "" to "" and would then enforce
			// uniqueness over it, so the second person to submit a blank email would be
			// told the address was taken.
			Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
		};

		// An address somebody else already holds (§7.8).
		//
		// Saying "that email is taken" would make this endpoint an oracle for whether any
		// given person has an account — a question worth refusing outright for a
		// location-sharing app. The *username* is enumerable because it is a public handle
		// drawn on a map; an address is not.
		//
		// So registration proceeds and the address is simply not attached: it is not this
		// caller's to attach, and there is no way to tell them so without answering the
		// question. Whoever does hold it gets told instead — and if that turns out to be the
		// same person, that email is precisely the thing they needed to read.
		AppUser? owner = user.Email is null ? null : await users.FindByEmailAsync(user.Email);

		if (owner is not null)
		{
			user.Email = null;
		}

		IdentityResult result = await users.CreateAsync(user, request.Password);

		if (!result.Succeeded)
		{
			return Results.ValidationProblem(AsProblems(result));
		}

		if (owner is not null)
		{
			await emails.SendRegistrationAttemptAsync(owner);
		}
		else if (user.Email is not null)
		{
			// §7.2's flow ends "if email supplied: send 24 h confirmation link". Load-bearing
			// on the ladder path especially: an account created past the threshold is
			// restricted until it confirms, so registering without sending the link would
			// leave it restricted with no way out.
			string token = await users.GenerateEmailConfirmationTokenAsync(user);

			await emails.SendConfirmationAsync(user, user.Email, token);
		}

		// Registering signs you in — §7.2's flow ends "issue access + permanent refresh token",
		// not "now go and log in". Sending someone who just chose a password to a login screen
		// to type it again is the kind of thing that reads as a bug because it is one.
		//
		// No Location header: there is deliberately no endpoint that resolves an account to
		// a profile (§7.14), so there is no URI to point at. 201 still says what happened.
		return Results.Created((string?)null, await sessions.BeginAsync(user, request.DeviceId, request.DeviceName));
	}

	/// <summary>
	/// Identity's errors, keyed by the field they are about.
	/// <para>
	/// The duplicate-username message is deliberately specific, which is the one place this
	/// project accepts enumeration. Uniqueness means registration cannot avoid saying whether
	/// a name is taken, and a username is a public handle drawn on a map rather than a private
	/// identifier — so login stays generic (§7.4) and this does not.
	/// </para>
	/// </summary>
	private static Dictionary<string, string[]> AsProblems(IdentityResult result)
	{
		Dictionary<string, List<string>> byField = [];

		foreach (IdentityError error in result.Errors)
		{
			string field = error.Code switch
			{
				"DuplicateUserName" or "InvalidUserName" => nameof(RegisterRequest.UserName),
				UserNameValidator.InvalidLengthCode or UserNameValidator.ReservedCode
					=> nameof(RegisterRequest.UserName),
				"DuplicateEmail" or "InvalidEmail" => nameof(RegisterRequest.Email),
				_ when error.Code.StartsWith("Password", StringComparison.Ordinal)
					=> nameof(RegisterRequest.Password),

				// An unrecognised code still has to reach the caller. Filing it under a
				// field it is not about would be worse than filing it under none.
				_ => string.Empty,
			};

			if (!byField.TryGetValue(field, out List<string>? messages))
			{
				byField[field] = messages = [];
			}

			messages.Add(error.Description);
		}

		return byField.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray());
	}
}
