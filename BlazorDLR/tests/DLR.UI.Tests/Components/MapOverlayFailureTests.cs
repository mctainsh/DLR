using System.Reflection;

using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Diagnostics;
using BlazorDLR.Shared.Markers;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;

using Bunit;

using DLR.UI.Tests.Fakes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace DLR.UI.Tests.Components;

/// <summary>
/// What the map does when the half of it that draws markers goes wrong.
/// <para>
/// The failure this file exists for was reported from a phone: "Map markers unavailable — the base
/// map is working, but pins, tracks and traveller positions cannot be drawn on this device", with
/// "One or more errors occurred. (Object reference not set to an instance of an object.)" for
/// evidence and nothing in the log. That banner is <c>RideMap</c>'s error boundary, which means the
/// overlay had thrown out of a lifecycle method and been unmounted for the rest of the page —
/// markers gone until the rider navigated away and back.
/// </para>
/// <para>
/// Two rules come out of that, and both are asserted here:
/// <list type="bullet">
///   <item>an icon that will not rasterise costs a plain pin, never the overlay, and</item>
///   <item>whatever went wrong is in the diagnostic log, in full, because on a phone in a mount
///     that log is the only copy of it there will ever be.</item>
/// </list>
/// </para>
/// </summary>
public sealed class MapOverlayFailureTests : BunitContext
{
	private static readonly MapCamera SampleCamera = new(-33.868, 151.209, 12);

	/// <summary>
	/// Empties the process-wide icon cache (§16.3 — rasterise once, keep for the life of the app),
	/// so a test starts against a host that has never rasterised anything.
	/// <para>
	/// <strong>Called from the test body, never from a constructor.</strong> xUnit builds a test
	/// class instance well ahead of running its body, so a reset written in one is undone by
	/// whatever ran in the gap — including the sibling test below that deliberately latches the
	/// cache as unavailable. Same trap, same rule as <c>SourceOfferFooterCache</c>.
	/// </para>
	/// <para>
	/// Reflection rather than a reset hook on the type: that would be production surface existing
	/// only for this file.
	/// </para>
	/// </summary>
	private static void ResetIconCache()
	{
		Type cache = typeof(MarkerIconCache);
		((System.Collections.IDictionary)cache
			.GetField("Pixels", BindingFlags.NonPublic | BindingFlags.Static)!
			.GetValue(null)!).Clear();
		cache.GetField("_module", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
		cache.GetField("_unavailable", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
	}

	/// <summary>
	/// Wires everything <c>RideMap</c> and the overlay inside it resolve, plans the overlay module,
	/// and clears the icon cache. Every test's first line.
	/// </summary>
	private void WireMap()
	{
		ResetIconCache();

		JSInterop.SetupModule("./_content/BlazorDLR.Shared/map/overlay.js")
			.SetupVoid("present", _ => true).SetVoidResult();

		Services.AddSingleton<IMapInterop>(new FakeMapInterop());
		Services.AddRideMapServices();
		Services.AddSingleton<IDeviceSettings, InMemoryDeviceSettings>();
		Services.AddSingleton<RouteStyleState>();
	}

	/// <summary>An authored marker with a key nothing else in the suite shares, so the cache cannot answer from another test.</summary>
	private static Dictionary<Guid, MapMarker> OneMarker(out string iconKey)
	{
		iconKey = $"probe-{Guid.NewGuid():N}";
		Guid id = Guid.NewGuid();

		return new Dictionary<Guid, MapMarker>
		{
			[id] = new MapMarker(id, -33.868, 151.209, iconKey, "Servo"),
		};
	}

	private IRenderedComponent<RideMap> RenderMapWith(IReadOnlyDictionary<Guid, MapMarker> markers) =>
		Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, SampleCamera)
			.Add(p => p.Markers, markers));

	[Fact]
	public void AnIconRasteriserThatAnswersNothing_LeavesTheOverlayDrawing()
	{
		// The regression. `renderPixels` is declared to hand back a Uint8Array, and a JS function
		// that returns undefined — a stale cached module, a host that answers the import with
		// something else — deserialises as null. The C# side read `.Length` off it, and the
		// NullReferenceException that followed came out of OnAfterRenderAsync, which is not a
		// failed frame but an unmounted overlay.
		WireMap();
		JSInterop.SetupModule("./_content/BlazorDLR.Shared/map/markers.js")
			.Setup<byte[]?>("renderPixels", _ => true).SetResult(null);

		IRenderedComponent<RideMap> component = RenderMapWith(OneMarker(out _));

		component.WaitForAssertion(() =>
		{
			component.FindAll("canvas.dlr-map-overlay").Count.ShouldBe(1,
				"an icon that cannot be rasterised is a plain pin — the overlay draws the marker either way.");
			component.Markup.Contains("Map markers unavailable", StringComparison.Ordinal).ShouldBeFalse(
				"neither RideMap's boundary nor the overlay's own error branch has anything to report here.");
		}, timeout: TimeSpan.FromSeconds(5));
	}

	[Fact]
	public void AnIconRasteriserThatThrows_IsLoggedInFullAndLeavesTheOverlayDrawing()
	{
		WireMap();
		JSInterop.SetupModule("./_content/BlazorDLR.Shared/map/markers.js")
			.Setup<byte[]?>("renderPixels", _ => true)
			.SetException(new JSException("marker icon failed to load"));

		// Captured as it is written. The log is a process-wide ring that every suite in every
		// other collection writes to — and one of them clears it — so reading it back afterwards
		// is a race this assertion loses every few runs. See LogCapture.
		using LogCapture log = new();

		IRenderedComponent<RideMap> component = RenderMapWith(OneMarker(out string iconKey));

		component.WaitForAssertion(() =>
		{
			component.FindAll("canvas.dlr-map-overlay").Count.ShouldBe(1,
				"one icon that will not draw is one plain pin, never a map without markers.");

			log.Text.Contains(iconKey, StringComparison.Ordinal).ShouldBeTrue(
				"the log has to say which icon it was — that is the whole of what makes the entry actionable.");
			log.Text.Contains("JSException", StringComparison.Ordinal).ShouldBeTrue(
				"and what threw, by type: a message on its own has already proved not to be enough.");
		}, timeout: TimeSpan.FromSeconds(5));
	}

	[Fact]
	public void NoRasteriserAtAll_LeavesTheOverlayDrawingAndSaysSoOnce()
	{
		// The module import itself failing — the case that is worth latching, because it will
		// answer the same way for the rest of the session. Simulated by leaving markers.js
		// unplanned: bUnit's JS interop is strict, so importing it throws, which is what a host
		// that cannot fetch the module does.
		WireMap();

		using LogCapture log = new();

		IRenderedComponent<RideMap> component = RenderMapWith(OneMarker(out _));

		component.WaitForAssertion(() =>
		{
			component.FindAll("canvas.dlr-map-overlay").Count.ShouldBe(1,
				"§16.3: no rasteriser is a map of plain pins, not a map with a banner over it.");

			log.Text.Contains("loading the marker rasteriser", StringComparison.Ordinal).ShouldBeTrue(
				"a device where every marker is a dot has a reason, and this is the only place it is recorded.");
		}, timeout: TimeSpan.FromSeconds(5));
	}
}
