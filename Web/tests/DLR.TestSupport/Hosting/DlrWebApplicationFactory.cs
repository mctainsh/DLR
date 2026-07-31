using DLR.Server.Data;
using DLR.Server.Identity;
using DLR.TestSupport.Database;
using DLR.TestSupport.Email;
using DLR.TestSupport.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace DLR.TestSupport.Hosting;

/// <summary>
/// The server under test: the real <c>Program</c>, pointed at a throwaway PostgreSQL
/// database, with the clock and the email transport replaced by ones a test can drive.
/// </summary>
public sealed class DlrWebApplicationFactory : WebApplicationFactory<Program>
{
	/// <summary>
	/// Where the fake clock starts unless a test says otherwise. A fixed instant rather
	/// than "now": a suite whose starting point moves every day has a different set of
	/// boundary conditions every day.
	/// </summary>
	public static readonly DateTimeOffset DefaultStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	/// <summary>
	/// The signing key every test server uses. Fixed rather than random so a token can be
	/// pasted into a debugger and read, and long enough to clear the 32-byte HS256 floor.
	/// </summary>
	public const string SigningKey = "test-signing-key-not-a-secret-and-long-enough-for-hs256";

	/// <summary>
	/// Rate limits raised out of the way for tests that are not about them.
	/// <para>
	/// The §7.8 limits are per address, and every test in the suite shares one — so five
	/// login attempts a minute would make the timing test in §7.4 fail for a reason it is not
	/// investigating. Raised rather than switched off, so the code path is still exercised
	/// everywhere; <see cref="WithRealRateLimits"/> puts the shipped numbers back for the
	/// tests that assert them, and a separate test asserts the shipped defaults themselves.
	/// </para>
	/// </summary>
	private static readonly Dictionary<string, string?> RelaxedLimits = new()
	{
		["RateLimits:LoginPerMinutePerAddress"] = "10000",
		["RateLimits:LoginPerHourPerUserName"] = "10000",
		["RateLimits:RegisterPerHourPerAddress"] = "10000",
		["RateLimits:ForgotPerHourPerEmail"] = "10000",
		["RateLimits:ForgotPerHourPerAddress"] = "10000",
		["RateLimits:RefreshPerMinutePerDevice"] = "10000",
	};

	private readonly string _connectionString;
	private readonly Dictionary<string, string?> _overrides;

	private DlrWebApplicationFactory(
		string connectionString,
		DateTimeOffset startingAt,
		Dictionary<string, string?>? overrides = null)
	{
		_connectionString = connectionString;
		_overrides = overrides ?? [];
		Clock = new FakeTimeProvider(startingAt);

		// Local time is UTC in every test, so a machine's time zone can never be the
		// reason a suite passes here and fails on a runner in another hemisphere.
		Clock.SetLocalTimeZone(TimeZoneInfo.Utc);
	}

	/// <summary>
	/// The server's clock. Advance it to reach a token expiry, a wind-down, a throttle
	/// window or the 180-day inactivity horizon without waiting for any of them.
	/// </summary>
	public FakeTimeProvider Clock { get; }

	/// <summary>Everything the server has tried to email.</summary>
	public CollectingEmailSender Emails { get; } = new();

	/// <summary>
	/// The breach corpus, as this test decides it. Nothing here reaches Pwned Passwords —
	/// which is also the only way to exercise §7.2's "service unreachable" clause on demand.
	/// </summary>
	public FakeBreachedPasswordCheck Breaches { get; } = new();

	/// <summary>The connection string this instance is using, for a test that needs it directly.</summary>
	public string ConnectionString => _connectionString;

