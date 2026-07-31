using Npgsql;
using Testcontainers.PostgreSql;

namespace DLR.TestSupport.Database;

/// <summary>
/// A real PostgreSQL, started once per collection and shared by every test in it (§10.4).
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
		// rather than the server — one of the two has to have room to spare, and it is
		// cheaper for it to be this one.
		.WithCommand("-c", "max_connections=300")
		.Build();

	/// <summary>Connection string for the container's own database.</summary>
	public string AdminConnectionString => _container.GetConnectionString();

	/// <inheritdoc />
	public Task InitializeAsync() => _container.StartAsync();

	/// <inheritdoc />
	public Task DisposeAsync() => _container.DisposeAsync().AsTask();

	/// <summary>
	/// Creates an empty database and returns a connection string for it.
	/// <para>
	/// One database per factory rather than one per collection: tests then share a
	/// container's start-up cost — which is all of the cost — while staying isolated from
	/// each other's rows. Cleaning up between tests instead would put every future test
	/// one forgotten table behind a confusing failure.
	/// </para>
	/// </summary>
	public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken = default)
	{
		string name = $"dlr_{Guid.NewGuid():N}";

		await using NpgsqlConnection connection = new(AdminConnectionString);
		await connection.OpenAsync(cancellationToken);

		// The name is a fresh GUID, not input, and CREATE DATABASE takes no parameters —
		// quoting the identifier is what keeps it well-formed rather than what keeps it safe.
		await using NpgsqlCommand command = connection.CreateCommand();
		command.CommandText = $"CREATE DATABASE \"{name}\"";
		await command.ExecuteNonQueryAsync(cancellationToken);

		// A small pool per database, because there is one database per factory and one factory
		// per test. Npgsql's default ceiling is 100 connectors each, and PostgreSQL's default
		// max_connections is 100 in total — so a suite of any size eventually meets "sorry,
		// too many clients already", and it meets it as a failure in whichever test happened
		// to run when the limit was reached rather than as anything to do with that test.
		// Nothing here needs more than a handful.
		return new NpgsqlConnectionStringBuilder(AdminConnectionString)
		{
			Database = name,
			MaxPoolSize = 5,
			MinPoolSize = 0,
			ConnectionIdleLifetime = 5,
			ConnectionPruningInterval = 1,
		}.ConnectionString;
	}
}
