using BlazorDLR.Shared.Components;
using Bunit;
using DLR.Core.Contracts.Comments;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Components;

/// <summary>
/// §17.5 says a poll has 2–6 options. The composer's <c>BuildSpec</c> is what the
/// parent thread reads before it lets the user post — an under-filled composer must
/// return null so the "Post" button stays disabled.
/// </summary>
public sealed class PollComposerTests : BunitContext
{
	public PollComposerTests()
	{
		// TimeProvider is injected by the composer for its default close time.
		Services.AddSingleton(TimeProvider.System);
	}

	[Fact]
	public void BuildSpec_WithFewerThanTwoOptions_ReturnsNull()
	{
		IRenderedComponent<PollComposer> component = Render<PollComposer>();

		// Default composer has two empty option fields; both are trimmed to empty and
		// discarded, so BuildSpec sees zero real options and returns null.
		component.Instance.BuildSpec().ShouldBeNull(
			"a poll needs two or more real options — an empty composer is not a poll yet.");
	}

	[Fact]
	public async Task BuildSpec_WithTwoOptions_ReturnsSpec()
	{
		IRenderedComponent<PollComposer> component = Render<PollComposer>();

		// Each Change() re-renders and invalidates the event handler IDs, so bUnit v2
		// insists on a fresh FindAll per interaction wrapped in InvokeAsync.
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement[] inputs = component.FindAll("input[placeholder^='Option']").ToArray();
			inputs.Length.ShouldBeGreaterThanOrEqualTo(2);
			inputs[0].Change("Coast road");
		});
		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement[] inputs = component.FindAll("input[placeholder^='Option']").ToArray();
			inputs[1].Change("Mountain road");
		});

		PollSpec? spec = component.Instance.BuildSpec();
		spec.ShouldNotBeNull();
		spec!.Options.Count.ShouldBe(2);
		spec.Options[0].ShouldBe("Coast road");
		spec.Options[1].ShouldBe("Mountain road");
		spec.AllowMultiple.ShouldBeFalse("multi-select defaults off unless the user turns it on.");
	}
}
