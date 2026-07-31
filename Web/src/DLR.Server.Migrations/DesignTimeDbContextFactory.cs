using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DLR.Server.Data;

/// <summary>
/// How <c>dotnet ef</c> builds a context to write a migration against.
/// <para>
/// Design time needs the provider, not a database: the connection string here is never
/// opened. Without this, generating a migration would need a live server and the real
/// credentials, which is a poor thing to ask of a contributor whose whole test suite
/// otherwise runs against a throwaway container (§14.4).
/// </para>
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DlrDbContext>
{
	/// <inheritdoc />
	public DlrDbContext CreateDbContext(string[] args)
	{
		DbContextOptionsBuilder<DlrDbContext> options = new();

		options.UseNpgsql("Host=localhost;Database=dlr_design_time;Username=dlr;Password=unused");

		return new DlrDbContext(options.Options);
	}
}
