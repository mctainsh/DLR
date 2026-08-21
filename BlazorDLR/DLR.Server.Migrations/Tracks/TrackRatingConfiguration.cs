using DLR.Core.Tracks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DLR.Server.Data.Tracks;

/// <summary>The <c>track_rating</c> table (§6.2).</summary>
public sealed class TrackRatingConfiguration : IEntityTypeConfiguration<TrackRating>
{
	/// <summary>
	/// The star-range constraint's name, so a test can assert on the right failure rather than on
	/// any <c>DbUpdateException</c>.
	/// </summary>
	public const string StarRangeConstraint = "ck_track_rating_stars";

	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<TrackRating> builder)
	{
		builder.ToTable("track_rating", table =>

			// The scale, in the database as well as in the endpoint — the same marker arc the
			// comment table's "body or photo" rule follows. The endpoint is what produces a
			// sentence a rider can act on; this is what makes the rule true of every path that
			// ever writes the table, including a repair script run at two in the morning.
			table.HasCheckConstraint(
				StarRangeConstraint,
				FormattableString.Invariant(
					$"stars BETWEEN {TrackRatings.MinStars} AND {TrackRatings.MaxStars}")));

		builder.HasKey(rating => new { rating.TrackId, rating.UserId });

		builder
			.HasOne(rating => rating.Track)
			.WithMany()
			.HasForeignKey(rating => rating.TrackId)
			.OnDelete(DeleteBehavior.Cascade);

		builder
			.HasOne(rating => rating.User)
			.WithMany()
			.HasForeignKey(rating => rating.UserId)
			.OnDelete(DeleteBehavior.Cascade);

		// The average and the count for a page of twenty browse rows, in one grouped pass. The
		// primary key already leads on TrackId, so this index earns its place only by carrying
		// Stars as well — which turns the tally into an index-only scan instead of twenty trips
		// to the heap per page (§6.2).
		builder
			.HasIndex(rating => new { rating.TrackId, rating.Stars })
			.HasDatabaseName("ix_track_rating_track_stars");
	}
}
