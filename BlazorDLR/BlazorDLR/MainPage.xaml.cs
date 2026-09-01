using BlazorDLR.Shared.Diagnostics;

namespace BlazorDLR;

public partial class MainPage : ContentPage
{
	public MainPage()
	{
		InitializeComponent();

		DiagnosticLog.Write("Startup: MainPage constructed; the WebView is next.");

#if ANDROID
		blazorWebView.BlazorWebViewInitialized += OnBlazorWebViewInitialized;
#endif
	}

#if ANDROID
	/// <summary>
	/// Lets the WebView load the app's own loopback map-pack server (§4.5, §13 Q26).
	/// <para>
	/// <strong>The problem.</strong> BlazorWebView serves this app from <c>https://0.0.0.0/</c> on
	/// Android. <c>LoopbackMapPackServer</c> serves a downloaded PMTiles archive over plain HTTP on
	/// <c>127.0.0.1</c> - it cannot be HTTPS, because a self-signed certificate is exactly what a
	/// WebView refuses. So every range request for a map tile is an <em>http</em> subresource on an
	/// <em>https</em> page, which is textbook mixed content, and Android's WebView blocks it by
	/// default with <c>MIXED_CONTENT_NEVER_ALLOW</c>.
	/// </para>
	/// <para>
	/// The block is silent in the way that matters: MapLibre gets a failed fetch, the map object
	/// exists and renders nothing, and the offline map is a blank rectangle. The platform's own
	/// <c>network_security_config.xml</c> does not help - that governs whether the OS permits
	/// cleartext at all, and this is the WebView applying a stricter rule of its own on top.
	/// </para>
	/// <para>
	/// <strong>Why this is narrower than it looks.</strong> <c>CompatibilityMode</c> was tried
	/// first and is not enough: it permits passive content - images, stylesheets - and still
	/// blocks <c>fetch</c>, which is the only thing this app does over loopback. What
	/// <c>AlwaysAllow</c> widens is the set of hosts this WebView may load cleartext from, and that
	/// set is already bounded underneath by the network security config: <c>127.0.0.1</c>,
	/// <c>localhost</c> and the emulator's host alias, and nothing else. Every page in this WebView
	/// is also local content the app itself shipped - there is no third-party page here to inject
	/// an <c>http://</c> URL that the OS layer would then permit.
	/// </para>
	/// <para>
	/// The alternative is serving the archive from the app's own origin through a custom
	/// <c>WebViewClient</c>, which removes the mixed-content question entirely - and hooks
	/// BlazorWebView internals that move between MAUI releases. Recorded in
	/// <c>Documentation/offline-maps-plan.md §4.4</c> as approach B if this becomes a problem.
	/// </para>
	/// </summary>
	private static void OnBlazorWebViewInitialized(object? sender, Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs e)
	{
		// The last rung the C# side can report. Everything after this happens inside the WebView,
		// and MainLayout's "Page:" line is what says it got there - a log that ends here is an app
		// whose WebView came up and whose Blazor tree did not.
		DiagnosticLog.Write("Startup: BlazorWebView initialised.");

		if (e.WebView.Settings is { } settings)
		{
			settings.MixedContentMode = Android.Webkit.MixedContentHandling.AlwaysAllow;
		}
	}
#endif
}
