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
/// That pairing has already been got wrong once - every viewport event was silently dropped
/// - and three private copies of it meant three places to get it wrong again. All three
/// hosts reference this assembly, and this is plain <c>Microsoft.JSInterop</c>, so no host
/// type leaks into the shared project.
/// </para>
/// </summary>
public sealed class MapBridge
{
	private readonly Action<MapViewport> _forwardViewport;
	private readonly Action<MapClick> _forwardClick;
	private readonly Action<string> _forwardError;
	private readonly Action<MapGesture> _forwardGesture;
	private readonly Action<MapCoverage> _forwardCoverage;

	/// <summary>Creates a bridge that forwards to a host interop's events.</summary>
	/// <param name="forwardViewport">Raises <see cref="IMapInterop.ViewportChanged"/>.</param>
	/// <param name="forwardClick">Raises <see cref="IMapInterop.Clicked"/>.</param>
	/// <param name="forwardError">Raises <see cref="IMapInterop.ErrorOccurred"/>.</param>
	/// <param name="forwardGesture">Raises <see cref="IMapInterop.Gestured"/>.</param>
	/// <param name="forwardCoverage">Raises <see cref="IMapInterop.CoverageChanged"/>.</param>
	public MapBridge(
		Action<MapViewport> forwardViewport,
		Action<MapClick> forwardClick,
		Action<string> forwardError,
		Action<MapGesture> forwardGesture,
		Action<MapCoverage> forwardCoverage)
	{
		_forwardViewport = forwardViewport;
		_forwardClick = forwardClick;
		_forwardError = forwardError;
		_forwardGesture = forwardGesture;
		_forwardCoverage = forwardCoverage;
	}

	/// <summary>Called by the base-map module whenever the view moves.</summary>
	/// <param name="viewport">The new view.</param>
	[JSInvokable]
	public void OnViewportChanged(MapViewport viewport) => _forwardViewport(viewport);

	/// <summary>Called by the base-map module when the user taps the map.</summary>
	/// <param name="click">Where they tapped.</param>
	[JSInvokable]
	public void OnMapClicked(MapClick click) => _forwardClick(click);

	/// <summary>
	/// Called when the base map itself reports a problem - a tile that would not load, a style that
	/// would not parse, a source it could not reach.
	/// <para>
	/// These do not throw out of <c>createMap</c>: the map object exists and is happily rendering
	/// nothing, so without this the failure is entirely silent and a rider sees a blank rectangle
	/// with no explanation anywhere. It is the one class of map failure that used to be invisible.
	/// </para>
	/// </summary>
	/// <param name="message">What MapLibre said.</param>
	[JSInvokable]
	public void OnMapError(string message) => _forwardError(message);

	/// <summary>
	/// Called when the base map settles on a view whose coverage differs from the last one it
	/// reported - see <see cref="IMapInterop.CoverageChanged"/>.
	/// </summary>
	/// <param name="coverage">What the map can draw where it is now looking.</param>
	[JSInvokable]
	public void OnMapCoverage(MapCoverage coverage) => _forwardCoverage(coverage);

	/// <summary>
	/// Called when the rider moved the map with their own hand - see <see cref="IMapInterop.Gestured"/>
	/// for what turns on the distinction and why the module rather than this side has to draw it.
	/// </summary>
	/// <param name="kind">
	/// The gesture's name as the module spells it: <c>pan</c> or <c>rotate</c>. A name from a newer
	/// module is dropped rather than guessed at - an automatic mode cancelled by something nobody
	/// meant is worse than one that outlives a gesture this build has never heard of.
	/// </param>
	[JSInvokable]
	public void OnMapGesture(string kind)
	{
		switch (kind)
		{
			case "pan": _forwardGesture(MapGesture.Pan); break;
			case "rotate": _forwardGesture(MapGesture.Rotate); break;
		}
	}
}
