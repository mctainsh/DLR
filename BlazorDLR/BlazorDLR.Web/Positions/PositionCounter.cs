using DLR.Server.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace DLR.Server.Positions;

/// <summary>Banks each rider's fix count into their lifetime total (§14.6).</summary>
/// <remarks>
/// <strong>This is the only thing a fix ever writes to disk.</strong> The position itself lives in
/// <see cref="RiderPositionCache"/> and nowhere else (§5.5) - what persists is the number of them,
/// which is a statistic about an account rather than a place a person was.
/// </remarks>
public interface IPositionCounter
{
	/// <summary>
	/// Adds each rider's newly counted fixes to their lifetime total (§14.6).
	/// </summary>
	/// <param name="counts">Fixes per rider since the last drain.</param>
	/// <param name="cancellationToken">Abandons the write on shutdown.</param>
	Task CountAsync(IReadOnlyDictionary<Guid, long> counts, CancellationToken cancellationToken);
}

/// <summary>
/// The lifetime counter's <c>UPDATE … FROM UNNEST</c> (§14.6).
/// <para>
/// <strong>The one place SQL is hand-written.</strong> This runs on every drain for every rider who
/// moved, and a change-tracked read-modify-write would be two round trips per rider and a lost
/// update whenever two drains overlap. <c>+ delta</c> in the statement leaves the arithmetic to
/// PostgreSQL, where the row lock already is.
/// </para>
/// </summary>
/// <param name="database">Its connection, so the counter joins the same pool as everything else.</param>
public sealed class PositionCounter(DlrDbContext database) : IPositionCounter
{
	/// <summary>
	/// The statement.
	/// <para>
	/// A rider deleted between the fix and the drain simply matches nothing, which is the right
	/// answer - an account that has gone does not need its total kept up to date.
	/// </para>
	/// </summary>
	private const string AddCounts = """
		UPDATE asp_net_users AS u
		SET positions_recorded = u.positions_recorded + c.delta
		FROM UNNEST (@userIds, @deltas) AS c(user_id, delta)
		WHERE u.id = c.user_id;
		""";

	/// <inheritdoc />
	public async Task CountAsync(IReadOnlyDictionary<Guid, long> counts, CancellationToken cancellationToken)
	{
		if (counts.Count == 0)
		{
			return;
		}

		NpgsqlConnection connection = await OpenAsync(cancellationToken);

		await using NpgsqlCommand command = new(AddCounts, connection);

		command.Parameters.Add(Array("userIds", NpgsqlDbType.Uuid, [.. counts.Keys]));
		command.Parameters.Add(Array("deltas", NpgsqlDbType.Bigint, [.. counts.Values]));

		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	/// <summary>The context's own connection, opened if this is the first command on it.</summary>
	private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
	{
		NpgsqlConnection connection = (NpgsqlConnection)database.Database.GetDbConnection();

		if (connection.State != System.Data.ConnectionState.Open)
		{
			await connection.OpenAsync(cancellationToken);
		}

		return connection;
	}

	private static NpgsqlParameter Array<T>(string name, NpgsqlDbType elementType, T[] values) =>
		new(name, NpgsqlDbType.Array | elementType) { Value = values };
}
