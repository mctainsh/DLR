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

		// GPS + push are the two seams Phase 1 explicitly defers on this host — both need
		// hardware to verify and platform-native code that cannot be checked in this
		// environment. Phase 2 wires the Android foreground service and iOS
		// CLLocationManager, with the recording pipeline behind them.
		builder.Services.AddScoped<BlazorDLR.Shared.Services.ILocationProvider, NoopLocationProvider>();
		builder.Services.AddScoped<BlazorDLR.Shared.Services.INotificationService, NoopNotificationService>();

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

		// Theme preference (§18.6): dark by default, persisted in MAUI Preferences.
		builder.Services.AddScoped<BlazorDLR.Shared.Services.IThemeService, PreferencesThemeService>();
		builder.Services.AddScoped<BlazorDLR.Shared.State.ThemeState>();

		// Device-local preferences that are not the theme (§18.6), also in MAUI Preferences.
		// RouteStyleState broadcasts to the Skia overlay so a change made on the ride's info
		// page is on the map before the rider gets back to it.
		builder.Services.AddScoped<BlazorDLR.Shared.Services.IDeviceSettings, PreferencesDeviceSettings>();
		builder.Services.AddScoped<BlazorDLR.Shared.State.RouteStyleState>();

		// The private area (§10.1, §18.6). This is the host that records and publishes fixes,
		// so this is the host where the gate matters: every fix goes through
		// PrivateAreaState.HidesLocation before it is stored or sent, and the state answers
		// "hide" until LoadAsync has read the device — see its remarks.
		builder.Services.AddScoped<BlazorDLR.Shared.State.PrivateAreaState>();

		// The one confirm modal for every destructive action in the app (§18.6).
		builder.Services.AddScoped<BlazorDLR.Shared.State.ConfirmService>();

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
