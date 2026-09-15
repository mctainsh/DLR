namespace BlazorDLR.Shared.Services;

/// <summary>
/// Where a screen that stands in front of the app sends somebody afterwards (§18.6).
/// <para>
/// <strong>One leading slash, and not two.</strong> The value arrives off the query string, so it
/// is whatever was in the URL that opened the screen. An absolute value would make the last button
/// of the app's welcome mat an open redirect to somebody else's site, and a protocol-relative one -
/// <c>//evil.example/x</c> - is the spelling that looks like a path and is not: browsers read it as
/// a host. A guard that only checks the first character lets it straight through.
/// </para>
/// <para>
/// Shared because there are two of these gates now, the introduction and the terms, and both carry
/// a <c>return=</c>. The second one was written with the weaker check, which is the argument for
/// this being a function rather than a line each screen remembers.
/// </para>
/// </summary>
public static class SafeReturn
{
	/// <summary>The app's own root, and what anything unacceptable falls back to.</summary>
	public const string Home = "/";

	/// <summary>
	/// The requested route if it is a path within this app, or <see cref="Home"/>.
	/// </summary>
	/// <param name="requested">The raw <c>return=</c> value, or null when there was none.</param>
	public static string Or(string? requested) =>
		requested is { Length: > 1 } target && target[0] == '/' && target[1] != '/'
			? target
			: Home;
}
