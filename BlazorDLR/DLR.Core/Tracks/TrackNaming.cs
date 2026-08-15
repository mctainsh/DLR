using DLR.Core.Markers;

namespace DLR.Core.Tracks;

/// <summary>
/// What a track may be called (§15.1).
/// <para>
/// <strong>One rule, three callers.</strong> The recorder asks a rider for a name before it will
/// save (§15.1), the rename endpoint takes one, and the importer derives one from the file. All
/// three end up in the same 120-character column and in the same <c>&lt;name&gt;</c> element of a
/// GPX export, so the trimming and the limit live here rather than three times over — the §15.7
/// rule about stats applies just as well to text.
/// </para>
/// </summary>
public static class TrackNaming
{
	/// <summary>
	/// The longest name that is stored, matching <c>track.name</c>. Stated here as well as on the
	/// column so a client can refuse an over-long name at the keyboard rather than at the database.
	/// </summary>
	public const int MaxLength = 120;

	/// <summary>
	/// A name as it should be stored: cleaned and trimmed, or <c>null</c> when nothing is left.
	/// </summary>
	/// <param name="name">What the rider typed, or what a file carried.</param>
	/// <remarks>
	/// The character-level work is <see cref="MarkerText.Clean"/>'s, deliberately reused: a track
	/// name and a marker title are both user text that this app writes into a GPX file for other
	/// software to read, and a control character surviving into one of them is the same bug either
	/// way.
	/// </remarks>
	public static string? Clean(string? name) => MarkerText.Clean(name);

	/// <summary>
	/// Whether this is a name the app can accept from somebody who was asked for one — present,
	/// not just whitespace, and short enough to store.
	/// </summary>
	/// <param name="name">The candidate.</param>
	public static bool IsUsable(string? name) => Clean(name) is { Length: > 0 and <= MaxLength };

	/// <summary>
	/// Cleaned, and cut to <see cref="MaxLength"/> if it is longer.
	/// </summary>
	/// <param name="name">The candidate.</param>
	/// <returns>Something storable, or <c>null</c> when there was nothing to store.</returns>
	/// <remarks>
	/// For names nobody was asked for — the <c>&lt;name&gt;</c> of an imported track, or the file
	/// it came in. Refusing an import because a planning tool wrote a sentence into that element
	/// would be damaging a file over a column width; a rider who dislikes the result renames it.
	/// Where a person <em>was</em> asked, <see cref="IsUsable"/> and a refusal is the honest answer.
	/// </remarks>
	public static string? Clamp(string? name) =>
		Clean(name) is { } cleaned
			? cleaned.Length <= MaxLength ? cleaned : cleaned[..MaxLength].TrimEnd()
			: null;
}
