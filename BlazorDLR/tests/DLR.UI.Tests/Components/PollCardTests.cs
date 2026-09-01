using BlazorDLR.Shared.Components;
using Bunit;
using DLR.Core.Contracts.Comments;
using Microsoft.AspNetCore.Components;

namespace DLR.UI.Tests.Components;

/// <summary>
/// A single-select PollCard: tapping the current choice clears it; tapping another
/// option replaces it. Votes are always attributed (§17.5) - voter names appear
/// inline with the tallies.
/// </summary>
public sealed class PollCardTests : BunitContext
{
	private static PollResults SampleResults(bool allowMultiple, Guid? myOption = null)
	{
		Guid opt1 = new("11111111-1111-1111-1111-111111111111");
		Guid opt2 = new("22222222-2222-2222-2222-222222222222");
		return new PollResults(
			CommentId: Guid.NewGuid(),
			Question: "Coast or mountain?",
			AllowMultiple: allowMultiple,
			ClosesUtc: null,
			ClosedUtc: null,
			IsClosed: false,
			Options: new[]
			{
				new PollOptionResult(opt1, 0, "Coast road", 3, new[]
				{
					new PollVoter(Guid.NewGuid(), "Alice"),
					new PollVoter(Guid.NewGuid(), "Bob"),
					new PollVoter(Guid.NewGuid(), "Cass"),
				}),
				new PollOptionResult(opt2, 1, "Mountain road", 1, new[]
				{
					new PollVoter(Guid.NewGuid(), "Dave"),
				}),
			},
			MyOptionIds: myOption is null ? Array.Empty<Guid>() : new[] { myOption.Value });
	}

	[Fact]
	public void RendersEveryOptionWithTallyAndVoters()
	{
		IRenderedComponent<PollCard> component = Render<PollCard>(parameters => parameters
			.Add(p => p.Results, SampleResults(allowMultiple: false))
			.Add(p => p.CanClose, false)
			.Add(p => p.OnCast, EventCallback.Factory.Create<IReadOnlyList<Guid>>(this, _ => { }))
			.Add(p => p.OnClose, EventCallback.Factory.Create(this, () => { })));

		string markup = component.Markup;
		markup.Contains("Coast road", StringComparison.Ordinal).ShouldBeTrue();
		markup.Contains("Mountain road", StringComparison.Ordinal).ShouldBeTrue();
		// One element per voter rather than one joined string, so each name can carry the rider's
		// photograph beside it (§7.3). What §17.5 asks for is that the votes are attributed and in
		// order, which is what this reads - the separator between them is now the stylesheet's job.
		component.FindAll(".voters .voter").Select(voter => voter.TextContent.Trim())
			.ShouldBe(["Alice", "Bob", "Cass", "Dave"],
				"§17.5: votes are attributed and voter names are on the card.");
	}

	[Fact]
	public void MyOption_IsMarkedWithCheckmark()
	{
		Guid mine = new("11111111-1111-1111-1111-111111111111");
		IRenderedComponent<PollCard> component = Render<PollCard>(parameters => parameters
			.Add(p => p.Results, SampleResults(allowMultiple: false, myOption: mine))
			.Add(p => p.CanClose, false)
			.Add(p => p.OnCast, EventCallback.Factory.Create<IReadOnlyList<Guid>>(this, _ => { }))
			.Add(p => p.OnClose, EventCallback.Factory.Create(this, () => { })));

		component.Markup.Contains("your vote", StringComparison.Ordinal).ShouldBeTrue(
			"the voter's own choice is called out on the card.");
	}

	[Fact]
	public async Task TappingMyCurrentSingleSelectChoice_ClearsIt()
	{
		Guid mine = new("11111111-1111-1111-1111-111111111111");
		List<Guid> lastCast = new() { mine };

		IRenderedComponent<PollCard> component = Render<PollCard>(parameters => parameters
			.Add(p => p.Results, SampleResults(allowMultiple: false, myOption: mine))
			.Add(p => p.CanClose, false)
			.Add(p => p.OnCast, EventCallback.Factory.Create<IReadOnlyList<Guid>>(this, ids =>
			{
				lastCast = ids.ToList();
			}))
			.Add(p => p.OnClose, EventCallback.Factory.Create(this, () => { })));

		// Tap the button belonging to the "mine" option.
		AngleSharp.Dom.IElement myButton = component
			.FindAll("button.vote-target")
			.First(b => b.TextContent.Contains("Coast road"));
		await component.InvokeAsync(() => myButton.ClickAsync(new()));

		lastCast.ShouldBeEmpty(
			"§17.5 single-select rule: tapping the current choice clears the vote (empty list on the wire).");
	}
}
