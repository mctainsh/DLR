using System.Net;
using DLR.Core.Contracts.Identity;
using DLR.Server.Data.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DLR.Server.Identity;

/// <summary>
/// Creating an account (§7.2, §7.8) — everything up to but not including the session.
/// <para>
/// <strong>Extracted in SRV-34 because a browser registers too.</strong> The rules here are the
/// ones it would be worst to have two copies of: the §7.8 per-address ladder, the rate limit above
/// it, the duplicate-address answer that must not become an enumeration oracle, and the
/// confirmation link a ladder-restricted account needs in order to stop being restricted. A second
/// copy on the web route is how one of them ends up subtly different — and the different one would
/// be the route an abuser reaches with a browser.
/// </para>
/// </summary>
/// <param name="users">Identity.</param>
/// <param name="ladder">§7.8's row-counting ladder.</param>
/// <param name="throttle">§7.8's rate limits.</param>
/// <param name="emails">The confirmation and the "somebody used your address" notice.</param>
/// <param name="limits">The thresholds.</param>
/// <param name="clock">The project's clock (§10.4).</param>
public sealed class RegistrationService(
	UserManager<AppUser> users,
	RegistrationLadder ladder,
	RequestThrottle throttle,
	AccountEmails emails,
	IOptions<RateLimitOptions> limits,
	TimeProvider clock)
{
	/// <summary>Creates the account, or explains why not.</summary>
	/// <param name="request">What the caller asked for.</param>
	/// <param name="http">The request, for the client address the ladder counts by.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public async Task<RegistrationOutcome> RegisterAsync(
		RegisterRequest request,
		HttpContext http,
		CancellationToken cancellationToken = default)
	{
		_ = cancellationToken;

		IPAddress? client = http.Connection.RemoteIpAddress;

		if (!throttle.TryAcquire(
			$"register:{client}",
			limits.Value.RegisterPerHourPerAddress,
			TimeSpan.FromHours(1)))
		{
			// A ceiling above the ladder rather than a substitute for it (§7.8): the ladder
			// decides what an account costs, this decides how fast anyone may try.
			return RegistrationOutcome.Refused(
				new StatusCodeResult(StatusCodes.Status429TooManyRequests));
		}

		bool emailRequired = await ladder.RequiresEmailAsync(client);

		if (emailRequired && string.IsNullOrWhiteSpace(request.Email))
		{
			return RegistrationOutcome.Refused(ValidationProblem(
				new Dictionary<string, string[]>
				{
					[nameof(RegisterRequest.Email)] =
					[
						"An email address is required to register from this connection, and " +
						"must be confirmed before you can create or join a group ride.",
					],
				},
				title: "Email address required"));
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
		// Saying "that email is taken" would make this an oracle for whether any given person
		// has an account — a question worth refusing outright for a location-sharing app. The
		// *username* is enumerable because it is a public handle drawn on a map; an address is
		// not.
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
			return RegistrationOutcome.Refused(ValidationProblem(AsProblems(result)));
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

		return RegistrationOutcome.Created(user);
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

	/// <summary>
	/// The equivalent of <c>Results.ValidationProblem</c> for the MVC controllers this service
	/// feeds (§7.2). Returning an <see cref="IActionResult"/> keeps the shape identical — a
	/// <c>ProblemDetails</c> body with a 400 status — without dragging the minimal-API
	/// <c>IResult</c> back through the controllers that consume this.
	/// </summary>
	private static IActionResult ValidationProblem(
		IDictionary<string, string[]> errors,
		string? title = null) =>
		new BadRequestObjectResult(new ValidationProblemDetails(errors)
		{
			Title = title,
		})
		{
			ContentTypes = { "application/problem+json" },
		};
}

/// <summary>Whether an account was created, and what to answer if it was not.</summary>
/// <param name="User">The new account, when there is one.</param>
/// <param name="Problem">The refusal, when there is one. Exactly one of the two is set.</param>
public readonly record struct RegistrationOutcome(AppUser? User, IActionResult? Problem)
{
	/// <summary>The account exists; the caller decides what kind of session to give it.</summary>
	public static RegistrationOutcome Created(AppUser user) => new(user, null);

	/// <summary>Refused, with the answer already shaped.</summary>
	public static RegistrationOutcome Refused(IActionResult problem) => new(null, problem);
}
