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

	/// <summary>
	/// The "one subject, not two" constraint's name, so a test can assert on the right failure.
	/// </summary>
	public const string HasOneThreadConstraint = "ck_ride_comment_one_thread";

	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<RideComment> builder)
	{
		builder.ToTable("ride_comment", table =>
		{
			// A comment with a photo and no text is legitimate — most post-ride posts are exactly
			// that — so the rule is "at least one of the two", not "body required" (§17.2). In the
			// database as well as in the endpoint, for the same reason the marker arc is: every
			// path that ever writes this table inherits the invariant.
			table.HasCheckConstraint(
				HasContentConstraint,
				"body IS NOT NULL OR photo_id IS NOT NULL");

			// Exactly one subject, in the database rather than in the endpoints (§17.2). A row
			// with neither is a post hanging off nothing that every reader would have to
			// tolerate; a row with both is a post that appears in two conversations and gets
			// deleted by whichever of them goes first. Neither is a state the code above can
			// reach today, which is exactly why it belongs here — it is the state a future write
			// path would reach by accident.
			table.HasCheckConstraint(
				HasOneThreadConstraint,
				"(group_ride_id IS NULL) <> (track_id IS NULL)");
		});

		builder.HasKey(comment => comment.Id);

		builder.Property(comment => comment.Body).HasMaxLength(2_000);
		builder.Property(comment => comment.Kind).HasConversion<string>().HasMaxLength(20);

		builder
			.HasOne(comment => comment.Ride)
			.WithMany()
			.HasForeignKey(comment => comment.GroupRideId)
			.OnDelete(DeleteBehavior.Cascade);

		// Cascade too, and for the same reason: a route that is gone has no thread, and posts
		// pointing at nothing would fail the constraint above the moment anything touched them.
		// Deleting a route is the owner's deliberate act on their own row (§15.4) — un-sharing is
		// the reversible one, and it leaves the thread alone so that re-sharing brings it back.
		builder
			.HasOne(comment => comment.Track)
			.WithMany()
			.HasForeignKey(comment => comment.TrackId)
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

		// The thread, newest first — the query behind every open of the screen (§17.9). One index
		// per subject rather than one over both: a composite on (group_ride_id, track_id, ...)
		// would have a null in every row's leading column for half the table, which is an index
		// the planner reaches for in neither case.
		builder
			.HasIndex(comment => new { comment.GroupRideId, comment.PostedUtc })
			.HasDatabaseName("ix_ride_comment_ride_posted")
			.IsDescending(false, true);

		builder
			.HasIndex(comment => new { comment.TrackId, comment.PostedUtc })
			.HasDatabaseName("ix_ride_comment_track_posted")
			.IsDescending(false, true);

		// Pinned posts are fetched separately on every first load and there are at most three of
		// them, so the index is partial rather than over the whole thread.
		builder
			.HasIndex(comment => comment.GroupRideId)
			.HasDatabaseName("ix_ride_comment_pinned")
			.HasFilter("is_pinned");

		builder
			.HasIndex(comment => comment.TrackId)
			.HasDatabaseName("ix_ride_comment_track_pinned")
			.HasFilter("is_pinned");

		// Idempotency is the unique index, not the pre-check (§17.3). Two drains of one outbox
		// arriving together are decided here; scoped to the ride and the author, because the
		// client picks the identifier and a global unique index would let one rider's guid
		// collide with another's post.
		builder
			.HasIndex(comment => new { comment.GroupRideId, comment.AuthorId, comment.ClientGuid })
			.HasDatabaseName("ux_ride_comment_client")
			.IsUnique();

		// A second unique index rather than one over both columns, and this is the reason the
		// pair could not simply be widened: PostgreSQL treats nulls as distinct in a unique index,
		// so every route comment — whose group_ride_id is null — would satisfy the index above no
		// matter how many times the same outbox drained. Each thread kind gets an index whose
		// leading column is never null for the rows it has to decide about.
		builder
			.HasIndex(comment => new { comment.TrackId, comment.AuthorId, comment.ClientGuid })
			.HasDatabaseName("ux_ride_comment_track_client")
			.IsUnique();
	}
}
