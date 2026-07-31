using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Data;

/// <summary>
/// The one <see cref="DbContext"/>. Lives beside its migrations so that the model and
/// the schema that implements it are in a single assembly (§3).
/// </summary>
/// <param name="options">Configured by the host; the connection string is never here.</param>
public sealed class DlrDbContext(DbContextOptions<DlrDbContext> options) : DbContext(options)
{
	/// <inheritdoc />
	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		// Entity configurations are picked up from this assembly as each one is written,
		// so adding a table is adding a file rather than editing a growing method.
		modelBuilder.ApplyConfigurationsFromAssembly(typeof(DlrDbContext).Assembly);
	}
}
