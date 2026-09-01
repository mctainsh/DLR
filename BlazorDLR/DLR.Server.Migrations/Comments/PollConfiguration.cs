using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DLR.Server.Data.Comments;

/// <summary>The <c>comment_reaction</c> table (§17.9).</summary>
public sealed class CommentReactionConfiguration : IEntityTypeConfiguration<CommentReaction>
{
	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<CommentReaction> builder)
	{
		builder.ToTable("comment_reaction");

		// One reaction per user per comment, as a shape (§17.4). Reacting again is an upsert on
		// this key rather than a second row, so there is no path that can accumulate.
		builder.HasKey(reaction => new { reaction.CommentId, reaction.UserId });

		builder.Property(reaction => reaction.Reaction).HasMaxLength(32).IsRequired();

		builder
			.HasOne(reaction => reaction.Comment)
			.WithMany()
			.HasForeignKey(reaction => reaction.CommentId)
			.OnDelete(DeleteBehavior.Cascade);

		builder
			.HasOne(reaction => reaction.User)
			.WithMany()
			.HasForeignKey(reaction => reaction.UserId)
			.OnDelete(DeleteBehavior.Cascade);
	}
}

/// <summary>The <c>poll</c> table (§17.9).</summary>
public sealed class PollConfiguration : IEntityTypeConfiguration<Poll>
{
	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<Poll> builder)
	{
		builder.ToTable("poll");

		// The comment's id, not one of its own - one poll per comment is then impossible to
		// violate rather than merely unlikely (§17.5).
		builder.HasKey(poll => poll.CommentId);

		builder
			.HasOne(poll => poll.Comment)
			.WithOne()
			.HasForeignKey<Poll>(poll => poll.CommentId)
			.OnDelete(DeleteBehavior.Cascade);

		// SetNull rather than Cascade: the organiser who closed a poll deleting their account must
		// not delete the poll, and least of all the votes in it.
		builder
			.HasOne(poll => poll.ClosedBy)
			.WithMany()
			.HasForeignKey(poll => poll.ClosedByUserId)
			.OnDelete(DeleteBehavior.SetNull);
	}
}

/// <summary>The <c>poll_option</c> table (§17.9).</summary>
public sealed class PollOptionConfiguration : IEntityTypeConfiguration<PollOption>
{
	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<PollOption> builder)
	{
		builder.ToTable("poll_option");

		builder.HasKey(option => option.Id);

		builder.Property(option => option.Text).HasMaxLength(80).IsRequired();

		builder
			.HasOne(option => option.Poll)
			.WithMany(poll => poll.Options)
			.HasForeignKey(option => option.CommentId)
			.OnDelete(DeleteBehavior.Cascade);

		// The author's order, which is the order it renders in.
		builder
			.HasIndex(option => new { option.CommentId, option.Ordinal })
			.HasDatabaseName("ux_poll_option_ordinal")
			.IsUnique();
	}
}

/// <summary>The <c>poll_vote</c> table (§17.9).</summary>
public sealed class PollVoteConfiguration : IEntityTypeConfiguration<PollVote>
{
	/// <inheritdoc />
	public void Configure(EntityTypeBuilder<PollVote> builder)
	{
		builder.ToTable("poll_vote");

		// One vote per option per person. Note what this does *not* enforce: single-select is a
		// rule about how many options one voter may hold across a poll, which no key over a single
		// option can express. The endpoint owns that half, and it is tested separately.
		builder.HasKey(vote => new { vote.PollOptionId, vote.UserId });

		builder
			.HasOne(vote => vote.Option)
			.WithMany(option => option.Votes)
			.HasForeignKey(vote => vote.PollOptionId)
			.OnDelete(DeleteBehavior.Cascade);

		builder
			.HasOne(vote => vote.User)
			.WithMany()
			.HasForeignKey(vote => vote.UserId)
			.OnDelete(DeleteBehavior.Cascade);

		// Counting a result set groups on the option (§17.9).
		builder
			.HasIndex(vote => vote.PollOptionId)
			.HasDatabaseName("ix_poll_vote_option");
	}
}
