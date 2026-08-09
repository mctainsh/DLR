using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DLR.Server.Data.Rides;

/// <summary>The <c>group_ride_route</c> table (§8).</summary>
public sealed class GroupRideRouteConfiguration : IEntityTypeConfiguration<GroupRideRoute>
{
	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<GroupRideRoute> builder)
	{
		builder.ToTable("group_ride_route");

		// The pair is the key, the same way group_ride_member's is: "one route attached once" is
		// then a shape rather than a rule every path that attaches has to remember.
		builder.HasKey(route => new { route.GroupRideId, route.TrackId });

		builder
			.HasOne(route => route.Ride)
			.WithMany(ride => ride.Routes)
			.HasForeignKey(route => route.GroupRideId)
			.OnDelete(DeleteBehavior.Cascade);

		// The track going takes the attachment with it. A ride pointing at a track that no longer
		// exists would be a route the map cannot draw and the organiser cannot remove.
		builder
			.HasOne(route => route.Track)
			.WithMany()
			.HasForeignKey(route => route.TrackId)
			.OnDelete(DeleteBehavior.Cascade);

		// Cascade rather than SetNull, because the track is that account's too (§10.1) and would
		// cascade anyway — leaving a row whose "added by" is null while its track is gone would be
		// a broken attachment kept for the sake of a name nobody can read.
		builder
			.HasOne(route => route.AddedBy)
			.WithMany()
			.HasForeignKey(route => route.AddedByUserId)
			.OnDelete(DeleteBehavior.Cascade);

		// "Which routes does this ride have, in order" is the only read there is, and the first
		// is the one §5.4 projects riders against.
		builder
			.HasIndex(route => new { route.GroupRideId, route.Position })
			.HasDatabaseName("ix_group_ride_route_ride_position");

		// "Is this track the route of a live ride" — the §15.4 precondition on editing, asked of
		// the track rather than of the ride.
		builder
			.HasIndex(route => route.TrackId)
			.HasDatabaseName("ix_group_ride_route_track");
	}
}
