using System.Reflection;
using DLR.Server;
using DLR.Server.Api;
using DLR.Server.Data;
using DLR.Server.Hubs;
using DLR.Server.Identity;
using DLR.Server.Positions;
using DLR.Server.Rides;
using DLR.Server.Tracks;

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
builder.Services.AddSingleton<PositionFlushService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<PositionFlushService>());
builder.Services.AddSingleton<PositionCacheRehydrator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<PositionCacheRehydrator>());

builder.Services.AddSingleton<SharingWindDownService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SharingWindDownService>());

builder.Services.AddSignalR();
builder.Services.AddSingleton<RideBroadcastService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<RideBroadcastService>());
builder.Services.Configure<TrackEditOptions>(builder.Configuration.GetSection(TrackEditOptions.Section));
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.Section));
builder.Services.Configure<AccountLinkOptions>(builder.Configuration.GetSection(AccountLinkOptions.Section));
builder.Services.AddDlrJwtBearer();

builder.Services.AddSingleton(BuildInformation.ForAssembly(Assembly.GetExecutingAssembly()));
builder.Services.Configure<AboutOptions>(builder.Configuration.GetSection(AboutOptions.Section));

var app = builder.Build();

// Refuses to start on a missing, short, or committed signing key (§7.4). After Build
// rather than before it, because that is the first point at which the configuration is
// final — under the minimal hosting model a host can still contribute sources while the
// builder is running, and validating early would judge a half-assembled configuration.
// Still before the first request, so the failure is "this deployment is misconfigured"
// rather than a 500 on somebody's first sign-in attempt.
SigningKeySource.Validate(app.Configuration, app.Environment.ContentRootPath);
RequiredSettings.ValidateConnectionString(app.Configuration);

// Before anything that reads an address. Every per-address rule in §7.8 depends on
// this having run, and the ladder in particular breaks registration outright without
// it — every signup would look like it came from Caddy.
app.UseForwardedHeaders();

// The points endpoint sends ~200 KB of encoded polyline for a long ride (§15.5). Caddy
// compresses in production; this makes the same true of a direct request.
app.UseResponseCompression();

app.UseAuthentication();
app.UseAuthorization();

app.MapAbout();
app.MapRegistration();
app.MapToken();
app.MapSessions();
app.MapEmail();
app.MapPasswords();
app.MapProfile();
app.MapTracks();
app.MapTrackImport();
app.MapTrackEditing();
app.MapTrackPoints();
app.MapRides();
app.MapMembership();
app.MapPositions();

// CloseOnAuthenticationExpiration is left at its default of false, deliberately (§7.6). SignalR
// validates the token when the connection opens; closing on expiry would kill a two-hour ride's
// connection every fifteen minutes — the access token's lifetime, which has nothing to do with
// whether the rider is still on the bike. The client's AccessTokenProvider supplies a fresh token
// on *reconnect*, which is where rotation belongs. Written out so nobody later "fixes" it.
app.MapHub<RideHub>(RideHub.Path);

app.Run();

/// <summary>
/// Named so <c>WebApplicationFactory&lt;Program&gt;</c> has an entry point to bind to.
/// </summary>
public partial class Program;
