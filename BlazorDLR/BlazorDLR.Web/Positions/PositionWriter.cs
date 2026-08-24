using DLR.Server.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace DLR.Server.Positions;

/// <summary>Writes dirty positions to PostgreSQL (§5.5).</summary>
public interface IPositionWriter
{
	/// <summary>Writes a batch in one round trip.</summary>
	/// <param name="batch">What to write.</param>
	/// <param name="cancellationToken">Abandons the write on shutdown.</param>
	Task WriteAsync(IReadOnlyList<DirtyPosition> batch, CancellationToken cancellationToken);

	/// <summary>
	/// Adds each rider's newly counted fixes to their lifetime total (§14.6).
	/// </summary>
	/// <param name="counts">Fixes per rider since the last flush.</param>
	/// <param name="cancellationToken">Abandons the write on shutdown.</param>
	Task CountAsync(IReadOnlyDictionary<Guid, long> counts, CancellationToken cancellationToken);
}

/// <summary>
/// The <c>UNNEST</c> upsert (§5.5). One command regardless of rider count.
/// <para>
/// <strong>The one place SQL is hand-written</strong>, and it earns the §10.4 exemption twice
/// over. EF Core cannot express "insert or update several hundred rows in a single statement",
/// and it cannot express the <c>WHERE</c> guard below at all — a change-tracked update would
/// read, compare in memory and write back, which is three round trips and a race instead of one
/// statement and none.
/// </para>
/// </summary>
/// <param name="database">Its connection, so the writer joins the same pool as everything else.</param>
public sealed class PositionWriter(DlrDbContext database) : IPositionWriter
{
	/// <summary>
	/// The statement.
	/// <para>
	/// The trailing <c>WHERE</c> is load-bearing: it makes the flush <em>idempotent</em> and stops
	/// an out-of-order or retried batch from regressing a newer row. Without it a slow flush that
	/// overlaps a fast one can move every rider backwards in time, and nothing downstream would
	/// ever report it — the map would simply be wrong for ten seconds.
	/// </para>
	/// </summary>
	private const string Upsert = """
		INSERT INTO rider_position
			(group_ride_id, user_id, lat, lon, speed_mps, heading_deg, accuracy_m, recorded_utc)
		SELECT * FROM UNNEST (
			@rideIds, @userIds, @lats, @lons, @speeds, @headings, @accuracies, @times)
		ON CONFLICT (group_ride_id, user_id) DO UPDATE SET
			lat				= excluded.lat,
			lon				= excluded.lon,
			speed_mps		= excluded.speed_mps,
			heading_deg		= excluded.heading_deg,
			accuracy_m		= excluded.accuracy_m,
			recorded_utc	= excluded.recorded_utc
		WHERE excluded.recorded_utc > rider_position.recorded_utc;
		""";

	/// <summary>
	/// The lifetime counter's statement.
	/// <para>
	/// An <c>UPDATE … FROM UNNEST</c> for the same reason the upsert above is one: this runs on
	/// every flush for every rider who moved, and a change-tracked read-modify-write would be two
	/// round trips per rider and a lost update whenever two flushes overlap. <c>+ delta</c> in the
	/// statement leaves the arithmetic to PostgreSQL, where the row lock already is.
	/// </para>
	/// <para>
	/// A rider deleted between the fix and the flush simply matches nothing, which is the right
	/// answer — an account that has gone does not need its total kept up to date.
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

	/// <inheritdoc />
	public async Task WriteAsync(IReadOnlyList<DirtyPosition> batch, CancellationToken cancellationToken)
	{
		if (batch.Count == 0)
		{
			return;
		}

		NpgsqlConnection connection = await OpenAsync(cancellationToken);

		await using NpgsqlCommand command = new(Upsert, connection);

		command.Parameters.Add(Array("rideIds", NpgsqlDbType.Uuid, [.. batch.Select(row => row.RideId)]));
		command.Parameters.Add(Array("userIds", NpgsqlDbType.Uuid, [.. batch.Select(row => row.UserId)]));
		command.Parameters.Add(Array("lats", NpgsqlDbType.Integer, [.. batch.Select(row => row.Entry.Lat)]));
		command.Parameters.Add(Array("lons", NpgsqlDbType.Integer, [.. batch.Select(row => row.Entry.Lon)]));

		// Nullable arrays, because a fix without a speed is ordinary — a stationary rider, or a
		// provider that simply does not report one.
		command.Parameters.Add(Array(
			"speeds", NpgsqlDbType.Smallint, [.. batch.Select(row => row.Entry.SpeedMps)]));
		command.Parameters.Add(Array(
			"headings", NpgsqlDbType.Smallint, [.. batch.Select(row => row.Entry.HeadingDeg)]));
		command.Parameters.Add(Array(
			"accuracies", NpgsqlDbType.Smallint, [.. batch.Select(row => row.Entry.AccuracyM)]));
		command.Parameters.Add(Array(
			"times", NpgsqlDbType.TimestampTz, [.. batch.Select(row => row.Entry.RecordedUtc)]));

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
