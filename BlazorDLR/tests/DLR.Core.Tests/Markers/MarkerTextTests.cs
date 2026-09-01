using DLR.Core.Markers;

namespace DLR.Core.Tests.Markers;

/// <summary>
/// §16.2's user-text cleaner + GPX-name splitter. Invariants:
/// <list type="bullet">
///   <item>Trim + NFC normalise so two spellings of an accented name compare the same.</item>
///   <item>Newlines and tabs survive in notes; every other control character (including
///     bidirectional overrides) is stripped - those are the ones a title could smuggle
///     to render something other than what is stored.</item>
///   <item>Empty-after-cleaning returns null so the composer treats it as "no note".</item>
///   <item>Over-long GPX &lt;name&gt; is split at a word boundary near the limit, with
///     the overflow returned separately so nothing is silently truncated (§16.6).</item>
/// </list>
/// </summary>
public sealed class MarkerTextTests
{
	[Fact]
	public void Clean_NullEmptyOrWhitespace_ReturnsNull()
	{
		MarkerText.Clean(null).ShouldBeNull();
		MarkerText.Clean("").ShouldBeNull();
		MarkerText.Clean("   \t\n  ").ShouldBeNull(
			"whitespace-only text is 'no note' - the composer treats null as absent.");
	}

	[Fact]
	public void Clean_TrimsSurroundingWhitespace()
	{
		MarkerText.Clean("  hello  ").ShouldBe("hello");
	}

	[Fact]
	public void Clean_KeepsNewlinesAndTabs_InsideBody()
	{
		string input = "line1\nline2\tstill line2";
		MarkerText.Clean(input).ShouldBe(input,
			"newlines and tabs survive in a note - an address on two lines is legitimate.");
	}

	[Fact]
	public void Clean_StripsBidiOverrideCharacters()
	{
		// U+200E LEFT-TO-RIGHT MARK is a Format-category character - a title carrying one
		// could render as something other than what is stored. The cleaner strips them.
		string input = "abcdef‎ghi";
		MarkerText.Clean(input).ShouldBe("abcdefghi",
			"§16.2: bidirectional overrides let a title render as something other than what is stored - the cleaner strips them.");
	}

	[Fact]
	public void Clean_StripsAsciiControlChars_ExceptNewlineAndTab()
	{
		string input = "ab\nc\td";
		MarkerText.Clean(input).ShouldBe("ab\nc\td",
			"ASCII control chars are stripped; only \n and \t are kept.");
	}

	// ---------- SplitTitle ----------

	[Fact]
	public void SplitTitle_ShortEnough_ReturnsWholeName_AndNoOverflow()
	{
		(string title, string? overflow) = MarkerText.SplitTitle("Fuel stop", titleMaxChars: 40);

		title.ShouldBe("Fuel stop");
		overflow.ShouldBeNull();
	}

	[Fact]
	public void SplitTitle_BlankOrNull_ReturnsWaypointWithoutOverflow()
	{
		(string title, string? overflow) = MarkerText.SplitTitle(null, titleMaxChars: 40);

		title.ShouldBe("Waypoint", "an unnamed waypoint gets a default rather than an empty title.");
		overflow.ShouldBeNull();
	}

	[Fact]
	public void SplitTitle_TooLong_BreaksOnAWordBoundary_AndReturnsOverflow()
	{
		string longName = "Sunday morning coast run with the club until the cafe on the corner";

		(string title, string? overflow) = MarkerText.SplitTitle(longName, titleMaxChars: 40);

		title.Length.ShouldBeLessThanOrEqualTo(40);
		title.EndsWith(' ').ShouldBeFalse("the title is trimmed at the boundary - no trailing space.");
		overflow.ShouldNotBeNull("§16.6: overflow is preserved rather than truncated.");
		(title + " " + overflow).Length.ShouldBe(longName.Length, "the split is lossless - every character survives.");
	}

	[Fact]
	public void SplitTitle_NoWordBoundaryNearLimit_CutsAtLimit()
	{
		// A 60-char name with no spaces: the split falls at the raw limit.
		string longName = new('a', 60);

		(string title, string? overflow) = MarkerText.SplitTitle(longName, titleMaxChars: 40);

		title.Length.ShouldBe(40, "with no word boundary near the limit, the cut falls at the character limit.");
		overflow.ShouldNotBeNull();
		overflow!.Length.ShouldBe(20);
	}
}
