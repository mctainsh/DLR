using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DLR.Server.Data.Photos;

/// <summary>The <c>photo</c> table (§16.7).</summary>
public sealed class PhotoConfiguration : IEntityTypeConfiguration<Photo>
{
	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<Photo> builder)
	{
		builder.ToTable("photo");

		builder.HasKey(photo => photo.Id);

		builder.Property(photo => photo.BlobRef).HasMaxLength(64).IsRequired();
		builder.Property(photo => photo.ThumbBlobRef).HasMaxLength(64).IsRequired();
		builder.Property(photo => photo.ContentHash).HasMaxLength(32).IsRequired();

		// Deleting the account deletes the row. It does *not* delete the blobs - a cascade does
		// not reach the filesystem (§16.6), which is why SRV-33 deletes them explicitly and the
		// nightly sweep looks for orphans as a backstop. An orphaned blob is a privacy failure
		// that presents as a storage bill.
		builder
			.HasOne(photo => photo.Owner)
			.WithMany()
			.HasForeignKey(photo => photo.OwnerId)
			.OnDelete(DeleteBehavior.Cascade);

		builder
			.HasIndex(photo => photo.OwnerId)
			.HasDatabaseName("ix_photo_owner");
	}
}
