using BlazorDLR.Shared.Pages;
using BlazorDLR.Shared.Services;
using Bunit;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// §4.1's signed-in landing. Two things this file asserts:
/// <list type="bullet">
///   <item>The four navigation cards (My rides / Group rides / Import / Settings) are
///     all present — this is the sole path a signed-in user has into everything else.</item>
///   <item>The form factor label is what <see cref="IFormFactor"/> returned. A missing
///     label means the form-factor seam is broken; the shared UI relies on it for
///     web-only branches like TrackEditor.</item>
/// </list>
/// </summary>
public sealed class HomeTests : PageTestContext
{
	private FakeFormFactor WireServices(string formFactor = "Desktop", string platform = "Web")
	{
		FakeFormFactor ff = new() { FormFactor = formFactor, Platform = platform };
		Services.AddSingleton<IFormFactor>(ff);
		return ff;
	}

	[Fact]
	public void AllFourNavCards_ArePresent()
	{
		WireServices();

		IRenderedComponent<Home> component = Render<Home>();

		component.FindAll("a[href='/rides']").ShouldNotBeEmpty("§4.1: My adventures is one of the four Home cards.");
		component.FindAll("a[href='/group-rides']").ShouldNotBeEmpty("Group adventures is a Home card.");
		component.FindAll("a[href='/import']").ShouldNotBeEmpty("Import GPX is a Home card.");
		component.FindAll("a[href='/settings']").ShouldNotBeEmpty("Settings is a Home card.");
	}

	[Fact]
	public void FormFactorLabel_RendersWhatIFormFactorReturned()
	{
		WireServices(formFactor: "Phone", platform: "Android");

		IRenderedComponent<Home> component = Render<Home>();

		component.Markup.Contains("Phone", StringComparison.Ordinal).ShouldBeTrue(
			"the form factor comes from IFormFactor — the shared UI shows what the host answered.");
		component.Markup.Contains("Android", StringComparison.Ordinal).ShouldBeTrue(
			"the platform label helps a user file a bug that names the host correctly.");
	}
}
