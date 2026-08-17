using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
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

		public ValueTask SetCameraAsync(MapCamera camera, CancellationToken cancellationToken = default)
		{
			Cameras.Add(camera);
			return ValueTask.CompletedTask;
		}

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

		public ValueTask SetCameraAsync(MapCamera camera, CancellationToken cancellationToken = default) => _inner.SetCameraAsync(camera, cancellationToken);
		public ValueTask SetSourceAsync(MapSource source, CancellationToken cancellationToken = default) => _inner.SetSourceAsync(source, cancellationToken);
		public ValueTask DisposeAsync(CancellationToken cancellationToken = default) => _inner.DisposeAsync(cancellationToken);
	}
}
