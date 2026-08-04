using BlazorDLR.Services;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Stubs;
using BlazorDLR.Shared.State;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace BlazorDLR;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		// URLs come from MauiConstants — a compile-time constant per platform with an
		// environment-variable override (DLR_API_BASE / DLR_HUB_URL). Never any API key:
		// MapKit's private .p8 stays on the server and reaches the client as a short-
		// lived JWT via GET /api/v1/maps/token (§14.2, §14.3).
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

		// Phase 0 (SharedFrontend.md §7): every seam is registered so the shared
		// pipeline compiles into both hosts and DI resolves; the real implementations
		// arrive in Phase 1. Stubs throw with a message naming Phase 0, so a screen
		// reaching for one before its dependency is built fails with the reason.
		//
		// Mobile-only implementations (ILocationProvider on Android/iOS, SecureStorage-
		// backed ITokenStore, MediaPicker, FCM/APNs, MapKit JS interop) each land in
		// their own file under BlazorDLR/Services/ or BlazorDLR/Platforms/ in Phase 1.
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

		// The base map differs per phone platform (§4.5 v0.21): Apple Maps on iOS,
		// Google Maps on Android. A host is not a shared component, so a per-target
		// conditional here is legitimate — the architecture rule that forbids #if in
		// shared components deliberately does not cover this file.
		//
		// Every rider pin, marker and track lands on the shared Skia overlay, which
		// resolves the same way on both platforms and is bound below.
#if IOS || MACCATALYST
		builder.Services.AddScoped<BlazorDLR.Shared.Services.IMapInterop, AppleMapsInterop>();
#elif ANDROID
		builder.Services.AddScoped<BlazorDLR.Shared.Services.IMapInterop, GoogleMapsInterop>();
		// The Google Maps browser API key. Phase 0 reads from a compile-time constant —
		// good enough for a spike, and §14.2 already forbids the constant being anything
		// but a placeholder in committed code. Phase 1 fetches from the server.
		builder.Services.AddSingleton(new GoogleMapsApiKey(MauiConstants.GoogleMapsKey));
#else
		// Windows / other MAUI targets — the desktop head is not shipped, but the build
		// still needs to resolve. A stub keeps `dotnet build` on Windows honest.
		builder.Services.AddScoped<BlazorDLR.Shared.Services.IMapInterop, UninitialisedMapInterop>();
#endif

		// Theme preference (§18.6): dark by default, persisted in MAUI Preferences.
		builder.Services.AddScoped<BlazorDLR.Shared.Services.IThemeService, PreferencesThemeService>();
		builder.Services.AddScoped<BlazorDLR.Shared.State.ThemeState>();

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
