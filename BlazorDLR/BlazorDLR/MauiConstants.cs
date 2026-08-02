namespace BlazorDLR;

/// <summary>
/// Compile-time defaults for the mobile host (§14.3, §18.5, SharedFrontend.md §8).
/// <para>
/// A phone app has no <c>appsettings.json</c> shipped alongside it the way the web
/// client does, so the "base URL" that would live in configuration lives here as a
/// constant instead. An environment variable named <c>DLR_API_BASE</c> or
/// <c>DLR_HUB_URL</c> — read at startup — overrides these for local development and
/// CI. <strong>Never</strong> a real API key: MapKit's private <c>.p8</c> stays on
/// the server and reaches the client as a short-lived JWT via
/// <c>GET /api/v1/maps/token</c>. This file carries only URLs.
/// </para>
/// </summary>
internal static class MauiConstants
{
	/// <summary>
	/// The API origin the mobile app talks to when no environment override is set.
	/// The Android emulator's <c>10.0.2.2</c> shortcut reaches the host machine; an
	/// iOS simulator can reach <c>127.0.0.1</c> because it shares a kernel with the
	/// host. A real device build must override this with the LAN address of the
	/// server, or with a production URL — via <c>DLR_API_BASE</c>.
	/// </summary>
	public const string DefaultApiBase =
#if ANDROID
		"http://10.0.2.2:5005/";
#else
		"http://127.0.0.1:5005/";
#endif

	/// <summary>
	/// The SignalR hub URL. Defaults to <c>/hubs/ride</c> on the API base; a
	/// deployment can point it elsewhere via <c>DLR_HUB_URL</c>.
	/// </summary>
	public static string DefaultHubUrl(string apiBase) =>
		new Uri(new Uri(apiBase), "/hubs/ride").ToString();

	/// <summary>
	/// The Google Maps browser API key on Android. <strong>Never a real value in
	/// committed code</strong> (§14.2). Real deployments provide it through a
	/// build-time constant override or a Phase 1 configuration fetch. Null renders
	/// the "stated error" branch in <c>RideMap.razor</c> — the correct behaviour
	/// when the key is absent.
	/// </summary>
	public const string? GoogleMapsKey = null;

	/// <summary>Read an override from the environment; returns the fallback when empty.</summary>
	public static string ResolveApiBase() =>
		Environment.GetEnvironmentVariable("DLR_API_BASE") is { Length: > 0 } value ? value : DefaultApiBase;

	/// <summary>Read a hub-URL override from the environment; derives from the API base when absent.</summary>
	public static string ResolveHubUrl(string apiBase) =>
		Environment.GetEnvironmentVariable("DLR_HUB_URL") is { Length: > 0 } value ? value : DefaultHubUrl(apiBase);
}
