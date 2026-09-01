using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Services;
using Bunit;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Components;

/// <summary>
/// The AGPL §13 source offer footer (§14.6.2). Two properties matter here and both
/// come from the design outline: the licence text is <em>not configurable</em>, and
/// a build's commit is embedded rather than hand-maintained. The tests reach for the
/// values the server would emit and check that the footer renders them verbatim.
/// </summary>
[Collection(SourceOfferFooterCollection.Name)]
public sealed class SourceOfferFooterTests : BunitContext
{
	[Fact]
	public void RendersLicenceSourceAndTruncatedCommit()
	{
		// Give the fake a distinctive commit so we can tell which value the footer picked up.
		FakeApiClient api = new()
		{
			AboutResult = new AboutInfo(
				Licence: "AGPL-3.0-only",
				SourceUrl: "https://github.com/mctainsh/dlr-render-test",
				Commit: "abcd1234567890abcd1234567890abcd12345678",
				Version: "1.0.0+abcd1234",
				BuiltUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
		};
		Services.AddSingleton<IApiClient>(api);

		// Cleared here, next to the render, and never in a constructor - see SourceOfferFooterCache.
		SourceOfferFooterCache.Clear();
		IRenderedComponent<SourceOfferFooter> component = Render<SourceOfferFooter>();

		// The footer fetches About in OnInitializedAsync and re-renders once the task
		// resolves. Poll the DOM until the commit lands rather than racing it.
		component.WaitForAssertion(() =>
		{
			string markup = component.Markup;
			markup.Contains("AGPL-3.0-only", StringComparison.Ordinal)
				.ShouldBeTrue("the licence line is what §13 requires.");
			markup.Contains("mctainsh/dlr-render-test", StringComparison.Ordinal)
				.ShouldBeTrue("the source URL is the §13 offer, rendered verbatim from the About response.");
			markup.Contains("abcd123456", StringComparison.Ordinal)
				.ShouldBeTrue("a commit is rendered truncated so it fits in a footer; the full SHA is in the DOM.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void PlaceholderCopyIsHonestBeforeTheApiAnswers()
	{
		// Even before the async About call resolves, the footer renders the licence as a
		// placeholder - "© Dumb Luck Routes · AGPL-3.0-only" - because that line is true
		// regardless of whether the endpoint answered. The rest fills in later.
		Services.AddSingleton<IApiClient>(new FakeApiClient());

		// Cleared here, next to the render, and never in a constructor - see SourceOfferFooterCache.
		SourceOfferFooterCache.Clear();
		IRenderedComponent<SourceOfferFooter> component = Render<SourceOfferFooter>();

		component.Markup.Contains("AGPL-3.0-only", StringComparison.Ordinal)
			.ShouldBeTrue("the licence text is a constant of the design and must always render.");
	}
}
