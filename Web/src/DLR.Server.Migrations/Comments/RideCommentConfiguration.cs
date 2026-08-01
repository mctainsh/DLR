using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DLR.Server.Data.Comments;

/// <summary>The <c>ride_comment</c> table (§17.9).</summary>
public sealed class RideCommentConfiguration : IEntityTypeConfiguration<RideComment>
{
	/// <summary>
	/// The "body or photo" constraint's name, so a test can assert on the right failure rather
	/// than on any <c>DbUpdateException</c>.
	/// </summary>
	public const string HasContentConstraint = "ck_ride_comment_has_content";

	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<RideComment> builder)
	{
		builder.ToTable("ride_comment", table =>

			// A comment with a photo and no text is legitimate — most post-ride posts are exactly
			// that — so the rule is "at least one of the two", not "body required" (§17.2). In the
			// database as well as in the endpoint, for the same reason the marker arc is: every
			// path that ever writes this table inherits the invariant.
			table.HasCheckConstraint(
				HasContentConstraint,
				"body IS NOT NULL OR photo_id IS NOT NULL"));

		builder.HasKey(comment => comment.Id);

		builder.Property(comment => comment.Body).HasMaxLength(2_000);
		builder.Property(comment => comment.Kind).HasConversion<string>().HasMaxLength(20);

		builder
			.HasOne(comment => comment.Ride)
			.WithMany()
			.HasForeignKey(comment => comment.GroupRideId)
			.OnDelete(DeleteBehavior.Cascade);

		// The author's *account* going takes their posts with it (§17.6, §10.1). Their merely
		// leaving the ride does not — deleting half a conversation makes the other half nonsense,
		// so that path revokes access and keeps the rows.
		builder
			.HasOne(comment => comment.Author)
			.WithMany()
			.HasForeignKey(comment => comment.AuthorId)
			.OnDelete(DeleteBehavior.Cascade);

		// Cascade, where a marker's photo is SetNull — and the difference is the CHECK above. A
		// marker keeps a required title, so it survives losing its picture; a photo-only comment
		// has nothing left and would violate the constraint the moment the column was nulled.
		// In practice the two rows die together anyway: only your own upload can be attached, so
		// the photo and the comment always belong to the same account.
		builder
			.HasOne(comment => comment.Photo)
			.WithMany()
			.HasForeignKey(comment => comment.PhotoId)
			.OnDelete(DeleteBehavior.Cascade);

		// The thread, newest first — the query behind every open of the screen (§17.9).
		builder
			.HasIndex(comment => new { comment.GroupRideId, comment.PostedUtc })
			.HasDatabaseName("ix_ride_comment_ride_posted")
			.IsDescending(false, true);

		// Pinned posts are fetched separately on every first load and there are at most three of
		// them, so the index is partial rather than over the whole thread.
		builder
			.HasIndex(comment => comment.GroupRideId)
			.HasDatabaseName("ix_ride_comment_pinned")
			.HasFilter("is_pinned");

		// Idempotency is the unique index, not the pre-check (§17.3). Two drains of one outbox
		// arriving together are decided here; scoped to the ride and the author, because the
		// client picks the identifier and a global unique index would let one rider's guid
		// collide with another's post.
		builder
			.HasIndex(comment => new { comment.GroupRideId, comment.AuthorId, comment.ClientGuid })
			.HasDatabaseName("ux_ride_comment_client")
			.IsUnique();
	}
}
