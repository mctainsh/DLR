using DLR.Core.Contracts.Announcements;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DLR.Server.Data.Announcements;

/// <summary>The <c>announcement</c> table (§20.2).</summary>
public sealed class AnnouncementConfiguration : IEntityTypeConfiguration<Announcement>
{
	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<Announcement> builder)
	{
		builder.ToTable("announcement");

		builder.HasKey(announcement => announcement.Id);

		builder.Property(announcement => announcement.Title)
			.HasMaxLength(AnnouncementLimits.MaxTitleChars)
			.IsRequired();

		builder.Property(announcement => announcement.Body)
			.HasMaxLength(AnnouncementLimits.MaxBodyChars)
			.IsRequired();

		// SetNull rather than Cascade: an administrator deleting their own account must not take
		// a live maintenance notice down with it.
		builder
			.HasOne(announcement => announcement.CreatedBy)
			.WithMany()
			.HasForeignKey(announcement => announcement.CreatedByUserId)
			.OnDelete(DeleteBehavior.SetNull);

		// The only query that runs per launch, and the sweep's too (§20.3).
		builder
			.HasIndex(announcement => new { announcement.PublishFromUtc, announcement.ExpiresUtc })
			.HasDatabaseName("ix_announcement_window");
	}
}
