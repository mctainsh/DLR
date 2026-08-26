using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Tracks;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.UI.Tests.Components;

/// <summary>
/// <c>RideMap</c> composes the base-map JS module with the shared Skia overlay
/// (§4.5 v0.21). Two integration properties that live on the component and do not
/// require the JS module to load:
/// <list type="bullet">
///   <item>InitAsync is called exactly once even across re-renders — a base map that
///     initialises twice would double-bind DOM handlers.</item>
///   <item>Route / Markers / ShowUserLocation reach <c>InitAsync</c>'s options exactly
///     as the caller passed them; the SkiaMapOverlay picks up the same values via the
///     parameter cascade on the next viewport event.</item>
/// </list>
/// The SkiaMapOverlay itself renders in a live browser only (its <c>SKCanvasView</c>
/// reaches for <c>System.Runtime.InteropServices.JavaScript</c>). These tests force
/// the stated-error branch so the overlay never mounts — the assertion is on what
/// the fake interop observed before the failure.
/// </summary>
public sealed class RideMapForwardTests : BunitContext
{
	private static readonly MapCamera Camera = new(-33.868, 151.209, 12);

	[Fact]
	public void ShowUserLocation_Parameter_ReachesInitOptions()
	{
		FakeMapInterop map = new()
		{
			// Force stated-error so Skia never mounts — but Init still fires first.
			InitException = new InvalidOperationException("Stubbed."),
			// Wrap the fake so we can observe InitAsync's MapOptions argument.
		};
		ObservedMapInterop wrapped = new(map);
		Services.AddSingleton<IMapInterop>(wrapped);
		Services.AddRideMapServices();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, Camera)
			.Add(p => p.ShowUserLocation, true));

		component.WaitForAssertion(() =>
		{
			wrapped.LastOptions.ShouldNotBeNull(
				"§4.5: RideMap must call InitAsync with the options the caller supplied.");
		}, timeout: TimeSpan.FromSeconds(3));

		wrapped.LastOptions!.ShowUserLocation.ShouldBeTrue(
			"the ShowUserLocation flag must reach the base-map SDK — the platform's blue dot depends on it.");
		wrapped.LastOptions.Camera.Latitude.ShouldBe(-33.868);
		wrapped.LastOptions.Camera.Longitude.ShouldBe(151.209);
	}

	[Fact]
	public void ReRender_DoesNotReInitialiseTheMap()
	{
		ObservedMapInterop wrapped = new(new FakeMapInterop
		{
			InitException = new InvalidOperationException("Stubbed."),
		});
		Services.AddSingleton<IMapInterop>(wrapped);
		Services.AddRideMapServices();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, Camera));

		component.WaitForAssertion(() => wrapped.InitCount.ShouldBe(1),
			timeout: TimeSpan.FromSeconds(3));

		// Change a parameter — the component re-renders but must not re-init.
		component.Render(parameters => parameters
			.Add(p => p.Camera, new MapCamera(0, 0, 5)));

		wrapped.InitCount.ShouldBe(1,
			"§4.5: initialising twice on a re-render would double-bind JS handlers and often crash the module.");
	}

	[Fact]
	public async Task ACameraChangedWhileTheMapIsAttaching_IsStillApplied()
	{
		// The window is real and it is where the private-area picker lost its camera: a caller
		// that learns where to open from a device read can set Camera after its first render but
		// before InitAsync resolves. OnParametersSetAsync sees no applied camera yet and returns,
		// so unless init re-checks on the way out the move is dropped for good.
		SlowInitMapInterop map = new();
		Services.AddSingleton<IMapInterop>(map);
		Services.AddRideMapServices();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, Camera));

		component.WaitForAssertion(() => map.Started.ShouldBeTrue(), timeout: TimeSpan.FromSeconds(3));

		// The camera moves while the base map is still attaching.
		MapCamera moved = new(-31.95, 115.86, 15);
		component.Render(parameters => parameters.Add(p => p.Camera, moved));

		await component.InvokeAsync(map.CompleteInit);

		component.WaitForAssertion(
			() => map.Cameras.ShouldContain(moved,
				"a camera set during init must reach the base map — the alternative is a map " +
				"stuck on whatever it happened to open with."),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// An <see cref="IMapInterop"/> whose <c>InitAsync</c> hangs until the test releases it, and
	/// which never announces a viewport — so <c>SkiaMapOverlay</c>, browser-only, never mounts.
	/// </summary>
	private sealed class SlowInitMapInterop : IMapInterop
	{
		private readonly TaskCompletionSource _init = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public bool Started { get; private set; }

		public List<MapCamera> Cameras { get; } = new();

		public MapProvider Provider => MapProvider.MapLibreOsm;

		public event Action<MapViewport>? ViewportChanged;

		public event Action<MapClick>? Clicked;

		public event Action<string>? ErrorOccurred;

		/// <summary>A map that never finishes attaching is never gestured at either.</summary>
		public event Action<MapGesture>? Gestured
		{
			add { /* nothing to pan or turn — InitAsync has not returned. */ }
			remove { /* symmetric no-op. */ }
		}

		public void CompleteInit() => _init.TrySetResult();

		public async ValueTask InitAsync(Microsoft.AspNetCore.Components.ElementReference host, MapOptions options, CancellationToken cancellationToken = default)
		{
			Started = true;
			Cameras.Add(options.Camera);
			await _init.Task;
		}

		public ValueTask SetCameraAsync(MapCamera camera, TimeSpan animation = default, CancellationToken cancellationToken = default)
		{
			Cameras.Add(camera);
			return ValueTask.CompletedTask;
		}

		/// <summary>Nothing to record: a map that never finishes attaching is never framed either.</summary>
		public ValueTask FitBoundsAsync(
			TrackBounds bounds,
			double paddingPx = 32,
			double maxZoomLevel = 16,
			CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

		public ValueTask SetSourceAsync(MapSource source, CancellationToken cancellationToken = default) =>
			ValueTask.CompletedTask;

		public ValueTask DisposeAsync(CancellationToken cancellationToken = default)
		{
			// Nothing to release, and the events exist only to satisfy the interface — this fake
			// deliberately never raises any of them.
			_ = ViewportChanged;
			_ = Clicked;
			_ = ErrorOccurred;
			return ValueTask.CompletedTask;
		}
	}

	// -- The tile source (§4.5) ---------------------------------------------------------------

	[Fact]
	public async Task TheDevicesChosenTileSource_ReachesTheBaseMapAtInit()
	{
		FakeMapInterop map = new();
		Services.AddSingleton<IMapInterop>(map);
		Services.AddRideMapServices();

		MapSourceState sources = Services.GetRequiredService<MapSourceState>();
		await sources.SetAsync(MapSource.Custom("https://tiles.example.com/{z}/{x}/{y}.png", "© Example"));

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, new MapCamera(-33.868, 151.209, 12)));

		component.WaitForAssertion(() =>
		{
			map.LastOptions.ShouldNotBeNull();
			map.LastOptions.EffectiveSource.UrlTemplate.ShouldBe("https://tiles.example.com/{z}/{x}/{y}.png",
				"a map must open on the tiles the traveller chose, not on the default and then a restyle.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task ChangingTheSourceWhileAMapIsOpen_RestylesItRatherThanRebuildingIt()
	{
		// This is what makes the preview on the settings screen a preview: the state is scoped, so
		// the page writing it and the map reading it are the same instance. Restyling keeps the
		// camera — rebuilding would throw the rider back to a default view on every edit.
		FakeMapInterop map = new();
		Services.AddSingleton<IMapInterop>(map);
		Services.AddRideMapServices();

		MapSourceState sources = Services.GetRequiredService<MapSourceState>();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, new MapCamera(-33.868, 151.209, 12)));

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
			sources.SetAsync(MapSource.Custom("https://tiles.example.com/{z}/{x}/{y}.png", "© Example")));

		component.WaitForAssertion(() =>
		{
			map.Sources.Count.ShouldBe(1);
			map.Sources[0].Kind.ShouldBe(MapSourceKind.Custom);
		}, timeout: TimeSpan.FromSeconds(3));

		map.InitCount.ShouldBe(1, "the map was restyled, not torn down and rebuilt.");
	}

	[Fact]
	public async Task ASourceChangeAfterTheMapIsGone_IsNotDeliveredToIt()
	{
		FakeMapInterop map = new();
		Services.AddSingleton<IMapInterop>(map);
		Services.AddRideMapServices();

		MapSourceState sources = Services.GetRequiredService<MapSourceState>();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, new MapCamera(-33.868, 151.209, 12)));

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Instance.DisposeAsync().AsTask());

		await sources.SetAsync(MapSource.Custom("https://tiles.example.com/{z}/{x}/{y}.png", "© Example"));

		map.Sources.ShouldBeEmpty(
			"a disposed map still subscribed to the state would restyle a JS object that is already gone.");
	}

	// -- The base map complaining (§4.5) --------------------------------------------------------

	[Fact]
	public void AMapThatAttachedButCannotDrawTiles_SaysSoRatherThanGoingBlank()
	{
		// The failure offline packs made urgent. MapLibre does not throw for an unreachable source:
		// the map object exists and renders nothing, so without this the rider gets a blank
		// rectangle and cannot tell it from a broken app.
		FakeMapInterop map = new();
		Services.AddSingleton<IMapInterop>(map);
		Services.AddRideMapServices();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, new MapCamera(-33.868, 151.209, 12)));

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		map.RaiseError("Failed to fetch pmtiles://http://127.0.0.1:5000/x/sydney.pmtiles");

		component.WaitForAssertion(() =>
		{
			component.Markup.ShouldContain("Map tiles unavailable");
			component.Markup.ShouldContain("sydney.pmtiles");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void RepeatedTileErrors_ShowTheFirstRatherThanFlickering()
	{
		// One unreachable source raises an error per tile — a screenful is twenty a second.
		FakeMapInterop map = new();
		Services.AddSingleton<IMapInterop>(map);
		Services.AddRideMapServices();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, new MapCamera(-33.868, 151.209, 12)));

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		map.RaiseError("the first thing that went wrong");
		map.RaiseError("a later consequence of it");

		component.WaitForAssertion(
			() => component.Markup.ShouldContain("the first thing that went wrong"),
			timeout: TimeSpan.FromSeconds(3));

		// Keeping the latest would bury the cause under its own repetitions.
		component.Markup.ShouldNotContain("a later consequence of it");
	}

	[Fact]
	public void AnOfflinePackThatCannotBeRead_IsResolvedAgainOnce()
	{
		// A pack's archive is served over loopback, and the URL in the style carries a port the OS
		// assigned to this run. That address can stop being true while the map is still on screen —
		// a phone that suspends the app long enough for the listener to go takes every tile with it,
		// and the map never comes back on its own. Asking for the source again re-resolves the URL
		// and restarts the server, which is why this is worth one attempt before the banner.
		FakeMapInterop map = new();
		Services.AddSingleton<IMapInterop>(map);
		Services.AddRideMapServices();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, Camera)
			.Add(p => p.Source, MapSource.OfflinePack("au-qld")));

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		map.RaiseError("Load failed — source: protomaps");

		component.WaitForAssertion(
			() => map.Sources.ShouldBe([MapSource.OfflinePack("au-qld")]),
			timeout: TimeSpan.FromSeconds(3));

		// And once only: a pack that is genuinely unreadable would otherwise restyle the map on
		// every failed tile for as long as the screen is open.
		map.RaiseError("Load failed — source: protomaps");
		map.RaiseError("Load failed — source: protomaps");

		component.WaitForAssertion(
			() => component.Markup.ShouldContain("Map tiles unavailable"),
			timeout: TimeSpan.FromSeconds(3));

		map.Sources.Count.ShouldBe(1, "the re-resolve is spent against a source, not repeated per failed tile.");
	}

	[Fact]
	public void AnOnlineSourceThatCannotDrawTiles_IsNotResolvedAgain()
	{
		// Nothing to re-resolve: an OSM or custom template is the same string every time it is
		// asked for, so a restyle would only cost the rider a second round of failed tiles.
		FakeMapInterop map = new();
		Services.AddSingleton<IMapInterop>(map);
		Services.AddRideMapServices();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, Camera)
			.Add(p => p.Source, MapSource.Default));

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		map.RaiseError("Failed to fetch https://tile.openstreetmap.org/12/3/4.png");

		component.WaitForAssertion(
			() => component.Markup.ShouldContain("Map tiles unavailable"),
			timeout: TimeSpan.FromSeconds(3));

		map.Sources.ShouldBeEmpty();
	}

	// -- A caller holding the source (§4.2) -----------------------------------------------------

	/// <summary>
	/// The map-pack picker opens a world map, and the source stored on the device may be an offline
	/// pack — one region and then nothing, on the one screen where the rest of the world is the
	/// point. So a caller can hand this map a source of its own.
	/// </summary>
	[Fact]
	public async Task ACallerSuppliedSource_IsWhatTheMapOpensWith()
	{
		FakeMapInterop map = new();
		ObservedMapInterop wrapped = new(map);
		Services.AddSingleton<IMapInterop>(wrapped);
		Services.AddRideMapServices();

		// What the device says, and what this map is told to draw instead.
		await Services.GetRequiredService<MapSourceState>().SetAsync(
			MapSource.Custom("https://tiles.example.com/{z}/{x}/{y}.png", "© Example"));

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, Camera)
			.Add(p => p.Source, MapSource.Default));

		component.WaitForAssertion(
			() => wrapped.LastOptions!.EffectiveSource.ShouldBe(MapSource.Default),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// And the device changing underneath does not take it back. The caller is holding the answer;
	/// a restyle arriving from the setting would be the map deciding it knew better.
	/// </summary>
	[Fact]
	public async Task AMapHoldingItsOwnSource_DoesNotFollowTheDevice()
	{
		FakeMapInterop map = new();
		Services.AddSingleton<IMapInterop>(map);
		Services.AddRideMapServices();

		MapSourceState sources = Services.GetRequiredService<MapSourceState>();

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, Camera)
			.Add(p => p.Source, MapSource.Default));

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
			sources.SetAsync(MapSource.Custom("https://tiles.example.com/{z}/{x}/{y}.png", "© Example")));

		map.Sources.ShouldBeEmpty("the caller's source is the answer, and nothing else may replace it.");
	}

	/// <summary>
	/// Changing what the caller asked for restyles, on the same terms a device change does — which
	/// is how the picker answers a tile source that will not draw by falling back to OpenStreetMap.
	/// </summary>
	[Fact]
	public void ChangingTheCallersSource_RestylesTheMapItIsAlreadyShowing()
	{
		FakeMapInterop map = new();
		Services.AddSingleton<IMapInterop>(map);
		Services.AddRideMapServices();

		MapSource custom = MapSource.Custom("https://tiles.example.com/{z}/{x}/{y}.png", "© Example");

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, Camera)
			.Add(p => p.Source, custom));

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		component.Render(parameters => parameters.Add(p => p.Source, MapSource.Default));

		component.WaitForAssertion(() =>
		{
			map.Sources.Count.ShouldBe(1);
			map.Sources[0].ShouldBe(MapSource.Default);
		}, timeout: TimeSpan.FromSeconds(3));

		map.InitCount.ShouldBe(1, "restyled, not torn down and rebuilt.");
	}

	/// <summary>
	/// The complaint reaches the caller as well as the screen. A screen whose whole interface is
	/// drawn on the map — the pack picker — has somewhere better to go than a message about it.
	/// </summary>
	[Fact]
	public void TheFirstTileError_IsToldToTheCallerAsWellAsShown()
	{
		FakeMapInterop map = new();
		Services.AddSingleton<IMapInterop>(map);
		Services.AddRideMapServices();

		List<string> reported = [];

		IRenderedComponent<RideMap> component = Render<RideMap>(parameters => parameters
			.Add(p => p.Camera, Camera)
			.Add(p => p.OnTileError, reported.Add));

		component.WaitForAssertion(() => map.InitCount.ShouldBe(1), timeout: TimeSpan.FromSeconds(3));

		map.RaiseError("the first thing that went wrong");
		map.RaiseError("a later consequence of it");

		component.WaitForAssertion(
			() => reported.ShouldBe(["the first thing that went wrong"]),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>An <see cref="IMapInterop"/> that records init arguments before delegating.</summary>
	private sealed class ObservedMapInterop : IMapInterop
	{
		private readonly FakeMapInterop _inner;
		public MapOptions? LastOptions { get; private set; }
		public int InitCount => _inner.InitCount;

		public ObservedMapInterop(FakeMapInterop inner) => _inner = inner;

		public MapProvider Provider => _inner.Provider;
		public event Action<MapViewport>? ViewportChanged
		{
			add => _inner.ViewportChanged += value;
			remove => _inner.ViewportChanged -= value;
		}

		public event Action<MapClick>? Clicked
		{
			add => _inner.Clicked += value;
			remove => _inner.Clicked -= value;
		}

		public event Action<MapGesture>? Gestured
		{
			add => _inner.Gestured += value;
			remove => _inner.Gestured -= value;
		}

		public event Action<string>? ErrorOccurred
		{
			add => _inner.ErrorOccurred += value;
			remove => _inner.ErrorOccurred -= value;
		}

		public ValueTask InitAsync(Microsoft.AspNetCore.Components.ElementReference host, MapOptions options, CancellationToken cancellationToken = default)
		{
			LastOptions = options;
			return _inner.InitAsync(host, options, cancellationToken);
		}

		public ValueTask FitBoundsAsync(
			TrackBounds bounds,
			double paddingPx = 32,
			double maxZoomLevel = 16,
			CancellationToken cancellationToken = default) =>
			_inner.FitBoundsAsync(bounds, paddingPx, maxZoomLevel, cancellationToken);

		public ValueTask SetCameraAsync(MapCamera camera, TimeSpan animation = default, CancellationToken cancellationToken = default) => _inner.SetCameraAsync(camera, animation, cancellationToken);
		public ValueTask SetSourceAsync(MapSource source, CancellationToken cancellationToken = default) => _inner.SetSourceAsync(source, cancellationToken);
		public ValueTask DisposeAsync(CancellationToken cancellationToken = default) => _inner.DisposeAsync(cancellationToken);
	}
}
