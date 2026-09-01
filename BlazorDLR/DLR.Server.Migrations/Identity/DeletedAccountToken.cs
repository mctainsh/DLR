using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DLR.Server.Data.Identity;

/// <summary>
/// A refresh token that belonged to an account the 180-day sweep deleted (§7.11).
/// <para>
/// <strong>It exists so the app can say what happened.</strong> §7.11 requires the next refresh
/// after a deletion to fail with a <em>distinguishable</em> reason, so the device can say "this
/// account was removed after 180 days without use" and offer to create a new one. A generic
/// sign-in failure looks like a bug and is indistinguishable from a bad password - which is how a
/// rider who has done nothing wrong ends up filing a support request about an account that was
/// deleted exactly as the notice at signup said it would be.
/// </para>
/// <para>
/// <strong>Keyed on the hash, not on the account.</strong> The account is gone - that is the whole
/// premise - and its username has been released back to the pool, so there is nothing left to point
/// at. Holding the hash means only the device that actually held the token gets the specific
/// answer; anybody presenting a guess still gets "that refresh token is not valid", so this is not
/// an oracle for whether an account ever existed.
/// </para>
/// <para>
/// Nothing here identifies a person: a SHA-256 of a value that no longer opens anything, and a
/// date. Swept by the same job after <c>Maintenance:RefreshTokenRetentionDays</c>, because a device
/// that has not been opened in a month will be told to sign in and that is answer enough.
/// </para>
/// </summary>
public sealed class DeletedAccountToken
{
	/// <summary>SHA-256 of the refresh token, exactly as <c>refresh_token</c> held it.</summary>
	public byte[] TokenHash { get; set; } = [];

	/// <summary>When the sweep deleted the account.</summary>
	public DateTimeOffset DeletedUtc { get; set; }
}

/// <summary>The <c>deleted_account_token</c> table (§7.11).</summary>
public sealed class DeletedAccountTokenConfiguration : IEntityTypeConfiguration<DeletedAccountToken>
{
	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<DeletedAccountToken> builder)
	{
		builder.ToTable("deleted_account_token");

		builder.HasKey(token => token.TokenHash);

		builder.HasIndex(token => token.DeletedUtc).HasDatabaseName("ix_deleted_account_token_deleted");
	}
}
