namespace DLR.Server.Identity;

/// <summary>The §7.2 username rules, as constants the validator and the client both quote.</summary>
public static class UserNameRules
{
	/// <summary>Shortest username. Below this a handle is not a name, it is an initial.</summary>
	public const int MinimumLength = 3;

	/// <summary>Longest username. It has to fit on a map pin.</summary>
	public const int MaximumLength = 20;

	/// <summary>
	/// The only characters a username may contain.
	/// <para>
	/// ASCII-only is a security choice rather than a simplification. Because the unique
	/// handle is also the visible label, Unicode would allow homoglyph impersonation —
	/// <c>DaveSmıth</c> with a dotless i reads as <c>DaveSmith</c> at a glance on a moving
	/// map, and the two are distinct strings, so both can exist at once.
	/// </para>
	/// <para>
	/// Uppercase is <em>mandatory</em> in this set. Identity applies it as a whitelist over
	/// the string as typed, not over the normalised form, so omitting A–Z would reject
	/// <c>DaveSmith</c> outright — fine for a hidden login handle, unacceptable for a name
	/// people read. Identity's own default is dropped rather than extended: it permits
	/// <c>@</c> and <c>+</c>, which §7.2 does not.
	/// </para>
	/// </summary>
	public const string AllowedCharacters =
		"abcdefghijklmnopqrstuvwxyz" +
		"ABCDEFGHIJKLMNOPQRSTUVWXYZ" +
		"0123456789" +
		"-._";
}
