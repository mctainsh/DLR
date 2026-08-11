using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using BlazorDLR.Web.Client.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace BlazorDLR.Web.Client;

internal class Program
{
	static async Task Main(string[] args)
	{
		var builder = WebAssemblyHostBuilder.CreateDefault(args);

		// Device-specific services used by the BlazorDLR.Shared project.
		builder.Services.AddSingleton<IFormFactor, FormFactor>();

		// TimeProvider from day one (§10.4). Every timing decision in the client — token
		// caches, staleness gates, the map spike's own last-update stamp — resolves it.
		builder.Services.AddSingleton(TimeProvider.System);

		// The API base address is the origin the WASM bundle was served from — Caddy fronts
		// Kestrel in production (§9.1), and dev serves from the same origin in Development.
		// credentials: include is not needed here because Blazor WASM already respects the
		// browser's default cookie policy for same-origin requests; the __Host- cookie's
		// SameSite=Strict does the rest (§7.5, §18.5).
		//
		// Api:BaseUrl and Api:HubUrl in appsettings override same-origin when set — a
		// deployment that fronts the WASM host and the API from different origins can set
		// them without a code change. Empty means "use the WASM origin" (§14.3, §18.5).
		//
		// No bearer auth handler on the web: the refresh cookie authenticates every request
		// automatically, and adding a bearer here would put the access token where a page
		// script could read it (§18.5).
		string apiBase = builder.Configuration["Api:BaseUrl"] is { Length: > 0 } configured
			? configured
			: builder.HostEnvironment.BaseAddress;
		string hubBase = builder.Configuration["Api:HubUrl"] is { Length: > 0 } configuredHub
			? configuredHub
			: new Uri(new Uri(apiBase), "/hubs/ride").ToString();

		// BearerAuthHandler attaches the access token from AuthState on every /api/* request
		// and retries once on 401 with a refreshed token (§7.4). It resolves AuthState
		// lazily to break what would otherwise be a construction-time cycle:
		// AuthState → IApiClient → HttpClient → this handler → AuthState.
		builder.Services.AddScoped<BearerAuthHandler>();
		builder.Services.AddScoped(sp =>
		{
			BearerAuthHandler handler = sp.GetRequiredService<BearerAuthHandler>();
			handler.InnerHandler = new HttpClientHandler();
			return new HttpClient(handler) { BaseAddress = new Uri(apiBase) };
		});
		builder.Services.AddScoped<HttpApiClient>();
		builder.Services.AddScoped<IApiClient>(sp => sp.GetRequiredService<HttpApiClient>());

		// Auth state. The AuthenticationStateProvider is a scoped service that pulls its
		// initial claims from IApiClient and re-broadcasts on ApplySessionAsync — the
		// Welcome page and the SSR cookie handoff both drive it.
		builder.Services.AddScoped<AuthState>();
		builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<AuthState>());
		builder.Services.AddAuthorizationCore();

		// Live positions and hub events (§5.3, §5.7). The token provider hands SignalR the
		// current access token from AuthState — the same one BearerAuthHandler attaches to
		// API requests. The refresh cookie is HttpOnly and unreachable from WASM, and the
		// server accepts ?access_token= only on the ride hub path (§7.6).
		builder.Services.AddScoped<IRideHubClient>(sp => new SignalRRideHubClient(
			new Uri(hubBase),
			async ct => await sp.GetRequiredService<AuthState>().GetOrRefreshAccessTokenAsync(ct)));

		// Phase 1 repositories: HTTP passthrough for both, on both hosts. Phase 2 replaces
		// the mobile bindings with SQLite-backed ones (§4.4, §18.6).
		builder.Services.AddScoped<IRideRepository, HttpRideRepository>();
		builder.Services.AddScoped<ITrackRepository, HttpTrackRepository>();

		// The web's token is an HttpOnly cookie the JS heap cannot read (§18.5), so
		// this store is a no-op on purpose — writing is silent, reading returns null.
		builder.Services.AddScoped<ITokenStore, CookieBackedTokenStore>();

		// No GPS in the browser (§18.6); no push in the browser in v1 (§18.2).
		builder.Services.AddScoped<ILocationProvider, NoopLocationProvider>();
		builder.Services.AddScoped<INotificationService, NoopNotificationService>();

		// Web IMediaPicker uses <InputFile> plumbed through a static holder — real
		// implementation in BlazorDLR.Web.Client/Services/BrowserMediaPicker.cs.
		builder.Services.AddScoped<IMediaPicker, BrowserMediaPicker>();

		// Social sign-in (§7.16). Scaffolded but not available today — real bindings
		// need registrations at the provider that happen with store submission (Phase 3
		// exit criterion). The Welcome page shows the buttons dimmed until IsAvailable
		// returns true.
		builder.Services.AddScoped<IExternalSignInProvider>(_ => new UnavailableExternalSignInProvider(ExternalProvider.Apple));
		builder.Services.AddScoped<IExternalSignInProvider>(_ => new UnavailableExternalSignInProvider(ExternalProvider.Google));

		// MapLibre GL JS + OSM is the base map here and on the phones alike (§4.5 v0.24,
		// §18.3) — the same shared class, because it needs no credential to differ over.
		// Base-map role only; every rider pin, marker and track goes into the Skia overlay.
		// Transient, not scoped: one interop instance per <RideMap>, because each instance
		// owns a JS map and a DotNetObjectReference bridge. Shared scoped, navigating
		// ride → marker composer let the outgoing RideMap's DisposeAsync tear down the
		// *incoming* one's bridge — the JS map lived on and every viewport and click then
		// died against "no tracked object with id N", so the map drew but nothing it
		// reported ever reached C#.
		builder.Services.AddTransient<IMapInterop, MapLibreInterop>();

		// Theme preference (§18.6): dark by default, persisted in browser localStorage.
		// ThemeState broadcasts changes to the layout so the data-theme attribute updates
		// without a page reload.
		builder.Services.AddScoped<IThemeService, LocalStorageThemeService>();
		builder.Services.AddScoped<BlazorDLR.Shared.State.ThemeState>();

		// Device-local preferences that are not the theme (§18.6), also in localStorage.
		// RouteStyleState broadcasts to the Skia overlay so a change made on the ride's info
		// page is on the map before the rider gets back to it.
		builder.Services.AddScoped<IDeviceSettings, LocalStorageDeviceSettings>();
		builder.Services.AddScoped<BlazorDLR.Shared.State.RouteStyleState>();

		// The private area (§10.1, §18.6): a point and a radius this browser never sends
		// anywhere. It gates recording and publishing, neither of which the web host does
		// (§18.6) — it is registered here so the profile screen can set one for the phone
		// that will, and because a device-local setting is per-browser by construction.
		builder.Services.AddScoped<BlazorDLR.Shared.State.PrivateAreaState>();

		// The one confirm modal for every destructive action in the app. Mounted in
		// MainLayout; pages call `await Confirm.AskAsync(...)` in place of `window.confirm`.
		builder.Services.AddScoped<BlazorDLR.Shared.State.ConfirmService>();

		// PageNav's back arrow asks this whether stepping back lands inside the app. Counted
		// here rather than read from window.history.length, which also counts the pages the
		// tab visited before this one — see the type's remarks.
		builder.Services.AddScoped<BlazorDLR.Shared.State.NavigationHistory>();

		await builder.Build().RunAsync();
	}
}
