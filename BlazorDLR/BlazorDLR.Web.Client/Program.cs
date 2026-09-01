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
		Shared.Diagnostics.DiagnosticLog.Write("Starting : " + typeof(Shared.Components.SourceOfferFooter).Assembly.GetName().Version);

		var builder = WebAssemblyHostBuilder.CreateDefault(args);

		// Device-specific services used by the BlazorDLR.Shared project.
		builder.Services.AddSingleton<IFormFactor, FormFactor>();

		// TimeProvider from day one (§10.4). Every timing decision in the client - token
		// caches, staleness gates, the map spike's own last-update stamp - resolves it.
		builder.Services.AddSingleton(TimeProvider.System);

		// The API base address is the origin the WASM bundle was served from - Caddy fronts
		// Kestrel in production (§9.1), and dev serves from the same origin in Development.
		// credentials: include is not needed here because Blazor WASM already respects the
		// browser's default cookie policy for same-origin requests; the __Host- cookie's
		// SameSite=Strict does the rest (§7.5, §18.5).
		//
		// Api:BaseUrl and Api:HubUrl in appsettings override same-origin when set - a
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
		// initial claims from IApiClient and re-broadcasts on ApplySessionAsync - the
		// Welcome page and the SSR cookie handoff both drive it.
		builder.Services.AddScoped<AuthState>();
		builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<AuthState>());

		// Username to profile photograph, for every screen that draws a name (§7.3). Scoped like
		// AuthState above it, and for the same reason: what it holds belongs to one signed-in
		// session and must go when that session does.
		builder.Services.AddScoped<RiderAvatars>();
		builder.Services.AddAuthorizationCore();

		// Live positions and hub events (§5.3, §5.7). The token provider hands SignalR the
		// current access token from AuthState - the same one BearerAuthHandler attaches to
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
		// this store is a no-op on purpose - writing is silent, reading returns null.
		builder.Services.AddScoped<ITokenStore, CookieBackedTokenStore>();

		// No device-local copy of a ride in the browser - §18.6 keeps offline-first a property of
		// the phone. Registered rather than omitted because the shared ride screens resolve the
		// cache unconditionally; this one answers "nothing stored" and drops every write, which is
		// the truthful answer here and keeps the screens free of a host check.
		builder.Services.AddScoped<IOfflineStore, UnavailableOfflineStore>();
		builder.Services.AddScoped<RideSnapshotCache>();

		// No downloaded map archives here either, and therefore nothing to serve them over (§18.6).
		// Registered rather than omitted because the map seam resolves them unconditionally.
		builder.Services.AddScoped<IMapPackStore, UnavailableMapPackStore>();
		builder.Services.AddScoped<IMapPackServer, UnavailableMapPackServer>();
		builder.Services.AddScoped(sp => new MapPackDownloader(
			sp.GetRequiredService<IMapPackStore>(),
			MapPackDownloader.CreateCredentialFreeClient()));

		// And the catalogue of packs on offer (§4.2), which this host will never read either:
		// MapPackState does not fetch it where there is nowhere to put the result, so the browser
		// spends no request on a list the Maps screen does not render here.
		builder.Services.AddScoped(_ => new MapPackCatalogue(
			MapPackCatalogue.CreateCredentialFreeClient(),
			new Uri(MapPackCatalogue.DefaultUrl)));

		builder.Services.AddScoped<BlazorDLR.Shared.State.MapPackState>();

		// No notifications in the browser in v1 (§18.2). Since v0.26 the mobile hosts raise local
		// ones rather than taking a push - but the surface that feature exists for is a phone on a
		// bar mount, and a laptop with the tab open is already showing the thread. The screen lock
		// is the same answer for a different reason - the API exists here, the case for it does not
		// (see UnavailableScreenWakeLock). GPS is not on this list at all any more: see the block
		// below where the receiver used to be registered.
		builder.Services.AddScoped<IScreenWakeLock, UnavailableScreenWakeLock>();
		builder.Services.AddScoped<INotificationService, NoopNotificationService>();

		// Registered so the shared layout resolves on this host too (§18.2). Nothing here ever
		// writes to the routing letterbox: no notification is raised, so none is ever tapped.
		builder.Services.AddSingleton<BlazorDLR.Shared.State.NotificationRouting>();
		builder.Services.AddScoped<BlazorDLR.Shared.State.CommentNotifier>();

		// Web IMediaPicker uses <InputFile> plumbed through a static holder - real
		// implementation in BlazorDLR.Web.Client/Services/BrowserMediaPicker.cs.
		builder.Services.AddScoped<IMediaPicker, BrowserMediaPicker>();

		// Downloads go the only way a browser offers: a Blob URL on a synthetic anchor click.
		builder.Services.AddScoped<IFileSaver, BrowserFileSaver>();

		// Social sign-in (§7.16). Scaffolded but not available today - real bindings
		// need registrations at the provider that happen with store submission (Phase 3
		// exit criterion). The Welcome page shows the buttons dimmed until IsAvailable
		// returns true.
		builder.Services.AddScoped<IExternalSignInProvider>(_ => new UnavailableExternalSignInProvider(ExternalProvider.Apple));
		builder.Services.AddScoped<IExternalSignInProvider>(_ => new UnavailableExternalSignInProvider(ExternalProvider.Google));

		// MapLibre GL JS + OSM is the base map here and on the phones alike (§4.5 v0.24,
		// §18.3) - the same shared class, because it needs no credential to differ over.
		// Base-map role only; every rider pin, marker and track goes into the Skia overlay.
		// Transient, not scoped: one interop instance per <RideMap>, because each instance
		// owns a JS map and a DotNetObjectReference bridge. Shared scoped, navigating
		// ride → marker composer let the outgoing RideMap's DisposeAsync tear down the
		// *incoming* one's bridge - the JS map lived on and every viewport and click then
		// died against "no tracked object with id N", so the map drew but nothing it
		// reported ever reached C#.
		builder.Services.AddTransient<IMapInterop, MapLibreInterop>();

		// Device-local preferences (§18.6), in browser localStorage.
		// RouteStyleState broadcasts to the Skia overlay so a change made on the ride's info
		// page is on the map before the rider gets back to it.
		builder.Services.AddScoped<IDeviceSettings, LocalStorageDeviceSettings>();
		builder.Services.AddScoped<BlazorDLR.Shared.State.RouteStyleState>();

		// Whether to offer the administration card on Settings (§14.6). The server decides - this
		// only caches the answer so the menu does not ask again on every visit.
		builder.Services.AddScoped<BlazorDLR.Shared.State.AdminAccess>();

		// Which tiles go under the map (§4.5). The offline option resolves to OpenStreetMap here -
		// this host has no pack store (§18.6), which MapSourceState reads off IOfflineStore.
		builder.Services.AddScoped<BlazorDLR.Shared.State.MapSourceState>();

		// The ride the nav rail's globe leads back to (§18.6), kept in localStorage so a
		// reloaded tab still knows which ride this browser is on.
		builder.Services.AddScoped<BlazorDLR.Shared.State.CurrentRideState>();

		// The unread count on the rail's thread item (§17.6), kept in localStorage so a reloaded
		// tab does not lose what the rider had not read yet.
		builder.Services.AddScoped<BlazorDLR.Shared.State.UnreadThreadState>();
		builder.Services.AddScoped<BlazorDLR.Shared.State.ConsentAskedState>();

		// Whether this browser has been shown the introduction (§18.6), kept in localStorage so a
		// reloaded tab is not shown it a second time.
		builder.Services.AddScoped<BlazorDLR.Shared.State.IntroTourState>();

		// Puts the adventure and the GPS back the way the last launch left them (§5.7, §18.6).
		// MainLayout injects it, so it has to resolve here, and it restores nothing in a browser:
		// a reloaded tab lost no receiver, because this host never had one to lose.
		builder.Services.AddScoped<BlazorDLR.Shared.State.LaunchRestore>();

		// What the server had to say at launch, and the announcements it pushes afterwards (§20).
		// The notifier holds the hub connection for the life of the tab, which is what lets an
		// announcement reach a rider who is not on a ride screen.
		builder.Services.AddScoped<BlazorDLR.Shared.State.StartupCheckState>();
		builder.Services.AddScoped<BlazorDLR.Shared.State.AnnouncementNotifier>();

		// No GPS on this host, and nothing standing in for one (§18.6).
		//
		// ILocationProvider, LocationUpdateRateState, TrackRecordingState, PrivateAreaState and
		// LocationBroadcastState are all absent, deliberately. A browser cannot deliver the
		// background, high-cadence fixes a live ride needs, so every one of them was a stub
		// answering "not supported" to screens that then had to explain themselves. The private
		// area is on the account now (§10.1) rather than on the device, so a browser could in
		// principle edit it - but it gates a receiver this host does not have, and the screen it
		// lives on is the one describing that receiver, so it stays with the rest of the set.
		//
		// The shared screens resolve the broadcaster with GetService rather than @inject and
		// render their no-receiver branch when it is missing, which is what lets the whole set
		// be gone rather than present and inert. Receiving is unaffected: other riders'
		// positions arrive over the hub as data (§5.3) and are drawn like any other.

		// The one confirm modal for every destructive action in the app. Mounted in
		// MainLayout; pages call `await Confirm.AskAsync(...)` in place of `window.confirm`.
		builder.Services.AddScoped<BlazorDLR.Shared.State.ConfirmService>();

		// PageNav's back arrow asks this whether stepping back lands inside the app. Counted
		// here rather than read from window.history.length, which also counts the pages the
		// tab visited before this one - see the type's remarks.
		builder.Services.AddScoped<BlazorDLR.Shared.State.NavigationHistory>();

		await builder.Build().RunAsync();
	}
}
