using System.Collections.Frozen;

namespace DLR.Server.Identity;

/// <summary>
/// Names nothing may register under (§7.2).
/// <para>
/// This is not a politeness rule about squatting. The username is the label on a map pin, so
/// a rider called <c>support</c> or <c>no-reply</c> is a rider who can pose as the service on
/// somebody else's map — the same impersonation problem that ASCII-only solves for homoglyphs,
/// arriving by a different route.
/// </para>
/// </summary>
public static class ReservedUserNames
{
	private static readonly FrozenSet<string> Reserved = new[]
	{
		// The service, and anything a rider would read as the service.
		"dlr", "dumbluckrides", "dumbluck",
		"admin", "administrator", "moderator", "mod", "staff", "team",
		"support", "help", "helpdesk", "contact", "info",
		"system", "root", "operator", "official",
		"security", "abuse", "postmaster", "webmaster", "hostmaster",
		"noreply", "no-reply", "donotreply", "do-not-reply", "mailer-daemon",

		// Routes and reserved words that would read badly in a URL or a member list.
		"api", "auth", "login", "logout", "register", "account", "accounts",
		"me", "user", "users", "settings", "null", "undefined", "anonymous", "guest",
	}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	/// <summary>Whether <paramref name="userName"/> may not be registered.</summary>
	/// <param name="userName">The candidate, as typed.</param>
	public static bool IsReserved(string? userName) =>
		userName is not null && Reserved.Contains(userName);
}
