namespace DLR.Core.Display;

/// <summary>
/// One piece of a body or description on its way to the screen: either prose, or a URL the
/// rider typed (§17.2).
/// </summary>
/// <param name="Text">Exactly what the rider typed, which is what is shown.</param>
/// <param name="Url">
/// The absolute URI to navigate to, or <c>null</c> when this run is prose. Not
/// character-for-character <see cref="Text"/> - it is the parsed and normalised form.
/// </param>
public readonly record struct TextRun(string Text, string? Url);

/// <summary>
/// Finds the URLs in a plain-text body (§17.2).
/// <para>
/// Bodies and descriptions are stored, transported and re-rendered as plain text; this is the
/// only place that ever says otherwise, and it says it as a list of runs rather than a string of
/// markup. That is the point of the design: a caller cannot render the result as HTML by
/// accident, because there is no HTML to render.
/// </para>
/// <para>
/// There is deliberately no label syntax anywhere in the app. The reason §17.2 refused links for so
/// long is that a link whose visible text disagrees with its destination is a phishing primitive,
/// and the only complete defence is that no rider can write one. So the shown text is the URL,
/// and the rules in <see cref="Parse"/> keep that true even when the URL itself is the lie.
/// </para>
/// </summary>
public static class LinkScanner
{
	/// <summary>
	/// The separator every linkable URL contains, and the search this class is driven by.
	/// <para>
	/// An ordinal <see cref="string.IndexOf(string, StringComparison)"/> for it is vectorised,
	/// where walking the string looking for a case-insensitive "http" is not - and prose that is
	/// not a URL essentially never contains the trigram, so the overwhelmingly common input (a
	/// comment with no link in it) settles in one pass with one allocation.
	/// </para>
	/// </summary>
	private const string SchemeSeparator = "://";

	/// <summary>The only schemes that ever become a link.</summary>
	private static readonly string[] Schemes = ["https", "http"];

	/// <summary>
	/// Sentence punctuation that is far more often the writer's than the URL's, trimmed off the
	/// end of a match. <c>/</c> and <c>#</c> are absent on purpose - both are part of a URL.
	/// </summary>
	private const string TrailingPunctuation = ".,;:!?'\"*_~";

	/// <summary>
	/// Splits <paramref name="text"/> into prose and URLs, in order.
	/// </summary>
	/// <param name="text">A cleaned body or description.</param>
	/// <returns>
	/// The runs, with consecutive prose merged so a caller emits one text node per gap. Empty
	/// when there is nothing to show.
	/// </returns>
	public static IReadOnlyList<TextRun> Scan(string? text)
	{
		if (string.IsNullOrEmpty(text))
			return [];

		int separator = text.IndexOf(SchemeSeparator, StringComparison.Ordinal);

		if (separator < 0)
			return [new TextRun(text, null)];

		List<TextRun> runs = [];
		int proseStart = 0;

		while (separator >= 0)
		{
			int resume = separator + SchemeSeparator.Length;

			if (SchemeStart(text, separator) is { } start)
			{
				int end = EndOfCandidate(text, start);

				if (Parse(text[start..end]) is { } url)
				{
					if (start > proseStart)
						runs.Add(new TextRun(text[proseStart..start], null));

					runs.Add(new TextRun(text[start..end], url));
					proseStart = end;
					resume = Math.Max(resume, end);
				}
			}

			separator = resume >= text.Length
				? -1
				: text.IndexOf(SchemeSeparator, resume, StringComparison.Ordinal);
		}

		if (proseStart < text.Length)
			runs.Add(new TextRun(text[proseStart..], null));

		return runs;
	}

	/// <summary>
	/// Where the scheme in front of <paramref name="separator"/> starts, or <c>null</c> when what
	/// is in front of it is not one of <see cref="Schemes"/> standing on a word boundary.
	/// </summary>
	/// <remarks>
	/// The boundary matters: without it <c>xhttps://evil.example</c> scans as a link whose shown
	/// text starts mid-word, which is the same "shows one thing, visits another" this class
	/// exists to prevent.
	/// </remarks>
	private static int? SchemeStart(string text, int separator)
	{
		foreach (string scheme in Schemes)
		{
			int start = separator - scheme.Length;

			if (start < 0 || !IsBoundary(text, start))
				continue;

			if (string.Compare(text, start, scheme, 0, scheme.Length, StringComparison.OrdinalIgnoreCase) == 0)
				return start;
		}

		return null;
	}

	private static bool IsBoundary(string text, int index)
	{
		if (index == 0)
			return true;

		char before = text[index - 1];

		return char.IsWhiteSpace(before) || before is '(' or '[' or '{' or '<' or '\"' or '\'';
	}

	/// <summary>Where a candidate stops: at whitespace, then back over the writer's punctuation.</summary>
	/// <remarks>
	/// The bracket depths are carried rather than recounted per character trimmed. Recounting is
	/// quadratic in the candidate, and the candidate is rider-typed - a body that is one URL and
	/// two thousand close brackets is a cheap thing to send and was a measurable cost to draw.
	/// </remarks>
	private static int EndOfCandidate(string text, int start)
	{
		int end = start;
		int round = 0, square = 0, curly = 0;

		while (end < text.Length && !char.IsWhiteSpace(text[end]) && !char.IsControl(text[end]))
		{
			switch (text[end])
			{
				case '(': round++; break;
				case ')': round--; break;
				case '[': square++; break;
				case ']': square--; break;
				case '{': curly++; break;
				case '}': curly--; break;
			}

			end++;
		}

		// A closing bracket belongs to the URL only if the URL opened it. "(see
		// https://example.org)" ends at the bracket; the Wikipedia article about a
		// disambiguated term keeps it.
		while (end > start)
		{
			switch (text[end - 1])
			{
				case ')' when round < 0: round++; break;
				case ']' when square < 0: square++; break;
				case '}' when curly < 0: curly++; break;
				case char last when TrailingPunctuation.Contains(last): break;
				default: return end;
			}

			end--;
		}

		return end;
	}

	/// <summary>The absolute URI a candidate denotes, or <c>null</c> when it may not be a link.</summary>
	private static string? Parse(string candidate)
	{
		if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
			return null;

		if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
			return null;

		// The spoof this whole class is built around: the shown text leads with a host the
		// browser treats as a username and never visits.
		if (uri.UserInfo.Length > 0)
			return null;

		// A dotless host is a typo ("https://example") far more often than it is somebody
		// linking an intranet box from a ride thread, and a typo that renders as a tapable
		// link is worse than one that renders as the text it is.
		return uri.Host.Contains('.') ? uri.AbsoluteUri : null;
	}
}
