using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Tracks;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Components;

/// <summary>
/// §4.5's component-scope map rules - the ones that live in <c>RideMap.razor</c>
/// rather than in the base-map JS module.
/// <para>
/// Two properties this file asserts:
/// <list type="bullet">
///   <item>The <em>stated-error</em> branch fires when <see cref="IMapInterop.InitAsync"/>
///     throws. This is the branch §4.5 names: "a map that cannot get a token shows a
///     stated error, not a blank grey rectangle". v0.24 removed the token, and the rule
///     outlived it - an unreachable CDN or tile server reaches the same catch.</item>
///   <item><em>Provider-neutral registration.</em> A component that reads
///     <see cref="IMapInterop"/> without asserting on <see cref="MapProvider"/> is what
///     let v0.24 delete three providers and add one without touching a single screen.
///     The seam is worth asserting precisely because there is only one implementation
///     behind it today - that is when an accidental dependency on it would slip in.</item>
/// </list>
/// </para>
/// <para>
/// The happy-path render is <em>not</em> covered here because <c>RideMap</c> hosts
/// <c>SkiaMapOverlay</c> once a viewport arrives, and <c>SKCanvasView</c> reaches for
/// <c>System.Runtime.InteropServices.JavaScript</c> on <c>OnAfterRenderAsync</c> - a
/// browser-only API. That is a live-JS smoke, not a bUnit test, and it lands in the
/// <c>/map-spike</c> page walk-through in <c>SharedFrontend.md §7 Phase 0</c>.
/// Provider attribution is likewise a live-JS concern owned by each module.
/// </para>
/// </summary>
public sealed class RideMapTests : BunitContext
{
	private static readonly MapCamera SampleCamera = new(-33.868, 151.209, 12);

