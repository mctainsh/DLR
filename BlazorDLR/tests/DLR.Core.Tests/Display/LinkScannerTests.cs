using DLR.Core.Display;

namespace DLR.Core.Tests.Display;

/// <summary>
/// What becomes a tapable link in a body or description, and - the half that matters - what does
/// not (§17.2).
/// <para>
/// The safety argument §17.2 rests on is that the text a rider sees is the place they go. There is
/// no label syntax, so the only way to break that is a URL which itself reads as somewhere it does
/// not visit. The rejection tests below are that argument, written down.
/// </para>
/// </summary>
public sealed class LinkScannerTests
{
	private static void ShouldStayText(string text, string because) =>
		LinkScanner.Scan(text).ShouldAllBe(run => run.Url == null, because);

	[Theory]
	[InlineData("https://example.org")]
	[InlineData("http://example.org")]
	[InlineData("HTTPS://Example.org/Ridgeway")]
	[InlineData("https://example.org/a/b?c=d#e")]
	[InlineData("https://example.org:8443/x")]
	public void Scan_AWebUrlOnItsOwn_IsOneLinkRunShowingWhatWasTyped(string text)
	{
		TextRun run = LinkScanner.Scan(text).ShouldHaveSingleItem();

		run.Url.ShouldNotBeNull();
		run.Text.ShouldBe(
			text,
			"the whole phishing defence is that the rider reads the destination, so the run may " +
			"not show a tidied-up spelling of it.");
	}

	[Fact]
	public void Scan_UrlInASentence_SplitsIntoProseAndLink()
	{
		IReadOnlyList<TextRun> runs = LinkScanner.Scan("See https://example.org for the detour.");

		runs.Count.ShouldBe(3);
		runs[0].ShouldBe(new TextRun("See ", null));
		runs[1].Text.ShouldBe("https://example.org");
		runs[1].Url.ShouldBe("https://example.org/");
		runs[2].ShouldBe(new TextRun(" for the detour.", null));
	}

	[Fact]
	public void Scan_NewlinesAndSpacingSurviveAsTyped()
	{
		IReadOnlyList<TextRun> runs = LinkScanner.Scan("Line one\n\nhttps://example.org\n\nLine two");

		string.Concat(runs.Select(run => run.Text)).ShouldBe(
			"Line one\n\nhttps://example.org\n\nLine two",
			"the description screens render pre-wrap, so a run that trimmed whitespace would " +
			"throw away the paragraphs the rider wrote.");
	}

	[Theory]
	[InlineData("javascript:alert(1)")]
	[InlineData("data:text/html;base64,PHNjcmlwdD4=")]
	[InlineData("file:///etc/passwd")]
	[InlineData("intent://scan/#Intent;scheme=zxing;end")]
	[InlineData("mailto:someone@example.org")]
	public void Scan_ANonWebScheme_StaysText(string text) =>
		ShouldStayText(text, "http and https are the only schemes that ever become tapable (§17.2).");

	[Fact]
	public void Scan_UserinfoBeforeTheHost_StaysText() =>
		ShouldStayText(
			"https://dlr.example.org@evil.example/x",
			"this is the one spelling where a bare URL reads as a host it will not visit, which " +
			"is exactly what §17.2 refused links over.");

	[Theory]
	[InlineData("xhttps://evil.example")]
	[InlineData("seehttp://evil.example")]
	public void Scan_ASchemeMidWord_StaysText(string text) =>
		ShouldStayText(text, "a link starting mid-token would show one thing and visit another.");

	[Fact]
	public void Scan_ADotlessHost_StaysText() =>
		ShouldStayText("https://example", "a dotless host is a typo far more often than a link.");

	[Theory]
	[InlineData("Try https://example.org.")]
	[InlineData("Try https://example.org, then turn.")]
	[InlineData("(https://example.org)")]
	[InlineData("\"https://example.org\"")]
	public void Scan_TrailingPunctuation_BelongsToTheSentence(string text) =>
		LinkScanner.Scan(text).Single(run => run.Url is not null)
			.Text.ShouldBe("https://example.org");

	[Fact]
	public void Scan_BracketsTheUrlOpened_AreKept() =>
		LinkScanner.Scan("https://en.example.org/wiki/Ridgeway_(road)")
			.ShouldHaveSingleItem()
			.Text.ShouldBe("https://en.example.org/wiki/Ridgeway_(road)");

	[Fact]
	public void Scan_ManyTrailingBrackets_AreTrimmedWithoutRescanning()
	{
		string text = "https://example.org/x" + new string(')', 1_900);

		LinkScanner.Scan(text).Single(run => run.Url is not null)
			.Text.ShouldBe(
				"https://example.org/x",
				"the trim carries its bracket depths; recounting per character trimmed made a " +
				"rider-typed body quadratic to draw.");
	}

	[Fact]
	public void Scan_TwoUrls_AreTwoLinks()
	{
		IReadOnlyList<TextRun> runs = LinkScanner.Scan("https://a.example and https://b.example");

		runs.Count(run => run.Url is not null).ShouldBe(2);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void Scan_NothingToShow_IsNoRuns(string? text) =>
		LinkScanner.Scan(text).ShouldBeEmpty();

	[Fact]
	public void Scan_ProseWithNoSchemeSeparator_IsOneRunAndNotSplit() =>
		LinkScanner.Scan("Gravel from the cattle grid to the summit.")
			.ShouldHaveSingleItem()
			.ShouldBe(new TextRun("Gravel from the cattle grid to the summit.", null));

	[Fact]
	public void Scan_AMalformedScheme_DoesNotStallTheScan() =>
		string.Concat(LinkScanner.Scan("http:// and http://x and https://ok.example").Select(run => run.Text))
			.ShouldBe("http:// and http://x and https://ok.example");
}
