using DLR.Server.Data.Identity;
using Microsoft.AspNetCore.Identity;

namespace DLR.Server.Identity;

/// <summary>
/// Refuses a password that is already in a public breach corpus (§7.2).
/// <para>
/// This is the composition rules' replacement, not their companion. Requiring a digit and a
/// symbol pushes people toward <c>Passw0rd!</c>, which is in every corpus there is; asking
/// whether this exact string has already leaked measures the thing the rules were a proxy
/// for. It matters more here than in most applications, because for an account registered
/// without an email address the password is the only credential and there is no reset path.
/// </para>
/// </summary>
/// <param name="breaches">The lookup, faked in tests so the outage path is reachable.</param>
public sealed class BreachedPasswordValidator(IBreachedPasswordCheck breaches)
	: IPasswordValidator<AppUser>
{
	/// <summary>Error code for a password found in a breach corpus.</summary>
	public const string BreachedCode = "PasswordBreached";

	/// <inheritdoc />
	public async Task<IdentityResult> ValidateAsync(
		UserManager<AppUser> manager,
		AppUser user,
		string? password)
	{
		if (string.IsNullOrEmpty(password))
		{
			return IdentityResult.Success;
		}

		BreachCheckResult result = await breaches.CheckAsync(password);

		// Every outcome is named, and the discard throws rather than passing. Written the
		// obvious way — a permissive default, or `!= Breached` — the outage clause would be
		// indistinguishable from an oversight, and deleting the Unavailable line would
		// change nothing until the day a real outage arrived. This way a new outcome is a
		// decision somebody has to make.
		return result switch
		{
			BreachCheckResult.NotBreached => IdentityResult.Success,

			// A third-party outage must not stop signups (§7.2). This is the deliberate
			// trade: a password that may well be in the corpus gets through, because the
			// alternative is turning away every new rider while someone else's service is
			// down.
			BreachCheckResult.Unavailable => IdentityResult.Success,

			BreachCheckResult.Breached => IdentityResult.Failed(new IdentityError
			{
				Code = BreachedCode,
				Description =
					"This password has appeared in a public data breach. " +
					"Choose one you have not used anywhere else.",
			}),

			_ => throw new ArgumentOutOfRangeException(
				nameof(result),
				result,
				"A breach outcome nobody has decided what to do with."),
		};
	}
}
