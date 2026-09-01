using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Data;

/// <summary>
/// How <see cref="DlrDbContext"/> is configured, in one place.
/// <para>
/// The host and <c>dotnet ef</c> both build a context, and a difference between the two is
/// not a compile error - it is a migration that describes a schema the running server does
/// not use. The naming convention is exactly the kind of setting that goes wrong that way,
/// so neither caller gets to spell it out for itself.
/// </para>
/// </summary>
public static class DlrDbContextOptions
{
	/// <summary>Points a context at PostgreSQL with the project's naming convention.</summary>
	/// <param name="builder">The options being built.</param>
	/// <param name="connectionString">Where the database is.</param>
	public static DbContextOptionsBuilder UseDlr(
		this DbContextOptionsBuilder builder,
		string? connectionString)
	{
		// snake_case throughout, so the schema reads the way §7.13 and every other SQL
		// listing in the design document writes it - asp_net_users, last_active_utc - and
		// so hand-written SQL in the three folders that may contain it (§10.4) does not
		// need quoting to survive PostgreSQL's own case folding.
		return builder
			.UseNpgsql(connectionString)
			.UseSnakeCaseNamingConvention();
	}
}
