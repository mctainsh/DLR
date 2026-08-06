using BlazorDLR.Shared.Pages;
using BlazorDLR.Shared.Services;
using Bunit;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// §15.2's GPX import entry. The `.gpx` <c>InputFile</c> is deliberately the same
/// component on both hosts — a phone's file picker is provided by MAUI's
/// <c>FilePicker</c> when the user asks for it, but the "always available" path
/// through <c>InputFile</c> works on every host. The initial render is what this
/// test can prove without an actual file upload flow (which needs
/// <c>InputFileChangeEventArgs</c> plumbing that bUnit does not natively support).
/// </summary>
public sealed class GpxImportTests : BunitContext
{
	private FakeApiClient WireServices()
	{
		FakeApiClient api = new();
		Services.AddSingleton<IApiClient>(api);
		Services.AddSingleton(new HttpClient { BaseAddress = new Uri("http://localhost/") });
		return api;
	}

	[Fact]
	public void InputFile_IsPresent_WithGpxMimeTypeInAccept()
	{
		WireServices();

		IRenderedComponent<GpxImport> component = Render<GpxImport>();

		// <InputFile> renders as <input type="file"> in the DOM.
		AngleSharp.Dom.IElement input = component.Find("input[type='file']");
		string accept = input.GetAttribute("accept") ?? string.Empty;
		accept.Contains(".gpx", StringComparison.Ordinal).ShouldBeTrue(
			"§15.2: the picker restricts the file dialog to .gpx and matching mime types.");
		accept.Contains("application/gpx+xml", StringComparison.Ordinal).ShouldBeTrue(
			"the primary GPX mime type is included so browsers with the right hint show only GPX files.");
	}

	[Fact]
	public void Lead_CopyExplainsWhatWillBePreviewed()
	{
		WireServices();

		IRenderedComponent<GpxImport> component = Render<GpxImport>();

		component.Markup.Contains("distance, ascent and duration", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
			"§15.2: the copy tells the user they will see the summary before committing — the dryRun flow.");
	}
}
