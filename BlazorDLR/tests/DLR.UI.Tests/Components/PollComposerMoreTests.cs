using BlazorDLR.Shared.Components;
using Bunit;
using DLR.Core.Contracts.Comments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Components;

/// <summary>
/// §17.5's 2–6 bound and the close-time optional gate. The composer must:
/// <list type="bullet">
///   <item>Start with two option inputs and no remove button.</item>
///   <item>Stop offering "Add option" once six options exist - the seventh would make
///     it a survey, and the design outline draws the line there.</item>
///   <item>Emit a null <see cref="PollSpec.ClosesUtc"/> when the "close automatically"
///     switch is off, and a UTC-normalised value when the switch is on.</item>
/// </list>
/// </summary>
public sealed class PollComposerMoreTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);

	public PollComposerMoreTests()
	{
		Services.AddSingleton<TimeProvider>(new FakeTimeProvider(FixedInstant));
	}

	[Fact]
	public void FreshComposer_HasTwoOptionsAndNoRemoveButton()
	{
		IRenderedComponent<PollComposer> component = Render<PollComposer>();

		component.FindAll("input[placeholder^='Option']").Count.ShouldBe(2,
			"§17.5: the minimum poll is two options - the composer opens on that floor.");
		component.FindAll("button.remove").Count.ShouldBe(0,
			"§17.5: with two options nothing can be removed - the button must not be there to tempt.");
	}

	[Fact]
	public async Task AddOptionButton_DisappearsAtTheSixOptionCap()
	{
		IRenderedComponent<PollComposer> component = Render<PollComposer>();

		// Start at 2, click Add four times to reach 6.
		for (int i = 0; i < 4; i++)
		{
			await component.InvokeAsync(() =>
			{
				AngleSharp.Dom.IElement add = component.FindAll("button.add").Single();
				add.Click();
			});
		}

		component.FindAll("input[placeholder^='Option']").Count.ShouldBe(6);
		component.FindAll("button.add").Count.ShouldBe(0,
			"§17.5: the composer stops offering Add once six options exist - the seventh would make it a survey.");
	}

	[Fact]
	public async Task RemoveOption_LeavesAtLeastTwo()
	{
		IRenderedComponent<PollComposer> component = Render<PollComposer>();

		// Get to three options.
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement add = component.FindAll("button.add").Single();
			add.Click();
		});

		// One remove button per option once we're above the minimum.
		component.FindAll("button.remove").Count.ShouldBe(3);

		// Remove the second one.
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement[] removes = component.FindAll("button.remove").ToArray();
			removes[1].Click();
		});

		component.FindAll("input[placeholder^='Option']").Count.ShouldBe(2);
		component.FindAll("button.remove").Count.ShouldBe(0,
			"§17.5: at the two-option floor the remove buttons must vanish - the composer must not offer a removal that would break the minimum.");
	}

	[Fact]
	public async Task BuildSpec_WithCloseTimeSwitch_EmitsUtcClosesValue()
	{
		IRenderedComponent<PollComposer> component = Render<PollComposer>();

		// Fill two options.
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement[] inputs = component.FindAll("input[placeholder^='Option']").ToArray();
			inputs[0].Input("A");
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement[] inputs = component.FindAll("input[placeholder^='Option']").ToArray();
			inputs[1].Input("B");
		});

		// Turn on the close-time switch.
		await component.InvokeAsync(() =>
		{
			// The second checkbox on the composer is "Close automatically at".
			// (First is AllowMultiple.)
			AngleSharp.Dom.IElement[] checkboxes = component.FindAll("input[type=checkbox]").ToArray();
			checkboxes[1].Change(true);
		});

		PollSpec? spec = component.Instance.BuildSpec();
		spec.ShouldNotBeNull();
		spec!.ClosesUtc.ShouldNotBeNull("§17.5: with the switch on the spec must carry a ClosesUtc.");
		spec.ClosesUtc!.Value.Offset.ShouldBe(TimeSpan.Zero,
			"ClosesUtc must be UTC - the server does not want to guess a client's offset.");
	}

	[Fact]
	public async Task AllowMultipleSwitch_FlipsTheSpecFlag()
	{
		IRenderedComponent<PollComposer> component = Render<PollComposer>();

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement[] inputs = component.FindAll("input[placeholder^='Option']").ToArray();
			inputs[0].Input("A");
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement[] inputs = component.FindAll("input[placeholder^='Option']").ToArray();
			inputs[1].Input("B");
		});

		component.Instance.BuildSpec()!.AllowMultiple.ShouldBeFalse("multi-select defaults off.");

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement[] checkboxes = component.FindAll("input[type=checkbox]").ToArray();
			checkboxes[0].Change(true);
		});

		component.Instance.BuildSpec()!.AllowMultiple.ShouldBeTrue(
			"the switch toggles the spec - a poll that needs multi-select depends on this round-trip.");
	}
}
