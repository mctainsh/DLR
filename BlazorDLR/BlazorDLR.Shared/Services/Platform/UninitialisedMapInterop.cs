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
	public ValueTask InitAsync(ElementReference host, MapOptions options, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException(SsrGuard);

	/// <inheritdoc />
	public ValueTask SetCameraAsync(MapCamera camera, CancellationToken cancellationToken = default) =>
		throw new NotImplementedException(SsrGuard);

	/// <inheritdoc />
	public ValueTask DisposeAsync(CancellationToken cancellationToken = default) =>
		ValueTask.CompletedTask;
}
