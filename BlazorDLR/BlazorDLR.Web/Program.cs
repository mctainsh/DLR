using System.Reflection;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Stubs;
using BlazorDLR.Shared.State;
using BlazorDLR.Web.Components;
using BlazorDLR.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using DLR.Server;
using DLR.Server.Api;
using DLR.Server.Comments;
using DLR.Server.Data;
using DLR.Server.Hubs;
using DLR.Server.Identity;
using DLR.Server.Maintenance;
using DLR.Server.Maps;
using DLR.Server.Markers;
using DLR.Server.Moderation;
using DLR.Server.Photos;
using DLR.Server.Positions;
using DLR.Server.Rides;
using DLR.Server.Tracks;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// In every environment, not just Development, which is where the default puts it.
//
// A captive-dependency bug — a singleton holding something scoped — is a startup failure
// or it is a heisenbug: the scoped service is captured by the first request to build it
// and then quietly reused forever, which for anything holding a DbContext means one
// connection shared across every caller. The default arrangement means the whole graph is
// only checked on a developer's machine, and the test host runs as "Testing".
//
// It is a one-off cost at boot, and it has already caught one.
builder.Host.UseDefaultServiceProvider(options =>
{
	options.ValidateScopes = true;
	options.ValidateOnBuild = true;
});

// TimeProvider is registered from day one. Every timing-dependent rule in the
// project — token lifespans, the flush interval, the wind-down sweep, the
// 180-day inactivity horizon — resolves it, so tests advance a fake clock
// rather than sleeping. Retrofitting this later is miserable (§10.4).
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddDbContext<DlrDbContext>(options =>
	options.UseDlr(builder.Configuration.GetConnectionString("Dlr")));

builder.Services.AddDlrIdentity();
builder.Services.AddDlrAbuseControls(builder.Configuration);

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
builder.Services.Configure<BlobStoreOptions>(builder.Configuration.GetSection(BlobStoreOptions.Section));
builder.Services.AddSingleton<IBlobStore, FileSystemBlobStore>();
builder.Services.Configure<TrackImportOptions>(builder.Configuration.GetSection(TrackImportOptions.Section));
builder.Services.AddScoped<TrackStore>();
builder.Services.Configure<RideJoinOptions>(builder.Configuration.GetSection(RideJoinOptions.Section));
builder.Services.AddScoped<RideNotifications>();
builder.Services.AddScoped<PositionStore>();

// The cache is a singleton because it *is* the live state; the writer is scoped because it
// borrows the request context's connection. The flush service bridges the two through a scope
// of its own — a background service holding a scoped context is the captive dependency the
// container validation above exists to catch.
builder.Services.Configure<RideOptions>(builder.Configuration.GetSection(RideOptions.Section));
builder.Services.AddSingleton<RiderPositionCache>();
builder.Services.AddScoped<IPositionWriter, PositionWriter>();
builder.Services.AddSingletonHostedService<PositionFlushService>();
builder.Services.AddSingletonHostedService<PositionCacheRehydrator>();

builder.Services.AddSingletonHostedService<SharingWindDownService>();

builder.Services.AddSignalR();
builder.Services.AddSingletonHostedService<RideBroadcastService>();
builder.Services.Configure<TrackEditOptions>(builder.Configuration.GetSection(TrackEditOptions.Section));
builder.Services.Configure<MarkerOptions>(builder.Configuration.GetSection(MarkerOptions.Section));

// The one image decode path (§16.4). Singleton and stateless — it holds the caps and nothing
// else, so a second registration anywhere would be a second ingest, which is the thing the
// architecture test exists to prevent.
builder.Services.Configure<PhotoOptions>(builder.Configuration.GetSection(PhotoOptions.Section));
builder.Services.AddSingleton<ImageIngest>();

builder.Services.Configure<CommentOptions>(builder.Configuration.GetSection(CommentOptions.Section));

// Singleton because the dirty set *is* the pending broadcast, and hosted from the same instance so
// an endpoint marking a comment dirty and the timer draining it are the same object (§17.4).
builder.Services.AddSingletonHostedService<ReactionBroadcastService>();

// The one destructive timer (§7.11). Singleton and hosted from the same instance so an operator
// endpoint — or a test — driving one run drives the object the timer drives, not a second copy
// reading the same settings. Its defaults delete nothing until somebody turns DryRun off.
builder.Services.Configure<MaintenanceOptions>(builder.Configuration.GetSection(MaintenanceOptions.Section));
builder.Services.Configure<ModerationOptions>(builder.Configuration.GetSection(ModerationOptions.Section));
builder.Services.AddSingletonHostedService<NightlyMaintenanceService>();

// The browser's half of §7.4 (§7.5). Antiforgery covers exactly one endpoint — the cookie-to-
// access-token exchange — because that is the only place a cookie is presented as a credential,
// and the whole cost of choosing a cookie over localStorage is that one endpoint's CSRF exposure.
builder.Services.AddAntiforgery(options => options.HeaderName = "X-DLR-Antiforgery");
builder.Services.AddSingleton<WebSessionCookie>();
builder.Services.AddScoped<RegistrationService>();

