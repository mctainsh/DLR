using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Services;
using Bunit;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DLR.UI.Tests.Components;

/// <summary>
/// The failure and edge branches of the AGPL §13 source-offer footer (§14.6.2).
/// The happy-path renders are covered by <see cref="SourceOfferFooterTests"/>;
/// this file pins:
/// <list type="bullet">
///   <item>A short commit renders in full - the truncation is length-conditional.</item>
///   <item>An empty commit renders as "unknown" - never a blank between two separators
///     that would look like the footer was cut off.</item>
///   <item>An API failure (either <see cref="NotImplementedException"/> from a host whose
///     client cannot answer About, or any other exception) falls back to the placeholder
///     line, so the licence text is still visible.</item>
/// </list>
/// </summary>
[Collection(SourceOfferFooterCollection.Name)]
public sealed class SourceOfferFooterFallbackTests : BunitContext
{
	[Fact]
	public void ShortCommit_RendersInFull()
	{
		FakeApiClient api = new()
		{
			AboutResult = new AboutInfo(
				Licence: "AGPL-3.0-only",
				SourceUrl: "https://github.com/mctainsh/dlr",
				Commit: "abc123",
				Version: "1.0.0+abc123",
				BuiltUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
		};
		Services.AddSingleton<IApiClient>(api);

		// Cleared here, next to the render, and never in a constructor - see SourceOfferFooterCache.
		SourceOfferFooterCache.Clear();
		IRenderedComponent<SourceOfferFooter> component = Render<SourceOfferFooter>();

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains(">abc123<", StringComparison.Ordinal).ShouldBeTrue(
				"a commit shorter than the truncation limit renders in full - the truncation is length-conditional.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void EmptyCommit_RendersAsUnknown()
	{
		FakeApiClient api = new()
		{
			AboutResult = new AboutInfo(
				Licence: "AGPL-3.0-only",
				SourceUrl: "https://github.com/mctainsh/dlr",
				Commit: string.Empty,
				Version: "1.0.0",
				BuiltUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
		};
		Services.AddSingleton<IApiClient>(api);

		// Cleared here, next to the render, and never in a constructor - see SourceOfferFooterCache.
		SourceOfferFooterCache.Clear();
		IRenderedComponent<SourceOfferFooter> component = Render<SourceOfferFooter>();

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("unknown", StringComparison.Ordinal).ShouldBeTrue(
				"an empty commit is rendered as 'unknown' rather than as a blank between two separators - a blank looks like a bug.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void ApiThrowsNotImplemented_FootersFallsBackToPlaceholder_WithLicenceLine()
	{
		// A host whose IApiClient cannot answer About throws NotImplementedException rather
		// than returning a fabricated value - see InProcessAboutApiClient's SSR guard. The
		// footer must catch it and render the placeholder line.
		IApiClient api = Substitute.For<IApiClient>();
		api.GetAboutAsync(Arg.Any<CancellationToken>())
			.ThrowsAsync(new NotImplementedException("this host cannot answer /about"));
		Services.AddSingleton(api);

		// Cleared here, next to the render, and never in a constructor - see SourceOfferFooterCache.
		SourceOfferFooterCache.Clear();
		IRenderedComponent<SourceOfferFooter> component = Render<SourceOfferFooter>();

		component.WaitForAssertion(() =>
		{
			// The About endpoint failed - the placeholder line is what a caller sees,
			// which is still honest about the licence (that is not endpoint-dependent).
			string markup = component.Markup;
			markup.Contains("AGPL-3.0-only", StringComparison.Ordinal).ShouldBeTrue(
				"§14.6.2: the licence line is a constant of the design and must render even when the About endpoint failed.");
			markup.Contains("Dumb Luck Routes", StringComparison.Ordinal).ShouldBeTrue(
				"the placeholder line names the app so the copy is complete even without the endpoint answer.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

}
