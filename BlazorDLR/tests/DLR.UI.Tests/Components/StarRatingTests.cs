using BlazorDLR.Shared.Components;
using Bunit;
using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;

namespace DLR.UI.Tests.Components;

/// <summary>
/// The read-only star row draws exactly five stars, whatever the average (§6.2).
/// <para>
/// This is a regression file. The row used to be two lines of text - ☆☆☆☆☆ with ★★★★★
/// clipped over the top to a percentage width - which is only correct if the two characters
/// have the same advance width, and in the font the Android WebView picked they do not. A 4.0
/// route drew four stars, most of a fifth, and then a sixth outline that nobody had rated:
/// the clip was a percentage of the empty row’s width being applied to a narrower filled one.
/// </para>
/// <para>
/// Both rows are now the same drawn path, so the count is structural rather than a matter of
/// font metrics. These tests assert the count, because the count is what went wrong - a
/// pixel-accurate half star is not something bUnit can see, but a sixth star is.
/// </para>
/// </summary>
public sealed class StarRatingTests : BunitContext
{
	private IRenderedComponent<StarRating> RenderRow(double? average, int count, int? mine = null) =>
		Render<StarRating>(parameters => parameters
			.Add(p => p.Summary, new TrackRatingSummary(average, count, mine)));

	[Theory]
	[InlineData(null, 0)]
	[InlineData(1.0, 1)]
	[InlineData(2.5, 2)]
	[InlineData(4.0, 1)]
	[InlineData(4.75, 4)]
	[InlineData(5.0, 9)]
	public void EveryAverage_DrawsFiveStarsAndNoMore(double? average, int count)
	{
		IRenderedComponent<StarRating> component = RenderRow(average, count);

		component.FindAll("svg.star").Count.ShouldBe(TrackRatings.MaxStars,
			"a five star scale draws five stars at every value - the bug this replaced drew six");
	}

	[Fact]
	public void AWholeAverage_FillsThatManyStarsCompletely()
	{
		IRenderedComponent<StarRating> component = RenderRow(4.0, 1);

		System.Collections.Generic.IReadOnlyList<AngleSharp.Dom.IElement> fills =
			component.FindAll("svg.star .fill");

		fills.Count.ShouldBe(4);
		fills.ShouldAllBe(fill => fill.GetAttribute("style") == null,
			"a whole star is not clipped");
	}

	[Fact]
	public void AHalfAverage_ClipsTheLastFillToHalfAStar()
	{
		// 2.5 is the value that broke: the fraction is where the two rows disagreed.
		IRenderedComponent<StarRating> component = RenderRow(2.5, 2);

		System.Collections.Generic.IReadOnlyList<AngleSharp.Dom.IElement> fills =
			component.FindAll("svg.star .fill");

		fills.Count.ShouldBe(3, "two whole stars and the half one");
		fills[0].GetAttribute("style").ShouldBeNull();
		fills[1].GetAttribute("style").ShouldBeNull();
		fills[2].GetAttribute("style").ShouldBe("clip-path:inset(0 50% 0 0)");
	}

	[Fact]
	public void AnAverageThatIsNotAHalfStep_IsRoundedTheWayTheServerRoundsIt()
	{
		// 4.75 rounds to 5 whole stars through TrackRatings.ToHalfStars, not to four and three
		// quarters - the widget does not get its own opinion about rounding.
		IRenderedComponent<StarRating> component = RenderRow(4.75, 4);

		component.FindAll("svg.star .fill").Count.ShouldBe(TrackRatings.MaxStars);
	}

	[Fact]
	public void AnUnratedRoute_DrawsFiveOutlinesAndSaysSoInWords()
	{
		IRenderedComponent<StarRating> component = RenderRow(null, 0);

		component.FindAll("svg.star .outline").Count.ShouldBe(TrackRatings.MaxStars);
		component.FindAll("svg.star .fill").ShouldBeEmpty();
		component.Find(".score").TextContent.ShouldBe("Not rated yet");
	}

	[Fact]
	public void TheTappableRow_DrawsTheSameFiveStarsInButtons()
	{
		IRenderedComponent<StarRating> component = Render<StarRating>(parameters => parameters
			.Add(p => p.Summary, new TrackRatingSummary(3.0, 2, 2))
			.Add(p => p.OnRate, (int? _) => { }));

		component.FindAll("button.star-button").Count.ShouldBe(TrackRatings.MaxStars);
		component.FindAll("button.star-button svg.star").Count.ShouldBe(TrackRatings.MaxStars);
		component.FindAll("button.star-button .fill").Count.ShouldBe(2,
			"the reader gave it two, whatever everyone else gave it");
	}
}
