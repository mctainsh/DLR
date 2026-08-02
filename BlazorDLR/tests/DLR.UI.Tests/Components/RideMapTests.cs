using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Services;
using Bunit;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Components;

/// <summary>
/// §4.5's component-scope map rules — the ones that live in <c>RideMap.razor</c>
/// rather than in a base-map JS module.
/// <para>
/// Two properties this file asserts:
/// <list type="bullet">
///   <item>The <em>stated-error</em> branch fires when <see cref="IMapInterop.InitAsync"/>
///     throws. This is the branch §4.5 names: "a map that cannot get a token shows a
///     stated error, not a blank grey rectangle". The MapKit-token-unavailable case is
///     shape-compatible with the Google/OpenLayers key-missing cases — they all reach
///     the same catch in <c>RideMap</c>.</item>
///   <item><em>Provider-neutral registration.</em> A component that reads
///     <see cref="IMapInterop"/> without asserting on <see cref="MapProvider"/> proves
///     the same C# renders against any of the three providers; per §4.5 v0.21 that is
///     a load-bearing invariant of the design.</item>
/// </list>
/// </para>
/// <para>
/// The happy-path render is <em>not</em> covered here because <c>RideMap</c> hosts
/// <c>SkiaMapOverlay</c> once a viewport arrives, and <c>SKCanvasView</c> reaches for
/// <c>System.Runtime.InteropServices.JavaScript</c> on <c>OnAfterRenderAsync</c> — a
/// browser-only API. That is a live-JS smoke, not a bUnit test, and it lands in the
/// <c>/map-spike</c> page walk-through in <c>SharedFrontend.md §7 Phase 0</c>.
/// Provider attribution is likewise a live-JS concern owned by each module.
/// </para>
/// </summary>
public sealed class RideMapTests : BunitContext
{
	private static readonly MapCamera SampleCamera = new(-33.868, 151.209, 12);

	[Fact]
	public void MapKitTokenUnavailable_ShowsStatedError_NotBlankMap()
	{
		FakeMapInterop map = new()
		{
			Provider = MapProvider.AppleMaps,
			// MapKit JS surfaces a missing token as an initialisation throw; the fake
			// reproduces that shape. RideMap does not care which provider — only that
			// InitAsync threw. The Google API-key-absent branch and the OpenLayers
			// tile-server-unreachable branch reach the same catch.
			InitException = new InvalidOperationException("MapKit JS refused: no token from /api/v1/maps/token."),
		};
		Services.AddSingleton<IMapInterop>(map);

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, SampleCamera));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Map unavailable", StringComparison.Ordinal).ShouldBeTrue(
				"§4.5: a map that cannot initialise shows a stated error, not a blank grey rectangle.");
			component.Markup.Contains("MapKit JS refused", StringComparison.Ordinal).ShouldBeTrue(
				"the exception message carries the reason — someone debugging the token endpoint should see it in the DOM.");
			component.FindAll("div.dlr-map-base").Count.ShouldBe(1,
				"the base-map host <div> is still emitted — the JS module attaches here on retry, so removing it would strand a recoverable failure.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Theory]
	[InlineData(MapProvider.AppleMaps)]
	[InlineData(MapProvider.GoogleMaps)]
	[InlineData(MapProvider.OpenLayersOsm)]
	public void SameComponent_InitialisesEveryProvider_BeforeReachingTheOverlay(MapProvider provider)
	{
		FakeMapInterop map = new()
		{
			Provider = provider,
			// Force the stated-error branch so bUnit never renders SkiaMapOverlay — Skia's
			// SKCanvasView reaches for System.Runtime.InteropServices.JavaScript, which is
			// a browser-only API. The failure-path render still proves the shared
			// component composes against every provider: the same C# reaches InitAsync
			// regardless of which module answered.
			InitException = new InvalidOperationException($"{provider} module unreachable in this test host."),
		};
		Services.AddSingleton<IMapInterop>(map);

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, SampleCamera));

		component.WaitForAssertion(() =>
		{
			map.InitCount.ShouldBe(1,
				"§4.5 v0.21: one shared component initialises whichever provider is registered — no per-provider Razor.");
			component.Markup.Contains($"{provider} module unreachable", StringComparison.Ordinal).ShouldBeTrue(
				"the error carries the provider name — a stated error must state which one failed.");
		}, timeout: TimeSpan.FromSeconds(3));
	}
}
