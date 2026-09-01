using BlazorDLR.Shared.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace DLR.UI.Tests.Fakes;

/// <summary>
/// <c>RideMap</c> with its markup suppressed, for pages that host a map but are being tested
/// for something else.
/// <para>
/// <strong>Why a subclass rather than a substitute.</strong> The real component's logic is the
/// part under test - it is what turns an <c>IMapInterop</c> viewport into
/// <c>OnViewportReported</c> and a provider tap into <c>OnMapClicked</c>. What cannot run is
/// its <em>rendering</em>: <c>SkiaMapOverlay</c>'s <c>SKCanvasView</c> throws
/// <c>PlatformNotSupportedException</c> outside a browser, so any test that lets a viewport
/// through would fail on the render rather than on the assertion. Overriding
/// <see cref="BuildRenderTree"/> to draw nothing keeps every code path and drops only the
/// pixels.
/// </para>
/// <para>
/// A subclass is also what keeps <c>@ref="_map"</c> working - the page's field is typed
/// <c>RideMap</c>, and a substitute that was not one would fail the ref assignment with a cast
/// error before any assertion ran.
/// </para>
/// </summary>
public sealed class StubRideMap : RideMap
{
	/// <summary>Draws nothing. Everything else about the component is the real thing.</summary>
	/// <param name="builder">Unused.</param>
	protected override void BuildRenderTree(RenderTreeBuilder builder)
	{
		// Intentionally empty - see the class remarks.
	}
}
