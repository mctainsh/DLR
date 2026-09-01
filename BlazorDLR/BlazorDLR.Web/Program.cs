using System.Reflection;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using BlazorDLR.Web.Components;
using BlazorDLR.Web.Services;
using DLR.Server;
using DLR.Server.Account;
using DLR.Server.Admin;
using DLR.Server.Announcements;
using DLR.Server.Api;
using DLR.Server.Comments;
using DLR.Server.Data;
using DLR.Server.Diagnostics;
using DLR.Server.Hubs;
using DLR.Server.Identity;
using DLR.Server.Maintenance;
using DLR.Server.Markers;
using DLR.Server.Moderation;
using DLR.Server.Photos;
using DLR.Server.Positions;
using DLR.Server.Rides;
using DLR.Server.Tracks;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

WebApplication? app = null;
try
{
	// Settings are read once, at startup. The default host loads appsettings.json, the environment
	// overlay and user secrets with reloadOnChange:true, so an edit re-binds a running process; the
	// switch turns that off. It goes ahead of the caller's arguments because it has to be a
	// key=value the host reads before it loads the files, and because a valueless argument after it
	// (`--migrate`) would otherwise swallow it as its value.
	var builder = WebApplication.CreateBuilder(["--hostBuilder:reloadConfigOnChange=false", .. args]);

	// In every environment, not just Development, which is where the default puts it.
	//
	// A captive-dependency bug - a singleton holding something scoped - is a startup failure
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
	// project - token lifespans, the flush interval, the nightly sweeps, the
	// 180-day inactivity horizon - resolves it, so tests advance a fake clock
	// rather than sleeping. Retrofitting this later is miserable (§10.4).
	builder.Services.AddSingleton(TimeProvider.System);

	builder.Services.AddDbContext<DlrDbContext>(options =>
		options.UseDlr(builder.Configuration.GetConnectionString("Dlr")));

	builder.Services.AddDlrIdentity();
	builder.Services.AddDlrAbuseControls(builder.Configuration);

	// The administration roster, its policy, and the log file the log screen reads (§14.6). The
	// roster is a list of usernames in configuration rather than a column, so who may see this is
	// set by whoever controls the deployment and not by anybody using the app.
	builder.Services.AddDlrAdmin(builder.Configuration);
	builder.Logging.AddDlrFileLog();

	// What gets written to that file: the startup lines that say which build this is and where it is
	// writing, and the recorder every significant event and unhandled exception goes through (§14.6).
	builder.Services.AddDlrDiagnostics();

	builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
	builder.Services.Configure<BlobStoreOptions>(builder.Configuration.GetSection(BlobStoreOptions.Section));
	builder.Services.AddSingleton<IBlobStore, FileSystemBlobStore>();
	builder.Services.Configure<TrackImportOptions>(builder.Configuration.GetSection(TrackImportOptions.Section));
	builder.Services.AddScoped<TrackStore>();
	builder.Services.Configure<RideJoinOptions>(builder.Configuration.GetSection(RideJoinOptions.Section));
	builder.Services.AddScoped<RideNotifications>();
	builder.Services.AddScoped<RideMembers>();
	builder.Services.AddScoped<PositionStore>();

	// The cache is a singleton because it *is* the live state; the writer is scoped because it
	// borrows the request context's connection. The flush service bridges the two through a scope
	// of its own - a background service holding a scoped context is the captive dependency the
	// container validation above exists to catch.
	builder.Services.Configure<RideOptions>(builder.Configuration.GetSection(RideOptions.Section));
	builder.Services.AddSingleton<RiderPositionCache>();

	// Same lifetime and the same reasoning as the position cache: it is live presence, not a record.
	// Deliberately never a column - a durable log of when each account was at home would be a weaker
	// copy of the very thing the private area withholds (§10.1).
	builder.Services.AddSingleton<RiderPrivacyCache>();

	// Counts fixes on their way past, for the administration screen (§14.6). A singleton for the
	// cache's reason - it is live state - and deliberately never on the write path: the flush drains
	// it, so a fix arriving never waits on a counter.
	builder.Services.AddSingleton<PositionActivityMeter>();
	builder.Services.AddScoped<IPositionCounter, PositionCounter>();
	builder.Services.AddSingletonHostedService<PositionCounterFlushService>();

	builder.Services.AddSignalR();

	// Which connections each account holds. A singleton for the position cache's reason - it is
	// live state - and the only way an ended membership can reach a connection that is already in
	// a ride's group, since JoinRide's check runs once and nothing re-runs it (§5.2).
	builder.Services.AddSingleton<RideConnections>();
	builder.Services.AddSingletonHostedService<RideBroadcastService>();
	builder.Services.Configure<TrackEditOptions>(builder.Configuration.GetSection(TrackEditOptions.Section));
	builder.Services.Configure<MarkerOptions>(builder.Configuration.GetSection(MarkerOptions.Section));

	// The one image decode path (§16.4). Singleton and stateless - it holds the caps and nothing
	// else, so a second registration anywhere would be a second ingest, which is the thing the
	// architecture test exists to prevent.
	builder.Services.Configure<PhotoOptions>(builder.Configuration.GetSection(PhotoOptions.Section));
	builder.Services.AddSingleton<ImageIngest>();

	builder.Services.Configure<CommentOptions>(builder.Configuration.GetSection(CommentOptions.Section));

	// Singleton because the dirty set *is* the pending broadcast, and hosted from the same instance so
	// an endpoint marking a comment dirty and the timer draining it are the same object (§17.4).
	builder.Services.AddSingletonHostedService<ReactionBroadcastService>();

	// Sends an announcement the moment it goes live (§20.3). Singleton and hosted from the same
	// instance for the reason above: the window it has already swept is state on the object, and a
	// second copy would re-send everything the first one had sent.
	builder.Services.AddSingletonHostedService<AnnouncementBroadcastService>();

	// The one destructive timer (§7.11). Singleton and hosted from the same instance so an operator
	// endpoint - or a test - driving one run drives the object the timer drives, not a second copy
	// reading the same settings. Its defaults delete nothing until somebody turns DryRun off.
	builder.Services.Configure<MaintenanceOptions>(builder.Configuration.GetSection(MaintenanceOptions.Section));
	builder.Services.Configure<ModerationOptions>(builder.Configuration.GetSection(ModerationOptions.Section));
	builder.Services.AddSingletonHostedService<NightlyMaintenanceService>();

	// The browser's half of §7.4 (§7.5). Antiforgery covers exactly one endpoint - the cookie-to-
	// access-token exchange - because that is the only place a cookie is presented as a credential,
	// and the whole cost of choosing a cookie over localStorage is that one endpoint's CSRF exposure.
	builder.Services.AddAntiforgery(options => options.HeaderName = "X-DLR-Antiforgery");
	builder.Services.AddSingleton<WebSessionCookie>();
	builder.Services.AddScoped<RegistrationService>();

	// The one erasure, shared by the rider's own delete and the administrator's (§6.3, §14.6).
	builder.Services.AddScoped<AccountDeletion>();

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

	// SharedFrontend.md §4: the SSR pass renders shared components before the WASM client boots,
	// so this host needs its own DI for every seam in that section - even though the interactive
	// session will re-resolve them against BlazorDLR.Web.Client's DI. Where a seam has no
	// meaningful answer during a static render, the binding comes from
	// BlazorDLR.Shared/Services/Platform/.
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
	// The ride screens resolve a device-local cache of the ride (§4.4). This host has no device to
	// keep one on and the browser it hands off to keeps none either (§18.6), so both bind the store
	// that answers "nothing stored" - which is what lets the shared screens ask unconditionally.
	builder.Services.AddScoped<IOfflineStore, UnavailableOfflineStore>();
	builder.Services.AddScoped<RideSnapshotCache>();
	// Nor any downloaded map archive, nor a server for one (§18.6) - registered so the prerender
	// resolves the map seam rather than throwing before WASM can boot.
	builder.Services.AddScoped<IMapPackStore, UnavailableMapPackStore>();
	builder.Services.AddScoped<IMapPackServer, UnavailableMapPackServer>();
	builder.Services.AddScoped(sp => new MapPackDownloader(
		sp.GetRequiredService<IMapPackStore>(),
		MapPackDownloader.CreateCredentialFreeClient()));
	// The catalogue of packs on offer (§4.2). Registered for the same reason as the two above and read
	// by nobody here: MapPackState refuses to fetch it on a host that could not store the result, so
	// the prerender resolves the service and never sends the request.
	builder.Services.AddScoped(_ => new MapPackCatalogue(
		MapPackCatalogue.CreateCredentialFreeClient(),
		new Uri(MapPackCatalogue.DefaultUrl)));
	builder.Services.AddScoped<BlazorDLR.Shared.State.MapPackState>();
	// The live map asks for the screen to stay on (§4.3). There is no screen on this host, and the
	// browser it hands off to binds the same stub (§18.6).
	builder.Services.AddScoped<IScreenWakeLock, UnavailableScreenWakeLock>();
	builder.Services.AddScoped<INotificationService, NoopNotificationService>();
	// The notification seams (§17.6). Registered because the shared pipeline compiles into the
	// prerender and MainLayout injects them; neither does anything on this host, and the lifetimes are
	// the ones the mobile head uses so a scope-validation difference between the two cannot hide here.
	builder.Services.AddSingleton<BlazorDLR.Shared.State.NotificationRouting>();
	builder.Services.AddScoped<BlazorDLR.Shared.State.CommentNotifier>();
	builder.Services.AddScoped<IMediaPicker, NoopMediaPicker>();
	// No rider and no click during the prerender; the interactive host binds the real saver.
	builder.Services.AddScoped<IFileSaver, UnavailableFileSaver>();
	// Transient to match the interactive hosts: one interop per <RideMap>. Stateless here, but a
	// lifetime that differs between the prerender and the client it hands off to is a trap.
	builder.Services.AddTransient<IMapInterop, UninitialisedMapInterop>();
	builder.Services.AddScoped<IExternalSignInProvider>(_ => new UnavailableExternalSignInProvider(ExternalProvider.Apple));
	builder.Services.AddScoped<IExternalSignInProvider>(_ => new UnavailableExternalSignInProvider(ExternalProvider.Google));

	// Device-local preferences (§18.6). The prerender has no device to read from, so it renders
	// the shipped RouteStyle defaults and the WASM client re-resolves against localStorage once
	// it boots.
	builder.Services.AddScoped<IDeviceSettings, InMemoryDeviceSettings>();
	builder.Services.AddScoped<BlazorDLR.Shared.State.RouteStyleState>();

	// Whether to offer the administration card on Settings (§14.6). The server decides - this only
	// caches the answer so the menu does not ask again on every visit.
	builder.Services.AddScoped<BlazorDLR.Shared.State.AdminAccess>();

	// Which tiles go under the map (§4.5). RideMap injects it, so it has to resolve here or the
	// prerender throws before WASM can boot. The in-memory store answers "nothing chosen", which is
	// OpenStreetMap - the same map the client re-resolves to once it reads localStorage.
	builder.Services.AddScoped<BlazorDLR.Shared.State.MapSourceState>();

	// The ride the nav rail's globe leads back to (§18.6). NavMenu renders in the SSR pass, so
	// this has to resolve here or the prerender throws before WASM can boot. The in-memory store
	// answers "no ride", which is the list - the honest destination for a render that cannot see
	// the device - and the client re-resolves against localStorage the moment it takes over.
	builder.Services.AddScoped<BlazorDLR.Shared.State.CurrentRideState>();

	// The unread count on the rail's thread item (§17.6). Same reasoning: NavMenu injects it, so it
	// has to resolve here. It counts posts off the hub, and this host's hub client throws on every
	// call - so the prerender draws no badge, which is the honest answer for a render that can see
	// neither the device store nor a live connection.
	builder.Services.AddScoped<BlazorDLR.Shared.State.UnreadThreadState>();
	builder.Services.AddScoped<BlazorDLR.Shared.State.ConsentAskedState>();

	// Whether this device has been shown the introduction (§18.6). MainLayout injects it, so it has to
	// resolve here or the prerender throws before WASM can boot. The in-memory store answers "never
	// seen" - but the redirect that reads it runs after first render, which the prerender never
	// reaches, so this host cannot bounce anybody into the deck. The client re-resolves against
	// localStorage the moment it takes over, and that is the answer that decides.
	builder.Services.AddScoped<BlazorDLR.Shared.State.IntroTourState>();

	// Puts the adventure and the GPS back the way the last launch left them (§5.7, §18.6).
	// MainLayout injects it, so it has to resolve here, and it restores nothing at all in this
	// host: it runs on a device with a receiver to put back, and no browser has one.
	builder.Services.AddScoped<BlazorDLR.Shared.State.LaunchRestore>();

	// No GPS on this host, and nothing standing in for one (§18.6).
	//
	// ILocationProvider, LocationUpdateRateState, TrackRecordingState, PrivateAreaState and
	// LocationBroadcastState are all absent here and on the WASM client, deliberately. A browser has
	// no continuous background GPS the app can trust, so every one of them was a stub answering "not
	// supported" to screens that then had to say so - five registrations, a no-op provider and a
	// settings screen full of controls that could not do anything on the host reading them.
	//
	// The shared screens resolve the broadcaster with GetService rather than @inject and render
	// their no-receiver branch when it is missing, which is what lets the whole set be gone here
	// rather than present and inert. Receiving is unaffected: other riders' positions arrive over
	// the hub as data (§5.3), and drawing them was never a GPS concern.

	// The launch check and the announcement dialog (§20). MainLayout injects both, so they have to
	// resolve here or the prerender throws before WASM can boot. Neither does anything on this
	// host: the rung that drives them runs after first render, which a static render never reaches,
	// and this host's hub client throws on every call.
	builder.Services.AddScoped<BlazorDLR.Shared.State.StartupCheckState>();
	builder.Services.AddScoped<BlazorDLR.Shared.State.AnnouncementNotifier>();

	// The one confirm modal is mounted in MainLayout, which renders in the SSR pass too;
	// registering here keeps prerender from throwing on the ConfirmDialog @inject.
	builder.Services.AddScoped<BlazorDLR.Shared.State.ConfirmService>();

	// PageNav's back arrow asks this whether there is in-app history to step into. A single
	// static render never navigates, so it answers "no" here and the arrow falls back to the
	// page's declared parent route - which is the right answer for a prerender, and the WASM
	// client starts its own count the moment it boots.
	builder.Services.AddScoped<BlazorDLR.Shared.State.NavigationHistory>();

	// Auth state for the SSR pass. Shared pages inject AuthState directly (Welcome) and via
	// AuthenticationStateProvider (Home, AuthorizeView), so both must resolve on this host
	// too - otherwise the prerender crashes before the WASM client boots and takes over
	// with the client's own scoped instance. The SSR pass has no refresh cookie the token
	// store can read, so the principal starts anonymous and NotAuthorized wins.
	builder.Services.AddAuthorizationCore();
	builder.Services.AddScoped<AuthState>();
	builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<AuthState>());

	// RiderAvatars is deliberately NOT registered here, unlike in the two client hosts.
	//
	// It fetches thumbnails with an HttpClient this host does not have and would not want: the SSR
	// pass has no refresh cookie the token store can read (see the note above), so it has no bearer
	// token, so it could not read a photo endpoint even if one were wired. ValidateOnBuild says so
	// out loud rather than letting it be discovered at render time - registering it here fails
	// startup on the missing HttpClient, which is the check working.
	//
	// RiderAvatar resolves it with GetService and draws nothing without it, so the prerendered
	// markup carries names and no pictures. That is the correct prerender: the client renders the
	// photographs once it has a token, which is the same moment everything else on the page becomes
	// real.

	app = builder.Build();

	// First, and before the `--migrate` branch below, because that branch needs a database and this
	// is the setting that says where one is. It previously surfaced as an Npgsql stack trace on the
	// first request that touched a table.
	RequiredSettings.ValidateConnectionString(app.Configuration);

	// `--migrate` applies the schema and exits, without starting Kestrel (§9).
	//
	// A one-shot run of the same image rather than a Migrate() call on the way up. Migrating at
	// startup couples "is this server ready" to "has the schema moved", which is the coupling that
	// makes a rolling deploy or a second container an outage - and it also means a failed migration
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
	// final - under the minimal hosting model a host can still contribute sources while the
	// builder is running, and validating early would judge a half-assembled configuration.
	// Still before the first request, so the failure is "this deployment is misconfigured"
	// rather than a 500 on somebody's first sign-in attempt.
	SigningKeySource.Validate(app.Configuration, app.Environment.ContentRootPath);

	// Alongside the signing key and for the same reason, and deliberately not before the
	// `--migrate` branch: applying a schema needs a database, not somewhere to put photographs.
	// An unset value is not inert here - it resolves relative to the working directory, so the
	// server comes up perfectly happily and writes uploads into the source tree (§9.1).
	RequiredSettings.ValidateBlobRoot(app.Configuration);

	// Before anything that reads an address. Every per-address rule in §7.8 depends on
	// this having run, and the ladder in particular breaks registration outright without
	// it - every signup would look like it came from Caddy.
	app.UseForwardedHeaders();

	// The points endpoint sends ~200 KB of encoded polyline for a long ride (§15.5). Caddy
	// compresses in production; this makes the same true of a direct request.
	app.UseResponseCompression();

	// Everything worth knowing about this instance, before it serves anything: which build, which
	// machine, whose account, which folders resolved to what, which database (§14.6). A log whose first
	// lines do not say what is running cannot answer the questions actually asked of it, and the block
	// is also the honest test of file logging itself: it is there, or writing to disk is not working.
	//
	// Two sinks, one producer each, so nothing has to recognise and discard a duplicate. The writer
	// puts the block at the head of every file it opens - a server that stays up rolls a new file at
	// each midnight, and the day an administrator opens is far more often one of those than the day the
	// process started on. Standard output gets its own copy directly, because on a container that is
	// the only log there is, and it is written unlevelled and unfiltered so that nothing can hide it.
	app.Services.GetRequiredService<FileLoggerProvider>().Header = () => StartupBanner.Describe(app);

	Console.Out.WriteLine(StartupBanner.Describe(app));

	if (app.Environment.IsDevelopment())
		app.UseWebAssemblyDebugging();
	else
	{
		app.UseExceptionHandler("/Error");
		app.UseHsts();
	}

	// Re-executing a bodyless error response into the razor pipeline is right for a browser
	// navigation and wrong for every API caller, so it is scoped off /api/* for the same reason
	// the antiforgery branch below is. The re-execute rewrites the request to GET-or-POST
	// /not-found, which loses the original path and - worse - changes the status the client sees:
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
	// endpoint - the cookie-to-access-token exchange - and that endpoint calls
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
	// connection every fifteen minutes - the access token's lifetime, which has nothing to do with
	// whether the rider is still on the bike. The client's AccessTokenProvider supplies a fresh token
	// on *reconnect*, which is where rotation belongs. Written out so nobody later "fixes" it.
	app.MapHub<RideHub>(RideHub.Path);

	app.MapStaticAssets();

	// The razor host serves the WASM shell for every route. Auth on this app lives entirely in the
	// WASM heap (§7.4, §18.5) - the server has no cookie or bearer on a razor page GET, so applying
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
}
catch (Exception ex)
{
	// The logger is not available until after Build, so this is the only place to catch a
	// startup failure and log it. The file logger is configured to write the startup banner
	// first, so the log file is always readable even if the failure is in the middle of
	// startup.
	Console.Error.WriteLine(ex.ToString());
	var logger = app?.Services.GetRequiredService<ILogger<Program>>();
	logger?.LogCritical($"DLR Startup Failure {ex.ToString()}");
	logger?.LogCritical($"DLR Stats {StartupBanner.Describe(app!)}");
}

/// <summary>
/// Named so <c>WebApplicationFactory&lt;Program&gt;</c> has an entry point to bind to.
/// </summary>
public partial class Program;
