using System.Reflection;
using DLR.Server.Api;
using DLR.Server.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// TimeProvider is registered from day one. Every timing-dependent rule in the
// project — token lifespans, the flush interval, the wind-down sweep, the
// 180-day inactivity horizon — resolves it, so tests advance a fake clock
// rather than sleeping. Retrofitting this later is miserable (§10.4).
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddDbContext<DlrDbContext>(options =>
	options.UseNpgsql(builder.Configuration.GetConnectionString("Dlr")));

builder.Services.AddSingleton(BuildInformation.ForAssembly(Assembly.GetExecutingAssembly()));
builder.Services.Configure<AboutOptions>(builder.Configuration.GetSection(AboutOptions.Section));

var app = builder.Build();

app.MapAbout();

app.Run();

/// <summary>
/// Named so <c>WebApplicationFactory&lt;Program&gt;</c> has an entry point to bind to.
/// </summary>
public partial class Program;
