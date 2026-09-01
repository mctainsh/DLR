using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DLR.Server.Data.Moderation;

/// <summary>The <c>content_report</c> table (§17.7).</summary>
public sealed class ContentReportConfiguration : IEntityTypeConfiguration<ContentReport>
{
	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<ContentReport> builder)
	{
		builder.ToTable("content_report");

		builder.HasKey(report => report.Id);

		builder.Property(report => report.TargetKind).HasConversion<string>().HasMaxLength(20);
		builder.Property(report => report.Reason).HasMaxLength(500).IsRequired();
		builder.Property(report => report.ContentSnapshot).HasMaxLength(4_000).IsRequired();

		// No foreign key on TargetId or AuthorId, deliberately - see ContentReport. The report has
		// to survive the content, which is the entire reason the snapshot column exists.
		builder
			.HasOne(report => report.ReportedBy)
			.WithMany()
			.HasForeignKey(report => report.ReportedByUserId)
			.OnDelete(DeleteBehavior.Cascade);

		// The operator's queue: what is still open. Partial, because a resolved report is only
		// ever read by the sweep that purges it.
		builder
			.HasIndex(report => report.ResolvedUtc)
			.HasDatabaseName("ix_content_report_unresolved")
			.HasFilter("resolved_utc IS NULL");

		// One report per person per piece of content. Reporting twice is not two problems, and
		// without this a frustrated rider can manufacture a queue.
		builder
			.HasIndex(report => new { report.TargetKind, report.TargetId, report.ReportedByUserId })
			.HasDatabaseName("ux_content_report_reporter")
			.IsUnique();
	}
}

/// <summary>The <c>user_block</c> table (§16.5).</summary>
public sealed class UserBlockConfiguration : IEntityTypeConfiguration<UserBlock>
{
	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<UserBlock> builder)
	{
		builder.ToTable("user_block");

		builder.HasKey(block => new { block.BlockerId, block.BlockedId });

		builder
			.HasOne(block => block.Blocker)
			.WithMany()
			.HasForeignKey(block => block.BlockerId)
			.OnDelete(DeleteBehavior.Cascade);

		// NoAction on this side, not Cascade. Two cascade paths into asp_net_users through one
		// table is a multiple-cascade-path error in PostgreSQL; the blocker's account going takes
		// the row, and a blocked account being deleted is handled by the sweep rather than by a
		// second cascade nobody can see.
		builder
			.HasOne(block => block.Blocked)
			.WithMany()
			.HasForeignKey(block => block.BlockedId)
			.OnDelete(DeleteBehavior.NoAction);

		// "Who have I blocked" is read on every thread and marker fetch, so it is the hot query.
		builder
			.HasIndex(block => block.BlockerId)
			.HasDatabaseName("ix_user_block_blocker");
	}
}
