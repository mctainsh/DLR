using DLR.Server.Data;
using DLR.Server.Identity;
using DLR.TestSupport.Database;
using DLR.TestSupport.Email;
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

	private readonly string _connectionString;

	private DlrWebApplicationFactory(string connectionString, DateTimeOffset startingAt)
	{
		_connectionString = connectionString;
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

	/// <summary>The connection string this instance is using, for a test that needs it directly.</summary>
	public string ConnectionString => _connectionString;

	/// <summary>
	/// Creates a fresh database, starts the server against it and applies every migration.
	/// </summary>
	/// <param name="postgres">The collection's container.</param>
	/// <param name="startingAt">Where the fake clock starts; defaults to <see cref="DefaultStart"/>.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public static async Task<DlrWebApplicationFactory> CreateAsync(
		PostgresFixture postgres,
		DateTimeOffset? startingAt = null,
		CancellationToken cancellationToken = default)
	{
		string connectionString = await postgres.CreateDatabaseAsync(cancellationToken);

		DlrWebApplicationFactory factory = new(connectionString, startingAt ?? DefaultStart);

		await factory.MigrateAsync(cancellationToken);

		return factory;
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
			}));

		builder.ConfigureServices(services =>
		{
			// Replace rather than add: a second registration would leave the real clock
			// resolvable, and the one that wins would depend on registration order.
			services.RemoveAll<TimeProvider>();
			services.AddSingleton<TimeProvider>(Clock);

			services.RemoveAll<IEmailSender>();
			services.AddSingleton<IEmailSender>(Emails);
		});
	}

	private async Task MigrateAsync(CancellationToken cancellationToken)
	{
		using IServiceScope scope = Services.CreateScope();
		DlrDbContext database = scope.ServiceProvider.GetRequiredService<DlrDbContext>();

		await database.Database.MigrateAsync(cancellationToken);
	}
}
