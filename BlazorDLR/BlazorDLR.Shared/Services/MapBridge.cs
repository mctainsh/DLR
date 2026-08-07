using Microsoft.JSInterop;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// The callback seam between a base-map JS module and its <see cref="IMapInterop"/>.
/// <para>
/// A JS module cannot raise a C# event, but it can invoke a <c>[JSInvokable]</c> method on a
/// <see cref="DotNetObjectReference"/>. This turns those calls back into the
/// <see cref="IMapInterop.ViewportChanged"/> and <see cref="IMapInterop.Clicked"/> events the
/// shared components listen to.
/// </para>
/// <para>
/// <strong>One copy, deliberately.</strong> The method names below are half of a contract
/// whose other half is a string in each JS module (<c>map/interop.js</c>'s <c>dispatch</c>).
/// That pairing has already been got wrong once — every viewport event was silently dropped
/// — and three private copies of it meant three places to get it wrong again. All three
/// hosts reference this assembly, and this is plain <c>Microsoft.JSInterop</c>, so no host
/// type leaks into the shared project.
/// </para>
/// </summary>
public sealed class MapBridge
{
	private readonly Action<MapViewport> _forwardViewport;
	private readonly Action<MapClick> _forwardClick;

	/// <summary>Creates a bridge that forwards to a host interop's events.</summary>
	/// <param name="forwardViewport">Raises <see cref="IMapInterop.ViewportChanged"/>.</param>
	/// <param name="forwardClick">Raises <see cref="IMapInterop.Clicked"/>.</param>
	public MapBridge(Action<MapViewport> forwardViewport, Action<MapClick> forwardClick)
	{
		_forwardViewport = forwardViewport;
		_forwardClick = forwardClick;
	}

	/// <summary>Called by the base-map module whenever the view moves.</summary>
	/// <param name="viewport">The new view.</param>
	[JSInvokable]
	public void OnViewportChanged(MapViewport viewport) => _forwardViewport(viewport);

	/// <summary>Called by the base-map module when the user taps the map.</summary>
	/// <param name="click">Where they tapped.</param>
	[JSInvokable]
	public void OnMapClicked(MapClick click) => _forwardClick(click);
}
