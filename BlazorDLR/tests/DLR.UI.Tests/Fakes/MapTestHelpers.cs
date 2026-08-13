using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Fakes;

/// <summary>
/// What <c>RideMap</c> needs from DI besides an <see cref="IMapInterop"/>.
/// <para>
/// The component reads which tiles to draw from <see cref="MapSourceState"/> (§4.5), so every
/// suite that renders a map — the map's own component tests, and every page that embeds one —
/// has to resolve it. One helper rather than the same three lines in eight places, in the same
/// shape as <c>AddRealAuthorizationPipeline</c> next door.
/// </para>
/// <para>
/// <strong>Registered first, so a test can still override any of it.</strong> The last
/// registration of a service type is the one resolved, and several suites register their own
/// <see cref="IDeviceSettings"/> instance to assert against. Calling this from a constructor —
/// which is what <c>PageTestContext</c> does — puts these underneath whatever the test adds.
/// </para>
/// </summary>
internal static class MapTestHelpers
{
	/// <summary>
	/// Registers the device store, the pack store, the tile-source state and the route style —
	/// everything <c>RideMap</c> and the <c>SkiaMapOverlay</c> inside it resolve.
	/// <para>
	/// The overlay is in the list because it is part of the map's own tree: a map whose
	/// <c>InitAsync</c> <em>succeeds</em> reports a viewport, which mounts the overlay, which
	/// injects <see cref="RouteStyleState"/>. A missing service there throws during component
	/// instantiation — which no <c>ErrorBoundary</c> catches, and which surfaces as a failure
	/// nothing to do with the test that hit it.
	/// </para>
	/// <para>
	/// The pack store is <see cref="UnavailableOfflineStore"/> — the binding the browser hosts
	/// get (§18.6) — so <c>MapSourceState.CanUseOffline</c> is false by default and no test
	/// touches a real file. A test about offline registers a <c>FakeOfflineStore</c> over it.
	/// </para>
	/// </summary>
	public static void AddRideMapServices(this IServiceCollection services)
	{
		services.AddScoped<IDeviceSettings, InMemoryDeviceSettings>();
		services.AddScoped<IOfflineStore, UnavailableOfflineStore>();
		services.AddScoped<IMapPackStore, UnavailableMapPackStore>();
		services.AddScoped<MapSourceState>();
		services.AddScoped<RouteStyleState>();
	}
}
