namespace DLR.Server.Tests.Admin;

/// <summary>
/// The <c>Admins</c> roster, as configuration a test factory can be built with (§14.6).
/// <para>
/// One helper rather than one per suite: the section is a bare JSON array, so the keys are
/// <c>Admins:0</c>, <c>Admins:1</c> and so on, and three copies of that shape are three places to
/// fix when it moves.
/// </para>
/// </summary>
internal static class AdminRosterSettings
{
	/// <summary>Builds the settings overlay naming these accounts, and nobody else.</summary>
	/// <param name="names">The usernames on the roster. None means an empty roster.</param>
	/// <returns>Settings for <c>DlrWebApplicationFactory.CreateAsync</c>.</returns>
	internal static Dictionary<string, string?> Roster(params string[] names)
	{
		Dictionary<string, string?> settings = [];

		for (int index = 0; index < names.Length; index++)
		{
			settings[$"Admins:{index}"] = names[index];
		}

		// An empty roster still has to overwrite the one in appsettings.json, or every test here
		// would inherit whichever names that file happens to ship with.
		if (names.Length == 0)
		{
			settings["Admins:0"] = string.Empty;
		}

		return settings;
	}
}
