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

		return new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name }.ConnectionString;
	}
}