	/// <summary>
	/// Creates a fresh database, starts the server against it and applies every migration.
	/// </summary>
	/// <param name="postgres">The collection's container.</param>
	/// <param name="startingAt">Where the fake clock starts; defaults to <see cref="DefaultStart"/>.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <param name="settings">Configuration overrides — the §7.8 thresholds, mostly.</param>
	public static async Task<DlrWebApplicationFactory> CreateAsync(
		PostgresFixture postgres,
		DateTimeOffset? startingAt = null,
		Dictionary<string, string?>? settings = null,
		CancellationToken cancellationToken = default)
	{
		string connectionString = await postgres.CreateDatabaseAsync(cancellationToken);

		DlrWebApplicationFactory factory = new(connectionString, startingAt ?? DefaultStart, settings);

		await factory.MigrateAsync(cancellationToken);

		return factory;
	}

	/// <summary>
	/// A second server against the <em>same</em> database — a process restart, in other words.
	/// <para>
	/// The distinction §7.8 draws between the ladder and the rate limiter is only visible
	/// across one of these: in-process counters come back empty and row counts do not. Without
	/// a way to restart, <c>Register_LadderCountSurvivesProcessRestart</c> would be asserting
	/// something no test could observe.
	/// </para>
	/// </summary>
	/// <param name="settings">Configuration overrides for the new instance.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public DlrWebApplicationFactory Restart(
		Dictionary<string, string?>? settings = null,
		CancellationToken cancellationToken = default)
	{
		_ = cancellationToken;

		// The schema is already there, so no migration — and the clock restarts where this one
		// currently is, because a redeploy does not move time.
		return new DlrWebApplicationFactory(_connectionString, Clock.GetUtcNow(), settings);
	}

	/// <summary>Runs an operation against a scoped <see cref="DlrDbContext"/>.</summary>
	public async Task<T> WithDatabaseAsync<T>(Func<DlrDbContext, Task<T>> operation)
	{
		using IServiceScope scope = Services.CreateScope();

		return await operation(scope.ServiceProvider.GetRequiredService<DlrDbContext>());
	}

	/// <summary>Runs an operation against a scoped <see cref="DlrDbContext"/>.</summary>
	public async Task WithDatabaseAsync(Func<DlrDbContext, Task> operation)
	{
		using IServiceScope scope = Services.CreateScope();

		await operation(scope.ServiceProvider.GetRequiredService<DlrDbContext>());
	}

	/// <inheritdoc />
	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseEnvironment("Testing");

		builder.ConfigureAppConfiguration(configuration =>
			configuration.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:Dlr"] = _connectionString,

				// In memory rather than in a settings file, which is also what the §7.4
				// startup guard requires: a key that ships with the code is refused, and a
				// test suite is not an exemption from that.
				["Auth:SigningKey"] = SigningKey,

				// The test host connects over loopback, so the forwarded header is only
				// honoured if loopback is a known proxy. Without this every test would see
				// its own socket address and §7.8's per-address rules would be untestable.
				["ForwardedHeaders:KnownProxies:0"] = "127.0.0.1",
				["ForwardedHeaders:KnownProxies:1"] = "::1",
			})
			.AddInMemoryCollection(RelaxedLimits)
			.AddInMemoryCollection(_overrides));

		builder.ConfigureServices(services =>
		{
			// Replace rather than add: a second registration would leave the real clock
			// resolvable, and the one that wins would depend on registration order.
			services.RemoveAll<TimeProvider>();
			services.AddSingleton<TimeProvider>(Clock);

			services.RemoveAll<IEmailSender>();
			services.AddSingleton<IEmailSender>(Emails);

			// RemoveAll also takes out the typed HttpClient registration behind the real
			// implementation, which is the point: a test suite that can reach the internet
			// is a test suite that fails for reasons it did not cause.
			services.RemoveAll<IBreachedPasswordCheck>();
			services.AddSingleton<IBreachedPasswordCheck>(Breaches);

			services.AddSingleton<IStartupFilter, LoopbackConnection>();
		});
	}

	private async Task MigrateAsync(CancellationToken cancellationToken)
	{
		using IServiceScope scope = Services.CreateScope();
		DlrDbContext database = scope.ServiceProvider.GetRequiredService<DlrDbContext>();

		await database.Database.MigrateAsync(cancellationToken);
	}
}
