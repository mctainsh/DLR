using AngleSharp.Dom;
using BlazorDLR.Shared.Components;
using Bunit;

namespace DLR.UI.Tests.Components;

/// <summary>
/// The component that makes a URL in a body or description tapable (§17.2).
/// <para>
/// <c>LinkScannerTests</c> - the sibling in DLR.Core.Tests - pins which strings become links.
/// What is pinned here is the part that could not be caught there: that a link is an anchor
/// rather than injected markup, that it leaves the app, and that prose beside it is still
/// escaped.
/// </para>
/// </summary>
public sealed class LinkedTextTests : BunitContext
{
	private IRenderedComponent<LinkedText> Draw(string? text) =>
		Render<LinkedText>(parameters => parameters.Add(p => p.Text, text));

	[Fact]
	public void AUrl_BecomesAnAnchorToItselfAndLeavesTheApp()
	{
		IRenderedComponent<LinkedText> component = Draw("See https://example.org for the detour.");

		IElement anchor = component.Find("a.linked");
		anchor.GetAttribute("href").ShouldBe("https://example.org/");
		anchor.TextContent.ShouldBe("https://example.org");
		anchor.GetAttribute("target").ShouldBe(
			"_blank",
			"MAUI's BlazorWebView sends a _blank anchor to the system browser, which is what " +
			"keeps a link from navigating the WebView the app is running in.");
		anchor.GetAttribute("rel").ShouldBe("noopener noreferrer nofollow ugc");

		component.Markup.ShouldContain("See ");
		component.Markup.ShouldContain(" for the detour.");
	}

	[Fact]
	public void MarkupAroundAUrl_IsStillEscaped()
	{
		IRenderedComponent<LinkedText> component =
			Draw("<b>x</b> https://example.org <i>y</i><script>alert(1)</script>");

		component.FindAll("a.linked").Count.ShouldBe(1);
		component.FindAll("b").ShouldBeEmpty();
		component.FindAll("i").ShouldBeEmpty();
		component.FindAll("script").ShouldBeEmpty();
		component.Markup.ShouldContain("&lt;script&gt;");
	}

	[Theory]
	[InlineData("javascript:alert(1)")]
	[InlineData("https://dlr.example.org@evil.example/x")]
	public void ALinkThatWouldLie_IsDrawnAsText(string text)
	{
		IRenderedComponent<LinkedText> component = Draw(text);

		component.FindAll("a").ShouldBeEmpty();
		component.Markup.ShouldNotBeEmpty();
	}

	[Fact]
	public void ANewBody_IsRescanned()
	{
		IRenderedComponent<LinkedText> component = Draw("no link here");
		component.FindAll("a.linked").ShouldBeEmpty();

		component.Render(parameters => parameters.Add(p => p.Text, "https://example.org"));

		component.Find("a.linked").GetAttribute("href").ShouldBe(
			"https://example.org/",
			"the runs are cached against the text they were scanned from, so a body that changes " +
			"must not keep the previous body's runs.");
	}

	[Fact]
	public void NoText_DrawsNothing() => Draw(null).Markup.Trim().ShouldBeEmpty();
}
