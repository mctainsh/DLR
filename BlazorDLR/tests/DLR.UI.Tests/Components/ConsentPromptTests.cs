using BlazorDLR.Shared.Components;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace DLR.UI.Tests.Components;

/// <summary>
/// §5.6's join-time consent prompt. Two properties matter and both are structural:
/// the wind-down clause is in the copy — because through v0.14 it said flatly "it
/// stops when the ride ends" which the wind-down made a lie — and Share vs. "Not
/// now" are two callbacks the parent maps distinctly, since a swipe-away is not
/// consent.
/// </summary>
public sealed class ConsentPromptTests : BunitContext
{
	[Fact]
	public void Copy_MentionsTheWindDown()
	{
		IRenderedComponent<ConsentPrompt> component = Render<ConsentPrompt>(parameters => parameters
			.Add(p => p.RideName, "Saturday club run"));

		string markup = component.Markup;
		markup.Contains("two hours", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
			"§5.6: the copy must name the wind-down window so the consent is not overstated.");
		markup.Contains("Saturday club run", StringComparison.Ordinal).ShouldBeTrue(
			"the adventure name is what the user is agreeing to share with, so it belongs on the card.");
	}

	[Fact]
	public async Task ShareButton_InvokesOnShare_NotOnDismiss()
	{
		bool shared = false;
		bool dismissed = false;

		IRenderedComponent<ConsentPrompt> component = Render<ConsentPrompt>(parameters => parameters
			.Add(p => p.RideName, "Test adventure")
			.Add(p => p.OnShare, EventCallback.Factory.Create(this, () => shared = true))
			.Add(p => p.OnDismiss, EventCallback.Factory.Create(this, () => dismissed = true)));

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement shareButton = component
				.FindAll("button")
				.First(b => b.TextContent.Contains("Share", StringComparison.Ordinal));
			shareButton.Click();
		});

		shared.ShouldBeTrue();
		dismissed.ShouldBeFalse("Share and 'Not now' are two distinct callbacks — one must not fall through to the other.");
	}

	[Fact]
	public async Task NotNowButton_InvokesOnDismiss_NotOnShare()
	{
		bool shared = false;
		bool dismissed = false;

		IRenderedComponent<ConsentPrompt> component = Render<ConsentPrompt>(parameters => parameters
			.Add(p => p.RideName, "Test adventure")
			.Add(p => p.OnShare, EventCallback.Factory.Create(this, () => shared = true))
			.Add(p => p.OnDismiss, EventCallback.Factory.Create(this, () => dismissed = true)));

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement notNow = component
				.FindAll("button")
				.First(b => b.TextContent.Contains("Not now", StringComparison.Ordinal));
			notNow.Click();
		});

		dismissed.ShouldBeTrue();
		shared.ShouldBeFalse("§5.6: dismissing is not consent, so OnShare must not be raised on 'Not now'.");
	}
}
