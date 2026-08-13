namespace BlazorDLR.Shared.Services;

/// <summary>
/// Makes a downloaded archive reachable by URL, so MapLibre can read it (§4.5, §13 Q26).
/// <para>
/// <strong>Why a seam and not just a file path.</strong> The renderer is JavaScript inside a
/// WebView, and PMTiles is read by HTTP range request. A path on disk is not something the WebView
/// can address — <c>file://</c> is blocked from the app's own origin on both platforms, and the
/// BlazorWebView serves the app from a scheme it owns. Something has to turn "a file" into "a
/// URL", and this is the seam that does it.
/// </para>
/// <para>
/// <strong>The phone binds a loopback HTTP server</strong>
/// (<see cref="Platform.LoopbackMapPackServer"/>); the browser hosts bind one that answers
/// nothing, because they hold no archives to serve (§18.6). Two alternatives were weighed and
/// recorded in <c>Documentation/offline-maps-plan.md §4.4</c>: a custom WebView scheme handler
/// (two platform implementations, hand-rolled range parsing, and it hooks BlazorWebView internals
/// that move between MAUI releases) and pumping tiles through JS interop (every tile
/// base64-encoded across the boundary, roughly twenty per view change, on every pan).
/// </para>
/// </summary>
public interface IMapPackServer
{
	/// <summary>Whether this host can serve archives at all.</summary>
	bool IsSupported { get; }

	/// <summary>
	/// The URL MapLibre should read <paramref name="packId"/> from, starting the server if it is
	/// not already running — or <c>null</c> when this host serves nothing, or the device does not
	/// hold that archive.
	/// <para>
	/// A <c>null</c> is not an error to report: it is the answer that sends
	/// <c>MapSourceState.Effective</c> back to an online source, which is a working map under the
	/// routes and pins the screen is actually for.
	/// </para>
	/// <para>
	/// The URL is not stable across launches. It carries a port the OS assigned and a secret this
	/// process generated, so it must be asked for each time rather than stored.
	/// </para>
	/// </summary>
	/// <param name="packId">Which archive.</param>
	/// <param name="cancellationToken">Cancels the start.</param>
	ValueTask<Uri?> ResolveAsync(string packId, CancellationToken cancellationToken = default);
}
