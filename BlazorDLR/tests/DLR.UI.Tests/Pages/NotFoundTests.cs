using BlazorDLR.Shared.Pages;
using Bunit;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The 404 page. Small enough that "does anything render" is the property under
/// test: an empty NotFound would silently strand every mistyped URL.
/// </summary>
public sealed class NotFoundTests : BunitContext
{
	[Fact]
	public void Renders_ANotFoundMessage()
	{
		IRenderedComponent<NotFound> component = Render<NotFound>();

		component.Markup.Contains("Not Found", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
			"a 404 that renders blank is worse than one that says the wrong thing — the copy must state what happened.");
	}
}
