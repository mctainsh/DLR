using DLR.Server.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DLR.TestSupport.Database;

/// <summary>
/// A real PostgreSQL, started once per test assembly and shared by every test in it (§10.4).
/// <para>
/// Declared as an assembly fixture - see <c>DLR.Server.Tests/DatabaseFixture.cs</c>, which records
/// why sharing it through a collection was what made the suite run one test at a time.
/// </para>
/// <para>
/// Real Postgres rather than an in-memory provider is not fussiness: this schema leans on
/// partial unique indexes, <c>ON CONFLICT</c>, check constraints and an <c>UNNEST</c>
/// upsert, none of which an emulated provider implements the way the server will meet
/// them in production. A green test against a fake database would be a lie about exactly
/// the parts most worth testing.
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
	private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
		.WithDatabase("dlr")
		.WithUsername("dlr")
		.WithPassword("dlr-tests-only")

		// Headroom above the 100 default, so the pool ceiling above is what bounds the suite
		// rather than the server - one of the two has to have room to spare, and it is
		// cheaper for it to be this one.
		.WithCommand("-c", "max_connections=300")
		.Build();

	/// <summary>
	/// The migrated database every test database is copied from, built on first use.
	/// <para>
	/// <see cref="Lazy{T}"/> over a <see cref="Task{TResult}"/> rather than a field set in
	/// <see cref="InitializeAsync"/>: the migration is the expensive half and only a run that
	/// actually asks for a database should pay it, and the first ask may well arrive from
	/// several tests at once.
	/// </para>
	/// </summary>
	private readonly Lazy<Task<string>> _template;

	/// <summary>Creates the fixture. The container starts in <see cref="InitializeAsync"/>.</summary>
	public PostgresFixture() => _template = new Lazy<Task<string>>(BuildTemplateAsync);

	/// <summary>Connection string for the container's own database.</summary>
	public string AdminConnectionString => _container.GetConnectionString();

	/// <inheritdoc />
	public ValueTask InitializeAsync() => new(_container.StartAsync());

	/// <inheritdoc />
	public ValueTask DisposeAsync() => _container.DisposeAsync();

	/// <summary>
	/// Creates a migrated database and returns a connection string for it.
	/// <para>
	/// One database per factory rather than one per collection: tests then share a
	/// container's start-up cost - which is all of the cost - while staying isolated from
	/// each other's rows. Cleaning up between tests instead would put every future test
	/// one forgotten table behind a confusing failure.
	/// </para>
	/// <para>
	/// Copied from a template rather than migrated in place. Replaying the migrations is
	/// around four hundred milliseconds and the copy is fifteen, and the suite builds one of
	/// these per test - which made replaying the same twenty-eight migrations onto the same
	/// empty database the single largest thing <c>dotnet test</c> did. The copy is a real
	/// PostgreSQL database with the real schema in it, <c>__EFMigrationsHistory</c> included,
	/// so <see cref="RelationalDatabaseFacadeExtensions.GetAppliedMigrations"/> and the
	/// §9 health check still see every migration applied.
	/// </para>
	/// </summary>
	public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken = default)
	{
		string template = await _template.Value;

		return await CreateDatabaseAsync($"dlr_{Guid.NewGuid():N}", template, cancellationToken);
	}

	/// <summary>
	/// Creates one database, optionally as a copy of <paramref name="template"/>, and returns a
	/// connection string for it.
	/// </summary>
	private async Task<string> CreateDatabaseAsync(
		string name,
		string? template,
		CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(AdminConnectionString);
		await connection.OpenAsync(cancellationToken);

		// The names are a fresh GUID and one this class chose, not input, and CREATE DATABASE
		// takes no parameters - quoting the identifiers is what keeps them well-formed rather
		// than what keeps them safe.
		await using NpgsqlCommand command = connection.CreateCommand();
		command.CommandText = template is null
			? $"CREATE DATABASE \"{name}\""
			: $"CREATE DATABASE \"{name}\" TEMPLATE \"{template}\"";

		await command.ExecuteNonQueryAsync(cancellationToken);

		return ScopedConnectionString(name);
	}

	/// <summary>
	/// Creates the template and applies every migration to it, once per container.
	/// </summary>
	private async Task<string> BuildTemplateAsync()
	{
		string name = $"dlr_template_{Guid.NewGuid():N}";
		string connectionString = await CreateDatabaseAsync(name, template: null, CancellationToken.None);

		DbContextOptionsBuilder<DlrDbContext> options = new();
		options.UseDlr(connectionString);

		await using (DlrDbContext database = new(options.Options))
			await database.Database.MigrateAsync();

		// PostgreSQL refuses to copy a database while any session is connected to it, and the
		// migration above just held one. Without this the copy fails with "source database is
		// being accessed by other users" - and it fails intermittently, in whichever test happened
		// to ask first, which is the worst shape a failure can have.
		//
		// Both halves are needed. ClearPool returns Npgsql's own idle connectors, but
		// UseNpgsql(string) builds an internal NpgsqlDataSource whose pool it does not reach, so
		// asking the server directly is what actually makes the guarantee. Belt and braces on a
		// once-per-run path costs nothing and removes a race nobody would enjoy diagnosing.
		NpgsqlConnection.ClearPool(new NpgsqlConnection(connectionString));

		await using (NpgsqlConnection admin = new(AdminConnectionString))
		{
			await admin.OpenAsync();

			await using NpgsqlCommand terminate = admin.CreateCommand();
			terminate.CommandText =
				"SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
				"WHERE datname = @template AND pid <> pg_backend_pid()";

			terminate.Parameters.AddWithValue("template", name);

			await terminate.ExecuteNonQueryAsync();
		}

		return name;
	}

	/// <summary>
	/// A connection string for one database in the container, pooled for a single server.
	/// <para>
	/// A small pool per database, because there is one database per factory and one factory
	/// per test. Npgsql's default ceiling is 100 connectors each, and PostgreSQL's default
	/// max_connections is 100 in total - so a suite of any size eventually meets "sorry,
	/// too many clients already", and it meets it as a failure in whichever test happened
	/// to run when the limit was reached rather than as anything to do with that test.
	/// Nothing here needs more than a handful.
	/// </para>
	/// </summary>
	private string ScopedConnectionString(string database) =>
		new NpgsqlConnectionStringBuilder(AdminConnectionString)
		{
			Database = database,
			MaxPoolSize = 5,
			MinPoolSize = 0,
			ConnectionIdleLifetime = 5,
			ConnectionPruningInterval = 1,
		}.ConnectionString;
}
