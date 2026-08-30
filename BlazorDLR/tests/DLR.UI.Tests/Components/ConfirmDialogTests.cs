using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.State;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Components;

/// <summary>
/// The one confirm modal for the whole app. Tested here for the property Play's location
/// disclosure depends on: a message written as several paragraphs is rendered as several
/// paragraphs. Run into one block it is a wall of text, and unread text is not a prominent
/// disclosure (§4.3).
/// </summary>
public sealed class ConfirmDialogTests : BunitContext
{
	private ConfirmService Wire()
	{
		ConfirmService confirm = new();
		Services.AddSingleton(confirm);
		return confirm;
	}

	[Fact]
	public void EachLineOfTheMessage_BecomesItsOwnParagraph()
	{
		ConfirmService confirm = Wire();
		IRenderedComponent<ConfirmDialog> component = Render<ConfirmDialog>();

		_ = confirm.AskAsync("Title", "First thing.\nSecond thing.\nThird thing.");

		component.WaitForAssertion(() => component.FindAll("#confirm-message p").Count.ShouldBe(3));
	}

	[Fact]
	public void PlaysLocationDisclosure_RendersWholeAndWithBothAnswers()
	{
		ConfirmService confirm = Wire();
		IRenderedComponent<ConfirmDialog> component = Render<ConfirmDialog>();

		_ = confirm.AskAsync(new ConfirmRequest(
			LocationDisclosure.Title, LocationDisclosure.Message, "I agree", "No thanks"));

		component.WaitForAssertion(() =>
		{
			component.Markup.ShouldContain("collects location data");
			component.Markup.ShouldContain("even when the app is closed or not in use");
			component.Markup.ShouldContain("I agree");
			component.Markup.ShouldContain("No thanks");
		});
	}
}
