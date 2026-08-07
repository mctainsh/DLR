using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Services;
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

		public ValueTask InitAsync(Microsoft.AspNetCore.Components.ElementReference host, MapOptions options, CancellationToken cancellationToken = default)
		{
			LastOptions = options;
			return _inner.InitAsync(host, options, cancellationToken);
		}

		public ValueTask SetCameraAsync(MapCamera camera, CancellationToken cancellationToken = default) => _inner.SetCameraAsync(camera, cancellationToken);
		public ValueTask DisposeAsync(CancellationToken cancellationToken = default) => _inner.DisposeAsync(cancellationToken);
	}
}
