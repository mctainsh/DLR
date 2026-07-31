using System.Reflection;
using DLR.Server;
using DLR.Server.Api;
using DLR.Server.Data;
using DLR.Server.Identity;

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

app.UseAuthentication();
app.UseAuthorization();

app.MapAbout();
app.MapRegistration();
app.MapToken();
app.MapSessions();
app.MapEmail();
app.MapPasswords();
app.MapProfile();

app.Run();

/// <summary>
/// Named so <c>WebApplicationFactory&lt;Program&gt;</c> has an entry point to bind to.
/// </summary>
public partial class Program;
