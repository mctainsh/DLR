namespace BlazorDLR;

/// <summary>
/// Compile-time defaults for the mobile host (§14.3, §18.5, SharedFrontend.md §8).
/// <para>
/// A phone app has no <c>appsettings.json</c> shipped alongside it the way the web
/// client does, so the "base URL" that would live in configuration lives here as a
/// constant instead. An environment variable named <c>DLR_API_BASE</c> or
/// <c>DLR_HUB_URL</c> - read at startup - overrides these for local development and
/// CI. <strong>This file carries only URLs, and since v0.24 there is no map credential
/// left anywhere on the client to tempt anyone otherwise</strong> - MapLibre over OSM
/// needs no key on any host (§4.5, §14.2).
/// </para>
/// </summary>
internal static class MauiConstants
{
	/// <summary>
	/// The API origin the mobile app talks to when no environment override is set.
	/// Android and iOS ship pointing at production, so an installed build works on a
	/// real device with no configuration. Other MAUI targets (Windows / Mac Catalyst
	/// stubs) stay on the loopback address they build against. To develop against a
	/// local server, set <c>DLR_API_BASE</c> - <c>http://10.0.2.2:5005/</c> from the
	/// Android emulator (its shortcut to the host machine), <c>http://127.0.0.1:5005/</c>
	/// from the iOS simulator (it shares a kernel with the host).
	/// </summary>
	public const string DefaultApiBase =
#if ANDROID || IOS
		"https://dlr.securehub.net/";
#else
		"http://127.0.0.1:5005/";
#endif

	/// <summary>
	/// The SignalR hub URL. Defaults to <c>/hubs/ride</c> on the API base; a
	/// deployment can point it elsewhere via <c>DLR_HUB_URL</c>.
	/// </summary>
	public static string DefaultHubUrl(string apiBase) =>
		new Uri(new Uri(apiBase), "/hubs/ride").ToString();

	/// <summary>Read an override from the environment; returns the fallback when empty.</summary>
	public static string ResolveApiBase() =>
		Environment.GetEnvironmentVariable("DLR_API_BASE") is { Length: > 0 } value ? value : DefaultApiBase;

	/// <summary>Read a hub-URL override from the environment; derives from the API base when absent.</summary>
	public static string ResolveHubUrl(string apiBase) =>
		Environment.GetEnvironmentVariable("DLR_HUB_URL") is { Length: > 0 } value ? value : DefaultHubUrl(apiBase);
}
