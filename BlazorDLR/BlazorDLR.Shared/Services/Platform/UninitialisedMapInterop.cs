using DLR.Core.Tracks;
using Microsoft.AspNetCore.Components;

namespace BlazorDLR.Shared.Services.Platform;

/// <summary>
/// The <see cref="IMapInterop"/> the SSR pass in <c>BlazorDLR.Web</c> binds.
/// <para>
/// Every interactive host registers the real <see cref="MapLibreInterop"/>; this survives for
/// the prerender, which has no JS runtime to import a module into. Initialising throws rather
/// than degrades — a prerender that tried would fail mid-render instead of handing the client
/// a shell to hydrate, and the WASM client initialises the real map the moment it boots.
/// </para>
/// </summary>
public sealed class UninitialisedMapInterop : IMapInterop
{
	private const string SsrGuard =
		"The SSR pass has no JS runtime to import the map module into — the WASM client that " +
		"boots after it re-resolves IMapInterop and initialises the map there. A component " +
		"initialising a map during a static render is a wiring bug.";

	/// <inheritdoc />
	public MapProvider Provider => MapProvider.MapLibreOsm;

	/// <inheritdoc />
	public event Action<MapViewport>? ViewportChanged
	{
		add { /* no viewport is ever emitted during a static render. */ }
		remove { /* symmetric no-op. */ }
	}

	/// <inheritdoc />
	public event Action<MapClick>? Clicked
	{
		add { /* nothing to click during a prerender. */ }
		remove { /* symmetric no-op. */ }
	}

	/// <inheritdoc />
	public event Action<string>? ErrorOccurred
	{
		add { /* a map that never attaches never reports one. */ }
		remove { /* symmetric no-op. */ }
	}

	/// <inheritdoc />
	public event Action<MapGesture>? Gestured
	{
		add { /* nothing to pan or turn during a prerender. */ }
		remove { /* symmetric no-op. */ }
	}

	/// <inheritdoc />
	public event Action<MapCoverage>? CoverageChanged
	{
		add { /* a map that never draws never reports what it could not draw. */ }
		remove { /* symmetric no-op. */ }
	}

	/// <inheritdoc />
	public ValueTask InitAsync(ElementReference host, MapOptions options, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException(SsrGuard);

	/// <inheritdoc />
	public ValueTask SetCameraAsync(MapCamera camera, TimeSpan animation = default, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException(SsrGuard);

	/// <inheritdoc />
	public ValueTask FitBoundsAsync(
		TrackBounds bounds,
		double paddingPx = 32,
		double maxZoomLevel = 16,
		CancellationToken cancellationToken = default) =>
		throw new NotImplementedException(SsrGuard);

	/// <summary>
	/// Silently ignored, unlike the three above.
	/// <para>
	/// This one is not a component asking a prerender to do something impossible — it is
	/// <c>MapSourceState</c> broadcasting a change that every mounted map listens for, and on this
	/// host there is no mounted map. The real interop treats the same call on an unattached map as
	/// a no-op for the same reason: a caller changing a device setting has no business knowing
	/// whether a map happens to exist.
	/// </para>
	/// </summary>
	public ValueTask SetSourceAsync(MapSource source, CancellationToken cancellationToken = default) =>
		ValueTask.CompletedTask;

	/// <inheritdoc />
	public ValueTask DisposeAsync(CancellationToken cancellationToken = default) =>
		ValueTask.CompletedTask;
}
