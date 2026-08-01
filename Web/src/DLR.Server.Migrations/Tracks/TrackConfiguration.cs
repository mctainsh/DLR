using DLR.Server.Data.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DLR.Server.Data.Tracks;

/// <summary>The <c>track</c> table (§8).</summary>
public sealed class TrackConfiguration : IEntityTypeConfiguration<Track>
{
	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<Track> builder)
	{
		builder.ToTable("track");

		builder.HasKey(track => track.Id);

		builder.Property(track => track.Name).HasMaxLength(120);
		builder.Property(track => track.BlobRef).HasMaxLength(64).IsRequired();
		builder.Property(track => track.ImportedFileName).HasMaxLength(260);
		builder.Property(track => track.ImportedFormat).HasMaxLength(20);

		// Stored as text rather than an integer. A number in a column nobody can read without
		// the enum beside them is a support cost every time somebody looks at a row.
		builder.Property(track => track.Source).HasConversion<string>().HasMaxLength(20);
		builder.Property(track => track.Visibility).HasConversion<string>().HasMaxLength(20);

		builder
			.HasOne(track => track.Owner)
			.WithMany()
			.HasForeignKey(track => track.OwnerId)
			.OnDelete(DeleteBehavior.Cascade);

		// What makes the upload idempotent (§6.3). A unique index rather than a check in the
		// endpoint, because two drains of the same outbox can arrive at once and a read
		// followed by a write would let both through.
		builder
			.HasIndex(track => new { track.OwnerId, track.ClientGuid })
			.HasDatabaseName("ux_track_owner_client")
			.IsUnique();

		// The track list, which sorts on created_utc rather than started_utc (§6.2): an
		// imported route has no start time at all, and a ride imported today belongs at the top
		// whenever it was ridden.
		builder
			.HasIndex(track => new { track.OwnerId, track.CreatedUtc })
			.HasDatabaseName("ix_track_owner_created");

		// Duplicate detection on import (§15.3), scoped to one owner: two riders with the same
		// ride is not a duplicate, it is a group.
		builder
			.HasIndex(track => new { track.OwnerId, track.ContentHash })
			.HasDatabaseName("ix_track_owner_content_hash");
	}
}

/// <summary>The <c>track_revision</c> table (§15.6).</summary>
public sealed class TrackRevisionConfiguration : IEntityTypeConfiguration<TrackRevision>
{
	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<TrackRevision> builder)
	{
		builder.ToTable("track_revision");

		// The track id *is* the key. "Exactly one revision per track" is then a shape the
		// database enforces rather than a rule the endpoint has to remember (§15.6).
		builder.HasKey(revision => revision.TrackId);

		builder.Property(revision => revision.BlobRef).HasMaxLength(64).IsRequired();

		builder
			.HasOne(revision => revision.Track)
			.WithOne()
			.HasForeignKey<TrackRevision>(revision => revision.TrackId)
			.OnDelete(DeleteBehavior.Cascade);

		// The nightly sweep reads this column across the whole table and nothing else does.
		builder
			.HasIndex(revision => revision.PurgeAfterUtc)
			.HasDatabaseName("ix_track_revision_purge_after");
	}
}

/// <summary>Tracks reachable from an account, for the queries that start there.</summary>
public static class TrackNavigation
{
	/// <summary>The owner's tracks, newest first (§6.2).</summary>
	/// <param name="tracks">The set to filter.</param>
	/// <param name="ownerId">Whose tracks.</param>
	public static IQueryable<Track> OwnedBy(this IQueryable<Track> tracks, Guid ownerId) =>
		tracks.Where(track => track.OwnerId == ownerId).OrderByDescending(track => track.CreatedUtc);
}
