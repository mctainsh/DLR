using BlazorDLR.Shared.Components;
using Bunit;
using DLR.Core.Contracts.Comments;
using Microsoft.AspNetCore.Components;

namespace DLR.UI.Tests.Components;

/// <summary>
/// The multi-select and close-poll paths of §17.5. Two rules that need testing:
/// <list type="bullet">
///   <item>Multi-select tap on an option not yet chosen adds it; a tap on one already
///     chosen removes it. The full new set travels on the wire.</item>
///   <item>The "Close poll now" button is only rendered when the caller is the author
///     or the ride organiser (<c>CanClose</c> == true) AND the poll is not already
///     closed. Both branches would leak a control that would 403 on the server.</item>
///   <item>A closed poll disables every vote target - the tally is history, not a
///     ballot.</item>
/// </list>
/// </summary>
public sealed class PollCardMoreTests : BunitContext
{
	private static readonly Guid Opt1 = new("11111111-1111-1111-1111-111111111111");
	private static readonly Guid Opt2 = new("22222222-2222-2222-2222-222222222222");
	private static readonly Guid Opt3 = new("33333333-3333-3333-3333-333333333333");
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static PollResults MultiSelectResults(IReadOnlyList<Guid>? mine = null, bool closed = false) =>
		new(
			CommentId: Guid.NewGuid(),
			Question: "Which routes suit?",
			AllowMultiple: true,
			ClosesUtc: null,
			ClosedUtc: closed ? FixedInstant : null,
			IsClosed: closed,
			Options: new[]
			{
				new PollOptionResult(Opt1, 0, "Coast", 1, new[] { new PollVoter(Guid.NewGuid(), "Alice") }),
				new PollOptionResult(Opt2, 1, "Mountain", 0, Array.Empty<PollVoter>()),
				new PollOptionResult(Opt3, 2, "Forest", 0, Array.Empty<PollVoter>()),
			},
			MyOptionIds: mine ?? Array.Empty<Guid>());

	[Fact]
	public async Task MultiSelect_AddsUnchosenOption_ToTheExistingSet()
	{
		List<Guid>? sent = null;
		IRenderedComponent<PollCard> component = Render<PollCard>(parameters => parameters
			.Add(p => p.Results, MultiSelectResults(mine: new[] { Opt1 }))
			.Add(p => p.CanClose, false)
			.Add(p => p.OnCast, EventCallback.Factory.Create<IReadOnlyList<Guid>>(this, ids => sent = ids.ToList()))
			.Add(p => p.OnClose, EventCallback.Factory.Create(this, () => { })));

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement target = component.FindAll("button.vote-target")
				.First(b => b.TextContent.Contains("Mountain", StringComparison.Ordinal));
			target.Click();
		});

		sent.ShouldNotBeNull();
		sent!.Count.ShouldBe(2, "§17.5 multi-select: an unchosen tap adds - the full new set of chosen ids reaches OnCast.");
		sent.ShouldContain(Opt1);
		sent.ShouldContain(Opt2);
	}

	[Fact]
	public async Task MultiSelect_RemovesAlreadyChosen_LeavingTheRest()
	{
		List<Guid>? sent = null;
		IRenderedComponent<PollCard> component = Render<PollCard>(parameters => parameters
			.Add(p => p.Results, MultiSelectResults(mine: new[] { Opt1, Opt2 }))
			.Add(p => p.CanClose, false)
			.Add(p => p.OnCast, EventCallback.Factory.Create<IReadOnlyList<Guid>>(this, ids => sent = ids.ToList()))
			.Add(p => p.OnClose, EventCallback.Factory.Create(this, () => { })));

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement target = component.FindAll("button.vote-target")
				.First(b => b.TextContent.Contains("Coast", StringComparison.Ordinal));
			target.Click();
		});

		sent.ShouldNotBeNull();
		sent!.ShouldNotContain(Opt1, "§17.5 multi-select: a tap on an already-chosen option removes it.");
		sent!.ShouldContain(Opt2, "the untouched option stays selected - the toggle is per option, not a wipe.");
	}

	[Fact]
	public void CloseButton_HiddenWhenCallerCannotClose()
	{
		IRenderedComponent<PollCard> component = Render<PollCard>(parameters => parameters
			.Add(p => p.Results, MultiSelectResults())
			.Add(p => p.CanClose, false)
			.Add(p => p.OnCast, EventCallback.Factory.Create<IReadOnlyList<Guid>>(this, _ => { }))
			.Add(p => p.OnClose, EventCallback.Factory.Create(this, () => { })));

		component.FindAll("button.close").Count.ShouldBe(0,
			"§17.5: only the author or organiser sees the close button - everyone else must not.");
	}

	[Fact]
	public void CloseButton_RenderedForAuthor_WhenPollStillOpen()
	{
		IRenderedComponent<PollCard> component = Render<PollCard>(parameters => parameters
			.Add(p => p.Results, MultiSelectResults())
			.Add(p => p.CanClose, true)
			.Add(p => p.OnCast, EventCallback.Factory.Create<IReadOnlyList<Guid>>(this, _ => { }))
			.Add(p => p.OnClose, EventCallback.Factory.Create(this, () => { })));

		component.FindAll("button.close").Count.ShouldBe(1,
			"§17.5: an open poll authored by the caller renders the close button.");
	}

	[Fact]
	public void CloseButton_Hidden_WhenPollAlreadyClosed_EvenForAuthor()
	{
		IRenderedComponent<PollCard> component = Render<PollCard>(parameters => parameters
			.Add(p => p.Results, MultiSelectResults(closed: true))
			.Add(p => p.CanClose, true)
			.Add(p => p.OnCast, EventCallback.Factory.Create<IReadOnlyList<Guid>>(this, _ => { }))
			.Add(p => p.OnClose, EventCallback.Factory.Create(this, () => { })));

		component.FindAll("button.close").Count.ShouldBe(0,
			"§17.5: closing an already-closed poll is a no-op - the button must not offer it.");
	}

	[Fact]
	public void ClosedPoll_DisablesEveryVoteTarget()
	{
		IRenderedComponent<PollCard> component = Render<PollCard>(parameters => parameters
			.Add(p => p.Results, MultiSelectResults(closed: true))
			.Add(p => p.CanClose, false)
			.Add(p => p.OnCast, EventCallback.Factory.Create<IReadOnlyList<Guid>>(this, _ => { }))
			.Add(p => p.OnClose, EventCallback.Factory.Create(this, () => { })));

		component.FindAll("button.vote-target").ShouldAllBe(b => b.HasAttribute("disabled"),
			"§17.5: a closed poll is history. The tally must still render (results are always visible), but the ballot is disabled.");
	}
}