	/// <summary>
	/// A map given a box frames itself on it, and does not do it again until the box moves.
	/// <para>
	/// The second half is the half worth having. Framing is a <em>correction</em> applied after
	/// the base map attaches, so it runs off <c>OnParametersSetAsync</c> - which fires for every
	/// re-render of the page hosting the map, and the track detail re-renders for a rating
	/// arriving, a status line and a panel opening. Without the applied-box guard, a rider who had
	/// panned along their route to look at a junction would be snapped back to the whole line by
	/// the next unrelated render.
	/// </para>
	/// </summary>
	[Fact]
	public void Bounds_FrameTheMapOnce_AndAgainOnlyWhenTheBoxMoves()
	{
		FakeMapInterop map = new();
		Services.AddSingleton<IMapInterop>(map);
		Services.AddRideMapServices();

		TrackBounds box = new(-33.90, 151.10, -33.80, 151.30);

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, SampleCamera)
			.Add(p => p.Bounds, box));

		component.WaitForAssertion(
			() => map.Fits.ShouldBe([box],
				"the box is set before the base map exists, so the fit has to be sent once it attaches."),
			timeout: TimeSpan.FromSeconds(3));

		// A re-render for something else entirely. The host page does this constantly.
		component.Render(parameters => parameters.Add(p => p.ShowUserLocation, true));

		map.Fits.ShouldBe([box],
			"a map already framed on this box must not re-frame - a rider who has panned away would be dragged back.");

		TrackBounds moved = new(-34.00, 151.00, -33.70, 151.40);
		component.Render(parameters => parameters.Add(p => p.Bounds, moved));

		component.WaitForAssertion(
			() => map.Fits.ShouldBe([box, moved], "a different box is a different route to look at."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void BaseMapUnavailable_ShowsStatedError_NotBlankMap()
	{
		FakeMapInterop map = new()
		{
			// The shape map.maplibre.js throws in when the CDN cannot be reached. RideMap
			// does not care what failed - only that InitAsync threw - so the tile-server
			// and style-load branches reach this same catch.
			InitException = new InvalidOperationException(
				"Could not load MapLibre GL JS from https://cdn.jsdelivr.net/npm/maplibre-gl@4.7.1/dist/maplibre-gl.js."),
		};
		Services.AddSingleton<IMapInterop>(map);
		Services.AddRideMapServices();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, SampleCamera));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Map unavailable", StringComparison.Ordinal).ShouldBeTrue(
				"§4.5: a map that cannot initialise shows a stated error, not a blank grey rectangle.");
			component.Markup.Contains("Could not load MapLibre GL JS", StringComparison.Ordinal).ShouldBeTrue(
				"the exception message carries the reason - someone debugging a blocked CDN should see it in the DOM.");
			component.FindAll("div.dlr-map-base").Count.ShouldBe(1,
				"the base-map host <div> is still emitted - the JS module attaches here on retry, so removing it would strand a recoverable failure.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// The overlay rasterises a frame on a runtime that is not WebAssembly.
	/// <para>
	/// This is the regression test for the bug that made every map page freeze the phone apps.
	/// The overlay was an <c>SKCanvasView</c> from <c>SkiaSharp.Views.Blazor</c>, which
	/// initialises through <c>[JSImport]</c> - WebAssembly-only interop. On a MAUI
	/// <c>BlazorWebView</c>, where the runtime is Mono, its first render threw
	/// <em>"System.Runtime.InteropServices.JavaScript is not supported on this platform"</em>,
	/// and an unhandled throw in <c>OnAfterRenderAsync</c> does not merely lose the pins: it
	/// takes down the Blazor renderer. The symptom on device was a base map that still panned -
	/// it is pure JS in the WebView - while every button in the app stopped responding.
	/// </para>
	/// <para>
	/// <strong>bUnit runs on the desktop CLR, which is the same not-wasm situation as the
	/// phone</strong>, so this reproduces the device condition without a device. It failed
	/// before v0.24's fix and passes after it. Nothing else in the suite covers this: every
	/// other map test forces <c>InitException</c>, which is exactly why the overlay reached
	/// production having never once been mounted off the web.
	/// </para>
	/// </summary>
	[Fact]
	public void Overlay_RasterisesAFrame_OnARuntimeThatIsNotWebAssembly()
	{
		// Strict-mode JS interop, so this also asserts the overlay talks to the module it is
		// supposed to: an unplanned invocation fails the test rather than passing silently.
		List<object?[]> presented = [];
		BunitJSModuleInterop module = JSInterop.SetupModule("./_content/BlazorDLR.Shared/map/overlay.js");
		module.SetupVoid("present", _ => true).SetVoidResult();

		Services.AddSingleton<IMapInterop>(new FakeMapInterop());
		Services.AddRideMapServices();

		// The overlay's own dependencies. A missing service throws during component
		// instantiation, which an ErrorBoundary does not catch - a different failure from the
		// one under test, and one that would mask it.
		Services.AddSingleton<IDeviceSettings, InMemoryDeviceSettings>();
		Services.AddSingleton<RouteStyleState>();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, SampleCamera)
			// A circle rather than a marker: authored markers reach for their icon bitmap
			// through JS, and this test is about the rasterise, not the icon cache. DrawCircles
			// needs nothing but Skia, which is exactly the layer that could not run off wasm.
			.Add(p => p.Circles, new List<MapCircle>
			{
				new(-33.868, 151.209, RadiusM: 250),
			}));

		component.WaitForAssertion(() =>
		{
			// The overlay mounted and stayed mounted. Under the old binding the ErrorBoundary
			// in RideMap caught the throw and swapped this element for a stated error.
			component.FindAll("canvas.dlr-map-overlay").Count.ShouldBe(1,
				"the overlay renders on a non-wasm runtime - this is the whole of the phone fix.");
			component.Markup.Contains("Map markers unavailable", StringComparison.Ordinal).ShouldBeFalse(
				"neither the boundary nor the overlay's own stated-error branch should fire here.");

			presented = JSInterop.Invocations["present"].Select(i => i.Arguments.ToArray()).ToList();
			presented.ShouldNotBeEmpty("a viewport arrived, so a frame should have been rasterised and presented.");
		}, timeout: TimeSpan.FromSeconds(5));

		// And it is a real frame, not an empty one - Skia rasterised and PNG-encoded off-screen
		// on this runtime, which is the step SkiaSharp.Views.Blazor could not do here at all.
		object?[] last = presented[^1];

		string? png = last[1] as string;
		png.ShouldNotBeNullOrWhiteSpace("present() is handed a base64 PNG; empty means the rasterise produced nothing.");
		Convert.FromBase64String(png!).Length.ShouldBeGreaterThan(0, "the frame must decode as PNG bytes.");

		// The frame is sized in the same device pixels the projection draws in. A mismatch here
		// is the "track drawn in the wrong place and spilling past the edge of the map" class of
		// bug: the canvas backing store stops agreeing with the coordinates the pins were
		// plotted at, and every position is off by the ratio.
		MapViewport viewport = new FakeMapInterop().InitialViewport;
		// One options object, the shape map.maplibre.js's createMap already established for this
		// folder. Size, because a backing store that disagrees with the pixels the pins were
		// plotted into is the "track in the wrong place, spilling past the edge of the map" bug.
		// Centre, zoom and bearing, because overlay.js measures every later transform against
		// them - without those the overlay can only sit still and wait for the next round trip,
		// which is exactly the pan lag they remove.
		object view = last[2].ShouldNotBeNull("present() is handed the view the frame was drawn for.");

		ViewValue(view, "widthPx").ShouldBe(viewport.CanvasWidthPx);
		ViewValue(view, "heightPx").ShouldBe(viewport.CanvasHeightPx);
		ViewValue(view, "centreLat").ShouldBe(viewport.CentreLatitude);
		ViewValue(view, "centreLon").ShouldBe(viewport.CentreLongitude);
		ViewValue(view, "zoom").ShouldBe(viewport.ZoomLevel);
		ViewValue(view, "bearingDeg").ShouldBe(viewport.HeadingDeg);
	}

	/// <summary>
	/// Rotation is on by default and off on the two screens where a tap places something.
	/// <para>
	/// The picking screens - the private-area picker (§10.1) and the marker composer (§16.1) -
	/// use the map as a coordinate entry field. A rider who has turned it off north is aiming at
	/// a north-up mental image that is no longer on screen, and the point lands somewhere they
	/// did not mean. Asserted at the seam rather than through the pages, because the pages state
	/// it in one attribute each and this is the contract that attribute rides on.
	/// </para>
	/// </summary>
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void AllowRotation_ReachesTheBaseMap(bool allowed)
	{
		FakeMapInterop map = new() { InitException = new InvalidOperationException("no base map in this test host.") };
		Services.AddSingleton<IMapInterop>(map);
		Services.AddRideMapServices();

		Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, SampleCamera)
			.Add(p => p.AllowRotation, allowed));

		map.LastOptions.ShouldNotBeNull("the base map is initialised with the options the component built.");
		map.LastOptions!.AllowRotation.ShouldBe(allowed,
			"the base map decides what a gesture may do, so the choice has to survive the seam.");
	}

	/// <summary>Left alone, a map rotates - only the picking screens opt out.</summary>
	[Fact]
	public void AllowRotation_DefaultsToOn()
	{
		FakeMapInterop map = new() { InitException = new InvalidOperationException("no base map in this test host.") };
		Services.AddSingleton<IMapInterop>(map);
		Services.AddRideMapServices();

		Render<RideMap>(parameters => parameters.Add(p => p.Camera, SampleCamera));

		map.LastOptions!.AllowRotation.ShouldBeTrue(
			"the live adventure map turns with the traveller; opting out is the exception, not the default.");
	}

	/// <summary>
	/// Everything the overlay draws scales with the device pixel ratio.
	/// <para>
	/// The canvas is sized in <em>device</em> pixels and the projection plots into them, but
	/// every width, radius and font size in <c>SkiaMapOverlay</c> was written as a CSS pixel and
	/// tuned on a desktop browser where the two are equal. On a phone they are not: at the ratio
	/// of 3 a typical iPhone reports, a 2 px circle ring is two thirds of one CSS pixel. Tracks
	/// read as hairlines and the private-area circle - a sheer wash behind a sub-pixel ring -
	/// did not appear at all.
	/// </para>
	/// <para>
	/// Asserted through ink rather than through the constants, because the constants are the
	/// thing that would be got wrong. The same scene is rasterised at ratio 1 and ratio 3 into
	/// canvases of identical pixel size; if lengths scale, the ratio-3 frame lays down
	/// materially more ink. A frame that ignored the ratio would produce near-identical output.
	/// </para>
	/// </summary>
	[Fact]
	public void OverlayLengths_ScaleWithDevicePixelRatio()
	{
		BunitJSModuleInterop module = JSInterop.SetupModule("./_content/BlazorDLR.Shared/map/overlay.js");
		module.SetupVoid("present", _ => true).SetVoidResult();

		FakeMapInterop map = new() { InitialViewport = ViewportAtRatio(1) };
		Services.AddSingleton<IMapInterop>(map);
		Services.AddRideMapServices();
		Services.AddSingleton<IDeviceSettings, InMemoryDeviceSettings>();
		Services.AddSingleton<RouteStyleState>();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, SampleCamera)
			.Add(p => p.Circles, new List<MapCircle> { new(0, 0, RadiusM: 400) }));

		// Both frames come from one render, so ordering is this method's rather than xUnit's -
		// a theory split across cases would pass vacuously if the baseline case ran last.
		int atRatioOne = FrameBytesAfter(component, map, ViewportAtRatio(1));
		int atRatioThree = FrameBytesAfter(component, map, ViewportAtRatio(3));

		// Identical canvas in device pixels and identical ground covered, so the ratio is the
		// only variable. If lengths respond to it, the ratio-3 frame lays down materially more
		// ink; a frame that ignored it would be byte-identical.
		atRatioThree.ShouldBeGreaterThan(atRatioOne,
			"at ratio 3 the ring, the centre dot and the label are drawn 3× thicker into the same " +
			"canvas, so the frame must carry more ink. Equal output means lengths are still being " +
			"treated as device pixels - the bug that made tracks hairline-thin and the " +
			"private-area circle invisible on both phones.");
	}

	/// <summary>
	/// Reads one field off the anonymous options object handed to <c>present</c>. Reflection
	/// because the type is compiler-generated - naming the members here is the point of the
	/// assertion, since a rename on the C# side that JS does not follow is a silent break.
	/// </summary>
	private static object? ViewValue(object view, string name) =>
		view.GetType().GetProperty(name)?.GetValue(view)
		?? throw new InvalidOperationException(
			$"present() was handed no '{name}' - map/overlay.js reads it by that name.");

	private static MapViewport ViewportAtRatio(double ratio) => new(
		TopLeftLatitude: 0.01, TopLeftLongitude: -0.01,
		BottomRightLatitude: -0.01, BottomRightLongitude: 0.01,
		ZoomLevel: 12, HeadingDeg: 0,
		CanvasWidthPx: 600, CanvasHeightPx: 600,
		DevicePixelRatio: ratio);

	/// <summary>Pushes a viewport and returns the size of the frame the overlay produced for it.</summary>
	private int FrameBytesAfter(IRenderedComponent<RideMap> component, FakeMapInterop map, MapViewport viewport)
	{
		int before = JSInterop.Invocations["present"].Count;
		map.RaiseViewport(viewport);

		component.WaitForAssertion(
			() => JSInterop.Invocations["present"].Count.ShouldBeGreaterThan(before,
				"the overlay repaints on every viewport change - see OnParametersSet."),
			timeout: TimeSpan.FromSeconds(5));

		return Convert.FromBase64String((string)JSInterop.Invocations["present"].Last().Arguments[1]!).Length;
	}

	/// <summary>
	/// The component initialises whatever <see cref="IMapInterop"/> the host registered and
	/// reports what that thing said, without naming or branching on a provider.
	/// <para>
	/// This is the invariant v0.24 cashed in: three base maps were deleted and one added,
	/// and no screen changed. It is asserted through a fake that reports a provider the
	/// production registration never uses, so a <c>RideMap</c> that grew a check against
	/// <see cref="MapProvider.MapLibreOsm"/> would fail here rather than in a WebView.
	/// </para>
	/// </summary>
	[Fact]
	public void SameComponent_InitialisesWhateverIsRegistered_WithoutNamingIt()
	{
		FakeMapInterop map = new()
		{
			Provider = (MapProvider)999,
			// Force the stated-error branch so bUnit never renders SkiaMapOverlay - Skia's
			// SKCanvasView reaches for System.Runtime.InteropServices.JavaScript, which is
			// a browser-only API. The failure-path render still proves the shared component
			// composes: the same C# reaches InitAsync regardless of what answered.
			InitException = new InvalidOperationException("base map module unreachable in this test host."),
		};
		Services.AddSingleton<IMapInterop>(map);
		Services.AddRideMapServices();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, SampleCamera));

		component.WaitForAssertion(() =>
		{
			map.InitCount.ShouldBe(1,
				"§4.5: one shared component initialises whichever base map is registered - no per-provider Razor.");
			component.Markup.Contains("base map module unreachable", StringComparison.Ordinal).ShouldBeTrue(
				"the module's own message reaches the DOM - RideMap states the reason rather than substituting its own.");
		}, timeout: TimeSpan.FromSeconds(3));
	}
}
