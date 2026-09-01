using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DLR.Server.Data.Rides;

/// <summary>The <c>group_ride</c> table (§8).</summary>
public sealed class GroupRideConfiguration : IEntityTypeConfiguration<GroupRide>
{
	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<GroupRide> builder)
	{
		builder.ToTable("group_ride");

		builder.HasKey(ride => ride.Id);

		builder.Property(ride => ride.Name).HasMaxLength(120).IsRequired();
		builder.Property(ride => ride.Description).HasMaxLength(2_000);
		builder.Property(ride => ride.JoinCode).HasMaxLength(16).IsRequired();

		builder.Property(ride => ride.JoinPolicy).HasConversion<string>().HasMaxLength(20);

		// The §5.8 defaults are on the column as well as on the property. The property default
		// covers rides this code creates; the column default covers the rides that already exist
		// when the migration runs, and any future path that inserts without going through the
		// entity. A permission that defaulted to off for existing rides would silently mute every
		// ride in flight the moment this shipped.
		builder.Property(ride => ride.AllowMemberMarkers).HasDefaultValue(true);
		builder.Property(ride => ride.AllowMemberComments).HasDefaultValue(true);
		builder.Property(ride => ride.AllowMemberPhotos).HasDefaultValue(true);

		builder
			.HasOne(ride => ride.Owner)
			.WithMany()
			.HasForeignKey(ride => ride.OwnerId)
			.OnDelete(DeleteBehavior.Cascade);

		// Unique, because a typed code has to resolve to exactly one ride. Generation retries
		// on a collision rather than checking first: at 32⁶ a collision is rare enough that a
		// pre-check would be a query paid on every ride to avoid one that almost never happens.
		builder
			.HasIndex(ride => ride.JoinCode)
			.HasDatabaseName("ux_group_ride_join_code")
			.IsUnique();

		builder
			.HasIndex(ride => new { ride.OwnerId, ride.CreatedUtc })
			.HasDatabaseName("ix_group_ride_owner_created");
	}
}

/// <summary>The <c>group_ride_member</c> table (§8).</summary>
public sealed class GroupRideMemberConfiguration : IEntityTypeConfiguration<GroupRideMember>
{
	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<GroupRideMember> builder)
	{
		builder.ToTable("group_ride_member");

		// The pair is the key, so "in a ride once" is a shape rather than a rule somebody
		// has to remember on every path that admits a rider.
		builder.HasKey(member => new { member.GroupRideId, member.UserId });

		builder.Property(member => member.Role).HasConversion<string>().HasMaxLength(20);

		builder
			.HasOne(member => member.Ride)
			.WithMany(ride => ride.Members)
			.HasForeignKey(member => member.GroupRideId)
			.OnDelete(DeleteBehavior.Cascade);

		builder
			.HasOne(member => member.User)
			.WithMany()
			.HasForeignKey(member => member.UserId)
			.OnDelete(DeleteBehavior.Cascade);

		// "Which rides am I in" is the query behind every screen a rider opens.
		builder
			.HasIndex(member => member.UserId)
			.HasDatabaseName("ix_group_ride_member_user");
	}
}

/// <summary>The <c>group_ride_join_request</c> table (§7.13).</summary>
public sealed class GroupRideJoinRequestConfiguration : IEntityTypeConfiguration<GroupRideJoinRequest>
{
	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<GroupRideJoinRequest> builder)
	{
		builder.ToTable("group_ride_join_request");

		builder.HasKey(request => request.Id);

		builder.Property(request => request.Message).HasMaxLength(200);
		builder.Property(request => request.Status).HasConversion<string>().HasMaxLength(20);

		builder
			.HasOne(request => request.Ride)
			.WithMany()
			.HasForeignKey(request => request.GroupRideId)
			.OnDelete(DeleteBehavior.Cascade);

		builder
			.HasOne(request => request.User)
			.WithMany()
			.HasForeignKey(request => request.UserId)
			.OnDelete(DeleteBehavior.Cascade);

		// One *live* request per rider per ride, and history kept for the decided ones - the
		// partial index is what lets both be true at once. A declined-and-blocked row is what
		// stops somebody asking again, so deleting history would be deleting the block (§7.13).
		builder
			.HasIndex(request => new { request.GroupRideId, request.UserId })
			.HasDatabaseName("ux_join_request_pending")
			.HasFilter("status = 'Pending'")
			.IsUnique();

		// The organiser's inbox, and the per-rider spam limits (§5.2).
		builder
			.HasIndex(request => new { request.UserId, request.RequestedUtc })
			.HasDatabaseName("ix_join_request_user_requested");
	}
}
