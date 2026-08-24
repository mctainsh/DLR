using Microsoft.Extensions.Options;

namespace DLR.Server.Admin;

/// <summary>
/// Who the server treats as an administrator (§14.6).
/// <para>
/// <strong>A list of usernames in configuration, not a column and not a role table.</strong> The
/// context deliberately extends <c>IdentityUserContext</c> rather than <c>IdentityDbContext</c>
/// because ride access is decided by membership and consent rather than by granted roles — adding
/// the four role tables to carry a list of two names would undo that for the one case it does not
/// fit. This list also has a property no column has: it is set by whoever controls the deployment,
/// so an administrator cannot appoint another one through the app.
/// </para>
/// <para>
/// <strong>Read per request, never baked into a token.</strong> A claim would be simpler to check
/// and would take effect an hour late: an account removed from the roster would stay an
/// administrator until its access token expired, which is the wrong way round for the one
/// permission worth revoking in a hurry. <see cref="IOptionsMonitor{T}"/> picks up an edited
/// <c>appsettings.json</c> without a restart, for the same reason.
/// </para>
/// </summary>
/// <param name="options">The configured roster, re-read whenever the file changes.</param>
public sealed class AdminRoster(IOptionsMonitor<AdminOptions> options)
{
	/// <summary>
	/// Whether this username is on the roster.
	/// </summary>
	/// <param name="userName">The name off the caller's token, or null for an anonymous caller.</param>
	/// <returns><c>true</c> only for a non-blank name that appears in the configured list.</returns>
	/// <remarks>
	/// Case-insensitive, because Identity normalises usernames and the person editing the config
	/// file is typing from memory. Blank is never an administrator — an empty entry in the list
	/// would otherwise promote every caller whose token carried no name.
	/// </remarks>
	public bool IsAdmin(string? userName) =>
		!string.IsNullOrWhiteSpace(userName) && Everyone().Contains(userName.Trim());

	/// <summary>The roster as configured, for a caller checking many names at once.</summary>
	/// <returns>The names, trimmed, with blanks dropped, matched case-insensitively.</returns>
	/// <remarks>
	/// <see cref="IsAdmin"/> is written in terms of this rather than beside it, so a name is
	/// normalised in one place and the two cannot answer differently.
	/// </remarks>
	public IReadOnlySet<string> Everyone() =>
		options.CurrentValue.Users
			.Where(entry => !string.IsNullOrWhiteSpace(entry))
			.Select(entry => entry.Trim())
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The <c>Admins</c> section.
/// <para>
/// The section is a bare JSON array, so it binds through <see cref="Users"/> with
/// <c>Bind(section, o =&gt; o.Users)</c> rather than the usual <c>Configure&lt;T&gt;(section)</c>
/// — see <c>AdminRegistration</c>. Keeping the shape in the file is worth the small awkwardness
/// here: <c>"Admins": [ "JRM" ]</c> is what an operator expects to write.
/// </para>
/// </summary>
public sealed class AdminOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "Admins";

	/// <summary>
	/// The usernames with administrative access. Empty by default, which is the safe default: a
	/// deployment that has not named anybody has no administration screen rather than an open one.
	/// </summary>
	public IReadOnlyList<string> Users { get; set; } = [];
}
