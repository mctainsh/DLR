using BlazorDLR.Shared.Diagnostics;
using BlazorDLR.Shared.Pages.Settings;
using Bunit;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The log a rider can read without a laptop (§17.6 debugging).
/// <para>
/// The point of this page is that it needs nothing attached: no IDE for
/// <c>Debug.WriteLine</c>, no Console.app and cable for <c>NSLog</c>, no Xcode container
/// download for the file sink. So what is worth asserting is that a line written from
/// anywhere reaches the screen, and that a rider can narrow a thousand of them down to
/// the one they are looking for.
/// </para>
/// <para>
/// <see cref="DiagnosticLog"/> is static — deliberately, since it is called before there is
/// a service provider to resolve it from — so each test clears it and asserts on a token of
/// its own rather than on the buffer being empty.
/// </para>
/// </summary>
public sealed class DiagnosticsLogTests : PageTestContext
{
	[Fact]
	public void ALineWrittenBeforeThePageOpens_IsOnTheScreen()
	{
		DiagnosticLog.Clear();
		DiagnosticLog.Write("hub connected to https://example.test/hubs/ride");

		IRenderedComponent<DiagnosticsLog> component = Render<DiagnosticsLog>();

		component.Find("textarea.log").TextContent
			.Contains("hub connected", StringComparison.Ordinal).ShouldBeTrue(
			"the buffer is filled from app startup onwards, long before anybody opens this page — " +
			"a viewer that only showed lines written after it was opened would miss every one that " +
			"mattered.");
	}

	[Fact]
	public async Task TheFilter_NarrowsToMatchingLines()
	{
		DiagnosticLog.Clear();
		DiagnosticLog.Write("zzz-marker-alpha happened.");
		DiagnosticLog.Write("zzz-marker-beta happened.");

		IRenderedComponent<DiagnosticsLog> component = Render<DiagnosticsLog>();

		// Fresh find inside InvokeAsync, for the reason PollComposerTests spells out: a render
		// invalidates the event handler IDs, and this page re-renders on every DiagnosticLog write
		// — including ones from whatever else the suite is running in parallel, since the log is
		// deliberately static so it can be called before there is a container.
		await component.InvokeAsync(() => component.Find(".filter input").Input("zzz-marker-beta"));

		string shown = component.Find("textarea.log").TextContent;
		shown.Contains("zzz-marker-beta", StringComparison.Ordinal).ShouldBeTrue();
		shown.Contains("zzz-marker-alpha", StringComparison.Ordinal).ShouldBeFalse(
			"a thousand-line ring is only readable if a rider can cut it down to the thing they " +
			"came looking for.");
	}

	[Fact]
	public async Task Clearing_EmptiesTheScreenAndTheRing()
	{
		DiagnosticLog.Clear();
		DiagnosticLog.Write("zzz-marker-gamma happened earlier");

		IRenderedComponent<DiagnosticsLog> component = Render<DiagnosticsLog>();
		await component.InvokeAsync(() => component.Find(".log-actions button.danger").Click());

		component.Find("textarea.log").TextContent
			.Contains("zzz-marker-gamma", StringComparison.Ordinal).ShouldBeFalse();

		// That the marker is gone, rather than that the ring is empty. The log is deliberately
		// static — it is called before there is a container to resolve it from — so anything else
		// the suite is running in parallel writes into the same buffer, and "empty" would be a
		// test asserting that nothing else in the app was doing anything.
		DiagnosticLog.Snapshot()
			.Any(line => line.Text.Contains("zzz-marker-gamma", StringComparison.Ordinal))
			.ShouldBeFalse(
				"Clear is for starting a fresh attempt at reproducing something — a screen that " +
				"looked empty over a buffer that was not would put the next run's lines under the " +
				"last one's.");
	}
}