builder.Services.Configure<MapKitOptions>(builder.Configuration.GetSection(MapKitOptions.Section));
builder.Services.AddSingleton<MapKitSigningKey>();

builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.Section));
builder.Services.Configure<AccountLinkOptions>(builder.Configuration.GetSection(AccountLinkOptions.Section));
builder.Services.AddDlrJwtBearer();

builder.Services.AddSingleton(BuildInformation.ForAssembly(Assembly.GetExecutingAssembly()));
builder.Services.Configure<AboutOptions>(builder.Configuration.GetSection(AboutOptions.Section));
builder.Services.Configure<HealthOptions>(builder.Configuration.GetSection(HealthOptions.Section));

// The API surface is served by attribute-routed controllers (§5).
builder.Services.AddControllers();

// Add Blazor services (server + WASM interactive).
builder.Services.AddRazorComponents()
	.AddInteractiveWebAssemblyComponents();

// Device-specific services used by the BlazorDLR.Shared project.
builder.Services.AddSingleton<IFormFactor, FormFactor>();

// Phase 0 (SharedFrontend.md §7): the SSR pass renders shared components before the WASM
// client boots, so this host needs its own DI for every seam in §4 of that document — even
// though the interactive session will re-resolve them against BlazorDLR.Web.Client's DI.
//
// IApiClient is bound to an in-process shim that answers /api/v1/about directly from the
// same services the controller reads. Making the SSR pass call itself over HTTP for a value
// it already has in memory is the kind of waste worth avoiding, and the SourceOfferFooter is
// the one API call the shell needs to make on this pass.
builder.Services.AddScoped<IApiClient, InProcessAboutApiClient>();
builder.Services.AddScoped<IRideHubClient, ThrowingRideHubClient>();
builder.Services.AddScoped<IRideRepository, HttpRideRepository>();
builder.Services.AddScoped<ITrackRepository, HttpTrackRepository>();
builder.Services.AddScoped<ITokenStore, CookieBackedTokenStore>();
builder.Services.AddScoped<ILocationProvider, NoopLocationProvider>();
builder.Services.AddScoped<INotificationService, NoopNotificationService>();
builder.Services.AddScoped<IMediaPicker, NoopMediaPicker>();
// Transient to match the interactive hosts: one interop per <RideMap>. Stateless here, but a
// lifetime that differs between the prerender and the client it hands off to is a trap.
builder.Services.AddTransient<IMapInterop, UninitialisedMapInterop>();
builder.Services.AddScoped<IExternalSignInProvider>(_ => new UnavailableExternalSignInProvider(ExternalProvider.Apple));
builder.Services.AddScoped<IExternalSignInProvider>(_ => new UnavailableExternalSignInProvider(ExternalProvider.Google));

// §18.6: MainLayout injects ThemeState so it can set `data-theme` on the outer element.
// The SSR pass has no browser localStorage and no MAUI Preferences to read from — the
// in-memory stub answers Dark (the design default) and the client's own DI takes over
// once WASM boots and rehydrates from localStorage.
builder.Services.AddScoped<IThemeService, InMemoryThemeService>();
builder.Services.AddScoped<BlazorDLR.Shared.State.ThemeState>();

// Device-local preferences (§18.6). Same story as the theme above: the prerender has no
// device to read from, so it renders the shipped RouteStyle defaults and the WASM client
// re-resolves against localStorage once it boots.
builder.Services.AddScoped<IDeviceSettings, InMemoryDeviceSettings>();
builder.Services.AddScoped<BlazorDLR.Shared.State.RouteStyleState>();

// The private area (§10.1, §18.6). Registered for the prerender because the profile screen
// injects it; the SSR pass reads an empty in-memory store, so it renders "no private area"
// and the WASM client re-resolves against localStorage the moment it boots. Nothing on this
// host ever writes it — the value is the device's and the server is not told it exists.
builder.Services.AddScoped<BlazorDLR.Shared.State.PrivateAreaState>();

// The one confirm modal is mounted in MainLayout, which renders in the SSR pass too;
// registering here keeps prerender from throwing on the ConfirmDialog @inject.
builder.Services.AddScoped<BlazorDLR.Shared.State.ConfirmService>();

// Auth state for the SSR pass. Shared pages inject AuthState directly (Welcome) and via
// AuthenticationStateProvider (Home, AuthorizeView), so both must resolve on this host
// too — otherwise the prerender crashes before the WASM client boots and takes over
// with the client's own scoped instance. The SSR pass has no refresh cookie the token
// store can read, so the principal starts anonymous and NotAuthorized wins.
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<AuthState>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<AuthState>());

var app = builder.Build();

// First, and before the `--migrate` branch below, because that branch needs a database and this
// is the setting that says where one is. It previously surfaced as an Npgsql stack trace on the
// first request that touched a table.
RequiredSettings.ValidateConnectionString(app.Configuration);

