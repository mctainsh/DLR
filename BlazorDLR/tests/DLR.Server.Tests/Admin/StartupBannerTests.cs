using System.Reflection;
using DLR.Server.Admin;
using DLR.Server.Api;
using DLR.Server.Diagnostics;
using DLR.Server.Tracks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DLR.Server.Tests.Admin;

/// <summary>
/// The block <c>Program</c> writes before the server accepts anything (§14.6).
/// <para>
/// The two things worth pinning are that every value can be read — the block catches its own
/// failures, so a missing registration would otherwise degrade quietly to a shorter banner — and
/// that the connection string arrives without its password.
/// </para>
/// </summary>
public sealed class StartupBannerTests
{
	private const string Password = "HELLOW)THERE_8877";

	private static readonly DateTimeOffset Start = new(2026, 3, 4, 9, 30, 0, TimeSpan.Zero);

	[Fact]
	public async Task Describe_NamesTheFoldersTheDatabaseAndWhoItIsRunningAs()
	{
		await using WebApplication app = Build();

		string banner = StartupBanner.Describe(app);

		// Nothing was skipped: every service resolved and every setting was readable.
		banner.ShouldNotContain("Incomplete");

		banner.ShouldContain("Host=db.example; Database=dlr");
		banner.ShouldContain(Environment.MachineName);
		banner.ShouldContain(Environment.UserName);
		banner.ShouldContain(Environment.CurrentDirectory);
		banner.ShouldContain(AppContext.BaseDirectory);
		banner.ShouldContain(app.Environment.ContentRootPath);
		banner.ShouldContain("Blob folder");
		banner.ShouldContain("2026-03-04 09:30:00Z");

		// The roster by name, not by count: "one administrator" does not answer the question, which
		// is always whether a particular account is on it.
		banner.ShouldContain("[JRM]");
	}

	[Fact]
	public async Task Describe_LeavesThePasswordOut()
	{
		await using WebApplication app = Build();

		StartupBanner.Describe(app).ShouldNotContain(Password, Case.Insensitive);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void RedactConnectionString_SaysSoWhenThereIsNothingConfigured(string? connectionString) =>
		StartupBanner.RedactConnectionString(connectionString).ShouldBe("not configured");

	/// <summary>
	/// The smallest application the banner can describe: the services it reads, and nothing else.
	/// </summary>
	/// <returns>A built application, never started.</returns>
	private static WebApplication Build()
	{
		WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["ConnectionStrings:Dlr"] = $"Host=db.example; Database=dlr;Username=dlr;Password={Password}",
			[RequiredSettings.BlobRootPath] = Path.Combine(Path.GetTempPath(), "dlr-banner-blobs"),
		});

		builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Start));
		builder.Services.AddSingleton<ServerStart>();
		builder.Services.Configure<BlobStoreOptions>(
			options => options.RootPath = Path.Combine(Path.GetTempPath(), "dlr-banner-blobs"));
		builder.Services.AddSingleton(BuildInformation.ForAssembly(Assembly.GetExecutingAssembly()));
		builder.Services.Configure<AdminOptions>(options => options.Users = ["JRM"]);
		builder.Services.Configure<FileLogOptions>(options =>
		{
			// Off: the banner reports the settings, and a test has no business writing a log file.
			options.Enabled = false;
			options.Directory = Path.Combine(Path.GetTempPath(), "dlr-banner-logs");
			options.MinimumLevel = LogLevel.Information;
		});
		builder.Services.AddSingleton<FileLoggerProvider>();

		return builder.Build();
	}
}
