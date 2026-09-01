using DLR.Server.Data.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Data;

/// <summary>
/// The one <see cref="DbContext"/>. Lives beside its migrations so that the model and
/// the schema that implements it are in a single assembly (§3).
/// <para>
/// <see cref="IdentityUserContext{TUser, TKey}"/> rather than <c>IdentityDbContext</c>:
/// the role tables would be four tables nothing in §7 ever reads. Access is decided by
/// ride membership and organiser consent (§5.2), not by a role a user has been granted.
/// </para>
/// </summary>
/// <param name="options">Configured by the host; the connection string is never here.</param>
public sealed class DlrDbContext(DbContextOptions<DlrDbContext> options)
	: IdentityUserContext<AppUser, Guid>(options)
{
	/// <inheritdoc />
	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		RenameIdentityTables(modelBuilder);

		// Entity configurations are picked up from this assembly as each one is written,
		// so adding a table is adding a file rather than editing a growing method.
		modelBuilder.ApplyConfigurationsFromAssembly(typeof(DlrDbContext).Assembly);
	}

	/// <summary>
	/// Puts Identity's four tables into the schema's own casing (§7.13).
	/// <para>
	/// The snake_case convention rewrites names it derives and leaves alone names that were
	/// configured explicitly - and Identity's model configures all four by calling
	/// <c>ToTable("AspNetUsers")</c> itself. So every column, key and index around these
	/// tables would come out snake_case while the tables they belong to did not.
	/// </para>
	/// </summary>
	private static void RenameIdentityTables(ModelBuilder modelBuilder)
	{
		modelBuilder.Entity<AppUser>().ToTable("asp_net_users");
		modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("asp_net_user_claims");
		modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("asp_net_user_logins");
		modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("asp_net_user_tokens");
	}
}