// `--migrate` applies the schema and exits, without starting Kestrel (§9).
//
// A one-shot run of the same image rather than a Migrate() call on the way up. Migrating at
// startup couples "is this server ready" to "has the schema moved", which is the coupling that
// makes a rolling deploy or a second container an outage — and it also means a failed migration
// presents as a crash loop rather than as a step that failed. Compose runs this to completion
// before the server container starts, and /healthz reports the schema so that forgetting it
// entirely is loud rather than silent (§9.1).
//
// The signing key is deliberately not validated first: applying a schema needs a database, not an
// ability to issue tokens, and refusing to migrate over a missing Auth:SigningKey would make the
// recovery order impossible for anybody setting a server up for the first time.
if (args.Contains("--migrate", StringComparer.Ordinal))
{
	await using AsyncServiceScope migrationScope = app.Services.CreateAsyncScope();

	await migrationScope.ServiceProvider.GetRequiredService<DlrDbContext>().Database.MigrateAsync();

	return;
}

// Refuses to start on a missing, short, or committed signing key (§7.4). After Build
// rather than before it, because that is the first point at which the configuration is
// final — under the minimal hosting model a host can still contribute sources while the
// builder is running, and validating early would judge a half-assembled configuration.
// Still before the first request, so the failure is "this deployment is misconfigured"
// rather than a 500 on somebody's first sign-in attempt.
SigningKeySource.Validate(app.Configuration, app.Environment.ContentRootPath);

// Alongside the signing key and for the same reason, and deliberately not before the
// `--migrate` branch: applying a schema needs a database, not somewhere to put photographs.
// An unset value is not inert here — it resolves relative to the working directory, so the
// server comes up perfectly happily and writes uploads into the source tree (§9.1).
RequiredSettings.ValidateBlobRoot(app.Configuration);

// Before anything that reads an address. Every per-address rule in §7.8 depends on
// this having run, and the ladder in particular breaks registration outright without
// it — every signup would look like it came from Caddy.
app.UseForwardedHeaders();

// The points endpoint sends ~200 KB of encoded polyline for a long ride (§15.5). Caddy
// compresses in production; this makes the same true of a direct request.
app.UseResponseCompression();

if (app.Environment.IsDevelopment())
{
	app.UseWebAssemblyDebugging();
}
else
{
	app.UseExceptionHandler("/Error");
	app.UseHsts();
}

// Re-executing a bodyless error response into the razor pipeline is right for a browser
// navigation and wrong for every API caller, so it is scoped off /api/* for the same reason
// the antiforgery branch below is. The re-execute rewrites the request to GET-or-POST
// /not-found, which loses the original path and — worse — changes the status the client sees:
// a 401 on `PUT /api/v1/me/profile` came back as 405 (the razor endpoint has no PUT), and a
// 401 or 403 on any API POST came back as 400 (antiforgery runs on the re-executed path,
// which no longer starts with /api). An API client must receive the status the endpoint chose.
app.UseWhen(
	static context => !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase),
	branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));

app.UseAuthentication();
app.UseAuthorization();

// Antiforgery middleware runs on the razor-components pipeline (interactive rendering
// annotates endpoints with anti-forgery metadata and the framework will not start without
// the middleware). It is skipped for /api/* because §7.5 scopes CSRF to exactly one API
// endpoint — the cookie-to-access-token exchange — and that endpoint calls
// IAntiforgery.ValidateRequestAsync explicitly inside WebAuthController.TokenAsync. Letting
// the middleware run over /api/* rejects legitimate multipart uploads (§15.2, §16.4)
// regardless of [IgnoreAntiforgeryToken], which is an MVC filter the middleware does not
// consult.
app.UseWhen(
	static context => !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase),
	branch => branch.UseAntiforgery());

// The MVC controllers replace the previous MapXxx endpoints (§5).
app.MapControllers();

// CloseOnAuthenticationExpiration is left at its default of false, deliberately (§7.6). SignalR
// validates the token when the connection opens; closing on expiry would kill a two-hour ride's
// connection every fifteen minutes — the access token's lifetime, which has nothing to do with
// whether the rider is still on the bike. The client's AccessTokenProvider supplies a fresh token
// on *reconnect*, which is where rotation belongs. Written out so nobody later "fixes" it.
app.MapHub<RideHub>(RideHub.Path);

app.MapStaticAssets();

// The razor host serves the WASM shell for every route. Auth on this app lives entirely in the
// WASM heap (§7.4, §18.5) — the server has no cookie or bearer on a razor page GET, so applying
// endpoint-level [Authorize] would 401 the shell before the client could boot. AuthorizeRouteView
// (in Routes.razor) is the sole gate; it runs client-side and redirects an anonymous user to
// /welcome. AllowAnonymous strips the endpoint metadata the framework otherwise infers from the
// [Authorize] attributes on the page components themselves.
app.MapRazorComponents<App>()
	.AddInteractiveWebAssemblyRenderMode()
	.AddAdditionalAssemblies(
		typeof(BlazorDLR.Shared._Imports).Assembly,
		typeof(BlazorDLR.Web.Client._Imports).Assembly)
	.AllowAnonymous();

app.Run();

/// <summary>
/// Named so <c>WebApplicationFactory&lt;Program&gt;</c> has an entry point to bind to.
/// </summary>
public partial class Program;
