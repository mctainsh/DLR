using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Services;
using Bunit;
using DLR.Core.Display;

namespace DLR.UI.Tests.Components;

/// <summary>
/// The panel over the live map naming who is just up the road and just behind (§5.4).
/// <para>
/// Which four riders and in what order belongs to <c>NeighbourList</c> and is tested there. What
/// is left for a renderer is what only a renderer can answer: that the rows come out in the order
/// they were given, that each carries the colour its pin is drawn in, that the gap is in words
/// rather than a sign, that the reader's own row is marked as theirs, and that a panel with
/// nothing to say draws nothing at all rather than an empty box over the map.
/// </para>
/// </summary>
public sealed class NeighbourPanelTests : BunitContext
{
	private static NeighbourRow Row(
		string name,
		double relativeMetres,
		bool isSelf = false,
		string colour = "#3b82f6",
		MemberPresence presence = MemberPresence.Sharing) =>
		new(Guid.NewGuid(), name, colour, isSelf, presence, relativeMetres);

	[Fact]
	public void EveryRowIsDrawn_InTheOrderItWasGiven()
	{
		IRenderedComponent<NeighbourPanel> component = Render<NeighbourPanel>(parameters => parameters
			.Add(p => p.Rows,
			[
				Row("First", 900),
				Row("Me", 0, isSelf: true),
				Row("Last", -400),
			]));

		component.FindAll(".live-neighbours li")
			.Select(row => row.QuerySelector(".name")!.TextContent.Trim())
			.ShouldBe(["First", "Me", "Last"],
				"the list arrives in road order and the panel must not resort it — the shape of it is "
				+ "the answer to 'am I off the front or off the back'.");
	}

	[Fact]
	public void TheGapIsSaidInWords_NotByPositionAlone()
	{
		IRenderedComponent<NeighbourPanel> component = Render<NeighbourPanel>(parameters => parameters
			.Add(p => p.Rows, [Row("Ahead", 900), Row("Me", 0, isSelf: true), Row("Behind", -400)]));

		string text = component.Find(".live-neighbours").TextContent;

		text.ShouldContain("+ 900 m");
		text.ShouldContain("- 400 m");
	}

	/// <summary>
	/// The name is drawn wearing the colour that rider's pin is drawn in (§16.3), ink and all. The
	/// panel and the map naming the same person differently is worse than the panel carrying no
	/// colour at all.
	/// </summary>
	[Fact]
	public void EachRowCarriesTheColourThatRidersPinIsDrawnIn()
	{
		IRenderedComponent<NeighbourPanel> component = Render<NeighbourPanel>(parameters => parameters
			.Add(p => p.Rows, [Row("Orange", 300, colour: "#ff8800"), Row("Me", 0, isSelf: true)]));

		string style = component.Find(".live-neighbours li .name").GetAttribute("style").ShouldNotBeNull();

		style.ShouldContain("#ff8800");
		style.ShouldContain(MarkerColours.ForegroundFor("#ff8800"));
	}

	[Fact]
	public void TheReadersOwnRow_SaysItIsTheirs_InWordsAndNotOnlyByStyle()
	{
		IRenderedComponent<NeighbourPanel> component = Render<NeighbourPanel>(parameters => parameters
			.Add(p => p.Rows, [Row("Ahead", 300), Row("Me", 0, isSelf: true)]));

		component.FindAll(".live-neighbours li.self").Count.ShouldBe(1);
		component.Find(".live-neighbours li.self").TextContent.ShouldContain("you", customMessage:
			"the point everything else is measured from says so, rather than reporting that it is "
			+ "level with itself.");
	}

	/// <summary>
	/// Their pin is still on the map and it stopped moving (§5.6). The number is still the best
	/// answer there is, and a gap that has quietly frozen must not read as one that is holding.
	/// </summary>
	[Fact]
	public void ARiderWhoHasGoneQuiet_HasTheirGapMarkedAsFrozen()
	{
		IRenderedComponent<NeighbourPanel> component = Render<NeighbourPanel>(parameters => parameters
			.Add(p => p.Rows,
			[
				Row("Tunnel", 600, presence: MemberPresence.NoSignal),
				Row("Me", 0, isSelf: true),
			]));

		component.Find(".live-neighbours .stale").TextContent.Trim().ShouldBe("+ 600 m");
		component.Find(".live-neighbours .stale").GetAttribute("title").ShouldNotBeNullOrWhiteSpace(
			"colour is the first thing to go through a visor, so the state is available as text too.");
	}

	[Fact]
	public void WithNothingToSay_NothingIsDrawn()
	{
		IRenderedComponent<NeighbourPanel> component = Render<NeighbourPanel>(parameters => parameters
			.Add(p => p.Rows, []));

		component.FindAll(".live-neighbours").ShouldBeEmpty(
			"the map is the page — a panel announcing that it has nothing is a strip of map covered "
			+ "for no reason.");
	}
}
