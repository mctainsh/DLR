using System.Globalization;
using System.Text;

namespace DLR.Core.Markers;

/// <summary>
/// Cleaning the two free-text fields (§16.2).
/// <para>
/// Title and note are user text shown to other people. Blazor escapes by default and MAUI labels
/// are not HTML, so this is a discipline story rather than an XSS one — but the GPX exporter writes
/// both into a file other software reads, and a control character or an unnormalised composition
/// that survives to there is a bug in somebody else's parser with our name on it.
/// </para>
/// </summary>
public static class MarkerText
{
	/// <summary>Trims, strips control characters, and normalises to NFC.</summary>
	/// <param name="value">The raw text.</param>
	/// <returns>The cleaned text, or null when nothing is left.</returns>
	public static string? Clean(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		StringBuilder builder = new(value.Length);

		foreach (char character in value)
		{
			// Newlines survive in a note — somebody typing an address over two lines is not
			// doing anything wrong. Everything else in the control range goes, including the
			// bidirectional overrides that let a title render as something other than what is
			// stored.
			if (character is '\n' or '\t')
			{
				builder.Append(character);

				continue;
			}

			if (char.IsControl(character)
				|| char.GetUnicodeCategory(character) == UnicodeCategory.Format)
			{
				continue;
			}

			builder.Append(character);
		}

		string cleaned = builder.ToString().Trim();

		// NFC, so two spellings of the same accented name compare and render as one thing.
		return cleaned.Length == 0 ? null : cleaned.Normalize(NormalizationForm.FormC);
	}

	/// <summary>
	/// Splits an over-long GPX waypoint name into a title and the remainder (§16.6).
	/// </summary>
	/// <param name="name">The <c>&lt;name&gt;</c> text.</param>
	/// <param name="titleMaxChars">The title column's limit.</param>
	/// <returns>The title, and anything that did not fit.</returns>
	/// <remarks>
	/// Truncating and discarding would lose text somebody typed. The overflow is appended to the
	/// note instead, which is the difference between importing a file and damaging it.
	/// </remarks>
	public static (string Title, string? Overflow) SplitTitle(string? name, int titleMaxChars)
	{
		string cleaned = Clean(name) ?? string.Empty;

		if (cleaned.Length == 0)
		{
			return ("Waypoint", null);
		}

		if (cleaned.Length <= titleMaxChars)
		{
			return (cleaned, null);
		}

		// Broken at a word boundary when there is one near the limit, because a title cut
		// mid-word reads like corruption rather than like a title.
		int cut = cleaned.LastIndexOf(' ', titleMaxChars - 1);

		if (cut < titleMaxChars / 2)
		{
			cut = titleMaxChars;
		}

		return (cleaned[..cut].TrimEnd(), cleaned[cut..].TrimStart());
	}
}
