using BlazorDLR.Services;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace BlazorDLR;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		// URLs come from MauiConstants — a compile-time constant per platform with an
		// environment-variable override (DLR_API_BASE / DLR_HUB_URL). Never any API key,
		// and since v0.24 there is no map credential on the client at all: MapLibre over
		// OSM needs none (§4.5, §14.2, §14.3).
		string apiBase = MauiConstants.ResolveApiBase();
		string hubUrl = MauiConstants.ResolveHubUrl(apiBase);


		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		// Device-specific services used by the BlazorDLR.Shared project.
		builder.Services.AddSingleton<IFormFactor, FormFactor>();

		// TimeProvider from day one (§10.4). Every timing-dependent decision in the client —
		// token cache expiry, reconnect windows, staleness gates — resolves it, so tests
		// advance a fake clock rather than sleeping.
		builder.Services.AddSingleton(TimeProvider.System);

		// Every seam in SharedFrontend.md §4 is registered so the shared pipeline compiles
		// into this host and DI resolves. Where the capability does not exist here, the
		// binding comes from BlazorDLR.Shared/Services/Platform/ — those report their
		// unavailability through an IsSupported/IsAvailable flag, or throw with a message
		// naming which host cannot answer and what handles the call instead.
		//
		// Mobile-only implementations (ILocationProvider on Android/iOS, SecureStorage-
		// backed ITokenStore, MediaPicker, FCM/APNs) each land in their own file under
		// BlazorDLR/Services/ or BlazorDLR/Platforms/ in Phase 1. The map is no longer
		// among them — v0.24 moved it into BlazorDLR.Shared for every host.
		// Fully-qualified names on the shared-DI registrations: MAUI ships its own
		// IMediaPicker in Microsoft.Maui.Media, so the shared abstraction has to be
		// qualified here to disambiguate. Every other seam has a unique name.
		//
		// The API base address is a compile-time constant for now — Phase 1 replaces
		// this with a configuration read so a shipped build can point at production
		// rather than the developer's laptop. For the Phase 0 spike, running the
		// server locally with `dotnet run --project BlazorDLR.Web` is enough.
		//
		// The HttpClient is wrapped in BearerAuthHandler, which reads the current access
		// token from AuthState and refreshes on 401 with single-flight (§7.4, §18.5). The
		// handler is registered as a scoped service so it captures the same AuthState the
		// UI is bound to.
		builder.Services.AddScoped<BearerAuthHandler>();
		builder.Services.AddScoped(sp =>
		{
			BearerAuthHandler handler = sp.GetRequiredService<BearerAuthHandler>();
			handler.InnerHandler = new HttpClientHandler();
			return new HttpClient(handler) { BaseAddress = new Uri(apiBase) };
		});
		builder.Services.AddScoped<HttpApiClient>();
		builder.Services.AddScoped<BlazorDLR.Shared.Services.IApiClient>(
			sp => sp.GetRequiredService<HttpApiClient>());

		// Auth state — reads the refresh token from SecureStorage on demand, holds the
		// access token in memory, and broadcasts sign-in / sign-out to AuthorizeView (§7.4).
		builder.Services.AddScoped<AuthState>();
		builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<AuthState>());
		builder.Services.AddAuthorizationCore();

		// Real SignalR hub client (§5.3). The token provider now reads from SecureStorage
		// via AuthState — a valid access token if we have one, null otherwise.
		builder.Services.AddScoped<BlazorDLR.Shared.Services.IRideHubClient>(sp => new SignalRRideHubClient(
			new Uri(hubUrl),
			async ct => await sp.GetRequiredService<AuthState>().GetOrRefreshAccessTokenAsync(ct)));

		// Phase 1 repositories: HTTP passthrough on both hosts. Phase 2 replaces the
		// mobile bindings with SQLite-backed ones that hit local storage first (§4.4).
		builder.Services.AddScoped<BlazorDLR.Shared.Services.IRideRepository, HttpRideRepository>();
		builder.Services.AddScoped<BlazorDLR.Shared.Services.ITrackRepository, HttpTrackRepository>();

		// SecureStorage → Keychain / Keystore, this-device-only accessibility (§7.4).
		builder.Services.AddScoped<BlazorDLR.Shared.Services.ITokenStore, SecureStorageTokenStore>();

		// The device's own copy of what the server said (§4.4). Files under the app's data
		// directory rather than Preferences: one ride's planned routes are tens of kilobytes of
		// encoded polyline, and the preference store keeps everything it holds in memory.
		//
		// This is the host that has one. Both browser hosts bind UnavailableOfflineStore and
		// answer "nothing stored" to every read (§18.6) — the phone is the thing in the rider's
		// pocket in a dead zone, which is the whole case for keeping a copy.
		builder.Services.AddScoped<BlazorDLR.Shared.Services.IOfflineStore, FileOfflineStore>();
		builder.Services.AddScoped<BlazorDLR.Shared.Services.RideSnapshotCache>();

		// Downloaded map archives (§4.5, §13 Q26). A separate seam from IOfflineStore above, and the
		// difference is what reads them: that one holds a few kilobytes of JSON read whole by C#,
		// these are hundreds of megabytes read in ranges by MapLibre inside the WebView.
		//
		// Singletons, both. The store is stateless but the server owns a bound loopback port and a
		// per-run secret, and a second instance would be a second port serving the same files —
		// which is also why it is disposed with the app rather than with a scope.
		builder.Services.AddSingleton<BlazorDLR.Shared.Services.IMapPackStore, FileMapPackStore>();
		builder.Services.AddSingleton<BlazorDLR.Shared.Services.IMapPackServer,
			BlazorDLR.Shared.Services.Platform.LoopbackMapPackServer>();

		// The downloader owns an HttpClient of its own rather than taking the registered one, which
		// carries BearerAuthHandler — attaching the rider's access token to a request aimed at a
		// host they pasted would hand their session to a stranger's web server (§18.5).
		builder.Services.AddSingleton(sp => new BlazorDLR.Shared.Services.MapPackDownloader(
			sp.GetRequiredService<BlazorDLR.Shared.Services.IMapPackStore>(),
			BlazorDLR.Shared.Services.MapPackDownloader.CreateCredentialFreeClient()));

		// What is on offer to download (§4.2). Its own credential-free client for the same reason as
		// the downloader's: the catalogue is a static file on a host that is not the API, and the
		// registered client would attach the rider's access token to the request.
		builder.Services.AddSingleton(_ => new BlazorDLR.Shared.Services.MapPackCatalogue(
			BlazorDLR.Shared.Services.MapPackCatalogue.CreateCredentialFreeClient(),
			new Uri(BlazorDLR.Shared.Services.MapPackCatalogue.DefaultUrl)));

		builder.Services.AddScoped<BlazorDLR.Shared.State.MapPackState>();

		// GPS (§4.3). The platform providers live under Platforms/, one per target: an Android
		// foreground service over the fused location provider, and CLLocationManager on iOS.
		// Both are singletons — each owns one receiver, and a second instance would be a second
		// subscription to the same hardware, which on Android is a second foreground service.
		//
		// The #if is the one place in the app allowed to have one: this is the MAUI head, where
		// the two targets genuinely have different classes. Everything above the seam sees
		// ILocationProvider (§18.2, and UiLayeringRules keeps BlazorDLR.Shared free of it).
