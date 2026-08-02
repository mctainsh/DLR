using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DLR.Server.Data.Markers;

/// <summary>The <c>marker</c> table (§16.7).</summary>
public sealed class MarkerConfiguration : IEntityTypeConfiguration<Marker>
{
	/// <summary>The exclusive-arc constraint's name, so a test can assert on the right failure.</summary>
	public const string OneParentConstraint = "ck_marker_one_parent";

	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<Marker> builder)
	{
		builder.ToTable("marker", table =>

			// The §16.1 exclusive arc, in the database rather than only in the endpoint. Every
			// path that ever writes this table — an importer, a migration, a future bulk tool —
			// gets the invariant for free, and none of them has to remember it.
			table.HasCheckConstraint(
				OneParentConstraint,
				"(track_id IS NULL) <> (group_ride_id IS NULL)"));

		builder.HasKey(marker => marker.Id);

		builder.Property(marker => marker.Icon).HasMaxLength(32).IsRequired();
		builder.Property(marker => marker.Title).HasMaxLength(40).IsRequired();
		builder.Property(marker => marker.Note).HasMaxLength(500);

		builder
			.HasOne(marker => marker.Track)
			.WithMany()
			.HasForeignKey(marker => marker.TrackId)
			.OnDelete(DeleteBehavior.Cascade);

		builder
			.HasOne(marker => marker.Ride)
			.WithMany()
			.HasForeignKey(marker => marker.GroupRideId)
			.OnDelete(DeleteBehavior.Cascade);

		// The author's account going means their markers go with it (§10.1). Their *posts* stay
		// when they merely leave a ride (§17.6) — deletion of the account is the different case.
		builder
			.HasOne(marker => marker.CreatedBy)
			.WithMany()
			.HasForeignKey(marker => marker.CreatedByUserId)
			.OnDelete(DeleteBehavior.Cascade);

		// SetNull rather than Cascade: losing the photograph must not silently take the marker
		// with it. "Gravel across the whole corner" is still worth knowing without the picture,
		// and a cascade here would delete a hazard warning because a blob sweep ran.
		builder
			.HasOne(marker => marker.Photo)
			.WithMany()
			.HasForeignKey(marker => marker.PhotoId)
			.OnDelete(DeleteBehavior.SetNull);

		builder
			.HasIndex(marker => marker.GroupRideId)
			.HasDatabaseName("ix_marker_group_ride");

		builder
			.HasIndex(marker => marker.TrackId)
			.HasDatabaseName("ix_marker_track");

		// The per-member-per-ride cap counts on this pair (§16.5).
		builder
			.HasIndex(marker => new { marker.GroupRideId, marker.CreatedByUserId })
			.HasDatabaseName("ix_marker_ride_author");
	}
}
