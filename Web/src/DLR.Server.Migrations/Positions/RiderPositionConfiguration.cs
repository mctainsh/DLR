using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DLR.Server.Data.Positions;

/// <summary>The <c>rider_position</c> table (§5.5).</summary>
public sealed class RiderPositionConfiguration : IEntityTypeConfiguration<RiderPosition>
{
	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<RiderPosition> builder)
	{
		builder.ToTable("rider_position");

		// One row per rider per ride, as a shape rather than a rule. Also the conflict target
		// the §5.5 flush upserts on.
		builder.HasKey(position => new { position.GroupRideId, position.UserId });

		builder
			.HasOne(position => position.Ride)
			.WithMany()
			.HasForeignKey(position => position.GroupRideId)
			.OnDelete(DeleteBehavior.Cascade);

		builder
			.HasOne(position => position.User)
			.WithMany()
			.HasForeignKey(position => position.UserId)
			.OnDelete(DeleteBehavior.Cascade);

		// The freshness gate on rehydration (§5.5) and the nightly backstop sweep (§7.11) both
		// range over this column.
		builder
			.HasIndex(position => position.RecordedUtc)
			.HasDatabaseName("ix_rider_position_recorded");
	}
}