#if ANDROID
		builder.Services.AddSingleton<BlazorDLR.Shared.Services.ILocationProvider, BlazorDLR.Platforms.Android.Location.AndroidLocationProvider>();
#elif IOS || MACCATALYST
		builder.Services.AddSingleton<BlazorDLR.Shared.Services.ILocationProvider, BlazorDLR.Platforms.Apple.Location.AppleLocationProvider>();
#else
		builder.Services.AddSingleton<BlazorDLR.Shared.Services.ILocationProvider, NoopLocationProvider>();
#endif

		// The screen stays on while the live map is being read (§4.3). Singleton for the same
		// reason the receiver is: there is one window and one flag on it, so one thing counts who
		// has asked for it. Both browser hosts bind the stub — the case for it is a phone on a bar
		// mount, not a laptop with a tab open (§18.6).
		builder.Services.AddSingleton<BlazorDLR.Shared.Services.IScreenWakeLock, DeviceDisplayScreenWakeLock>();

		// Push is the seam still deferred on this host — it needs FCM/APNs registrations that
		// happen with store submission.
		builder.Services.AddScoped<BlazorDLR.Shared.Services.INotificationService, NoopNotificationService>();

		// The accuracy profile this device records at (§4.2), in MAUI Preferences.
		builder.Services.AddScoped<BlazorDLR.Shared.State.GpsProfileState>();

		// The rider's own track (§15.1). This is the host that actually records one: the pump
		// below offers it every fix, and the Location screen is where it is saved or thrown away.
		builder.Services.AddScoped<BlazorDLR.Shared.State.TrackRecordingState>();

		// The one place a fix travels from the receiver to the ride (§5.7). Scoped, like every
		// other state the UI binds to: the ride screens turn it on and off, and it holds the
		// device's set of reasons to be broadcasting.
		builder.Services.AddScoped<BlazorDLR.Shared.State.LocationBroadcastState>();

		// MAUI's MediaPicker / FilePicker back the shared IMediaPicker on the phone (§16.4).
		builder.Services.AddScoped<BlazorDLR.Shared.Services.IMediaPicker, MauiMediaPicker>();

		// Social sign-in (§7.16). Scaffolded but not available today — real bindings
		// need registrations at the provider that happen with store submission (Phase 3
		// exit criterion). The Welcome page shows the buttons dimmed until IsAvailable
		// returns true.
		builder.Services.AddScoped<BlazorDLR.Shared.Services.IExternalSignInProvider>(_ =>
			new UnavailableExternalSignInProvider(BlazorDLR.Shared.Services.ExternalProvider.Apple));
		builder.Services.AddScoped<BlazorDLR.Shared.Services.IExternalSignInProvider>(_ =>
			new UnavailableExternalSignInProvider(BlazorDLR.Shared.Services.ExternalProvider.Google));

		// One base map on every surface (§4.5 v0.24): MapLibre GL JS over OSM tiles, the
		// same class the web hosts register. The per-target #if that used to live here is
		// gone with the providers it selected — MapKit JS needed a server-minted token and
		// Google Maps needed a browser API key in the bundle, and MapLibre needs neither,
		// so there is nothing left for a platform conditional to decide.
		//
		// Every rider pin, marker and track lands on the shared Skia overlay, bound below.
		// Transient, not scoped: one interop instance per <RideMap>. Each owns a JS map and
		// a DotNetObjectReference bridge, so a shared instance lets one map's teardown
		// dispose another's bridge — see the note in BlazorDLR.Web.Client/Program.cs.
		builder.Services.AddTransient<BlazorDLR.Shared.Services.IMapInterop, BlazorDLR.Shared.Services.MapLibreInterop>();

		// Device-local preferences (§18.6), in MAUI Preferences.
		// RouteStyleState broadcasts to the Skia overlay so a change made on the ride's info
		// page is on the map before the rider gets back to it.
		builder.Services.AddScoped<BlazorDLR.Shared.Services.IDeviceSettings, PreferencesDeviceSettings>();
		builder.Services.AddScoped<BlazorDLR.Shared.State.RouteStyleState>();

		// Which tiles go under the map (§4.5). Scoped like every other device preference, and
		// scoped is what lets the settings screen's preview restyle as the rider edits it —
		// RideMap listens to the same instance the settings page writes.
		builder.Services.AddScoped<BlazorDLR.Shared.State.MapSourceState>();

		// The private area (§10.1, §18.6). This is the host that records and publishes fixes,
		// so this is the host where the gate matters: every fix goes through
		// PrivateAreaState.HidesLocation before it is stored or sent, and the state answers
		// "hide" until LoadAsync has read the device — see its remarks.
		builder.Services.AddScoped<BlazorDLR.Shared.State.PrivateAreaState>();

		// The ride the nav rail's globe leads back to (§18.6), in MAUI Preferences. This is the
		// host it matters most on: an app the OS reclaims mid-ride relaunches on the home screen
		// with the ride still running, and the globe is what puts the rider back on it.
		builder.Services.AddScoped<BlazorDLR.Shared.State.CurrentRideState>();

		// The one confirm modal for every destructive action in the app (§18.6).
		builder.Services.AddScoped<BlazorDLR.Shared.State.ConfirmService>();

		// PageNav's back arrow asks this whether stepping back lands inside the app. On the
		// phone this is the count that matters: the WebView has no browser back button, so
		// the arrow in the title bar is the only way back short of the rail.
		builder.Services.AddScoped<BlazorDLR.Shared.State.NavigationHistory>();

		// The Skia overlay is a Blazor component (SkiaMapOverlay.razor), not a DI service —
		// RideMap.razor renders it directly. No registration needed.

		builder.Services.AddMauiBlazorWebView();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
