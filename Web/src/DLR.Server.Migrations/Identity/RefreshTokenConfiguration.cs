using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DLR.Server.Data.Identity;

/// <summary>The <c>device</c> table (§7.10).</summary>
public sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<Device> builder)
	{
		builder.ToTable("device");

		builder.HasKey(device => device.Id);

		builder.Property(device => device.Name).HasMaxLength(60);
		// The column default matters as much as the property default, and for the same reason
		// SRV-28's content switches needed both: this column is added to a table that already has
		// rows, and every one of them is a phone. Without it they are backfilled with the empty
		// string, which maps back to no DeviceKind at all — every existing session unreadable.
		builder
			.Property(device => device.Kind)
			.HasConversion<string>()
			.HasMaxLength(20)
			.HasDefaultValue(DeviceKind.Mobile);

		builder
			.HasOne(device => device.User)
			.WithMany()
			.HasForeignKey(device => device.UserId)
			.OnDelete(DeleteBehavior.Cascade);

		builder.HasIndex(device => device.UserId).HasDatabaseName("ix_device_user");
	}
}

/// <summary>The <c>refresh_token</c> table and its four indexes (§7.13).</summary>
public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<RefreshToken> builder)
	{
		builder.ToTable("refresh_token");

		builder.HasKey(token => token.Id);

		builder.Property(token => token.TokenHash).IsRequired();
		builder.Property(token => token.RevokedReason).HasMaxLength(100);

		builder
			.HasOne<AppUser>()
			.WithMany()
			.HasForeignKey(token => token.UserId)
			.OnDelete(DeleteBehavior.Cascade);

		builder
			.HasOne(token => token.Device)
			.WithMany()
			.HasForeignKey(token => token.DeviceId)
			.OnDelete(DeleteBehavior.Cascade);

		// Unique, because the hash is how a presented token is found. A duplicate would mean
		// one secret opening two sessions, and the lookup silently picking whichever row the
		// planner reached first.
		builder
			.HasIndex(token => token.TokenHash)
			.HasDatabaseName("ux_refresh_token_hash")
			.IsUnique();

		builder.HasIndex(token => token.FamilyId).HasDatabaseName("ix_refresh_token_family");

		builder
			.HasIndex(token => new { token.UserId, token.DeviceId })
			.HasDatabaseName("ix_refresh_token_user_device");

		builder.HasIndex(token => token.ExpiresUtc).HasDatabaseName("ix_refresh_token_expires");
	}
}
