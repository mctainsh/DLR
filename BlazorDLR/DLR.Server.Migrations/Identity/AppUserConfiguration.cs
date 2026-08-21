using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DLR.Server.Data.Identity;

/// <summary>
/// The one thing about <see cref="AppUser"/> that Identity's own model does not express:
/// an email address that is optional but unique when present (§7.13).
/// </summary>
public sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<AppUser> builder)
	{
		// Identity's default index on NormalizedEmail is non-unique, because
		// RequireUniqueEmail is an in-memory validator rather than a constraint. That
		// validator cannot be turned on here — it rejects a null address, and an account
		// without one is the normal case (§7.2) — so the database enforces it instead.
		//
		// The filter is not what lets email-less accounts coexist: PostgreSQL already
		// treats NULLs as distinct in a unique index, so they would coexist without it.
		// It is here because that default is per-index and changeable — one NULLS NOT
		// DISTINCT and the system permits exactly one account with no email address —
		// and because an index over the rows that have something to be unique about is
		// the one §7.13 asks for.
		builder
			.HasIndex(user => user.NormalizedEmail)
			.HasDatabaseName("ux_users_email")
			.HasFilter("normalized_email IS NOT NULL")
			.IsUnique();

		// The nightly inactivity sweep (§7.11) reads this column across the whole table, and
		// it is the only query in the project that does.
		builder
			.HasIndex(user => user.LastActiveUtc)
			.HasDatabaseName("ix_users_last_active");

		// PostgreSQL's own address type rather than text: it validates on write, so a
		// malformed forwarded header cannot quietly become a ladder bucket of its own.
		builder.Property(user => user.CreatedByIp).HasColumnType("inet");

		// The ladder counts rows on every registration, and this is the index it counts them
		// with (§7.8). Without it that count is a sequential scan of the whole user table on
		// the signup path.
		builder
			.HasIndex(user => new { user.CreatedByIp, user.CreatedUtc })
			.HasDatabaseName("ix_users_created_ip");

		// Never mapped: it is IsRestricted's two inputs read together, and a stored copy is a
		// third place for them to disagree.
		builder.Ignore(user => user.IsRestricted);

		// Same reasoning: the three private-area columns are the truth, and this reads them
		// together (§10.1). Nothing queries on it — the only reader of an area is the account
		// that owns it — so there is no index to want either.
		builder.Ignore(user => user.HasPrivateArea);

		// SetNull rather than Cascade, exactly as a marker's and a track's photo are (§16.4): an
		// account whose profile picture was swept away is still an account, and cascading here
		// would delete a rider because a blob went missing.
		builder
			.HasOne(user => user.AvatarPhoto)
			.WithMany()
			.HasForeignKey(user => user.AvatarPhotoId)
			.OnDelete(DeleteBehavior.SetNull);

		builder.Property(user => user.DisplayName).HasMaxLength(60);
		builder.Property(user => user.PhoneNumber).HasMaxLength(20);

		// Exactly the width of "#rrggbb". The endpoint refuses anything that is not one
		// (MarkerColours.TryNormalise), and this is the same rule stated where a migration or a
		// manual UPDATE would also meet it.
		builder.Property(user => user.MarkerColour).HasMaxLength(7);

		// All three default false in the database as well as in the object, so a row inserted
		// by anything other than the registration endpoint — a migration, a fixture, a manual
		// INSERT during an incident — is also closed by default (§7.3).
		builder.Property(user => user.ShareDisplayName).HasDefaultValue(false);
		builder.Property(user => user.SharePhoneNumber).HasDefaultValue(false);
		builder.Property(user => user.ShareEmail).HasDefaultValue(false);
	}
}
