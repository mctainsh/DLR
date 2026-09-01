using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <summary>
/// A shared route gets the two things a published thing needs: a star rating and a thread (§6.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The thread is the comment table it already had, widened by one column.</strong> A
/// route's conversation is the same conversation an adventure's is - same plain-text body, same
/// photograph, same six reactions, same polls, same edit window, same pinning, same reporting and
/// blocking - so <c>ride_comment</c> now hangs off either a ride or a route, and
/// <c>ck_ride_comment_one_thread</c> makes "exactly one of the two" a property of the table rather
/// than a promise the endpoints keep. A second table would have been a second copy of all of it,
/// and the copy that drifted would have been whichever had fewer tests.
/// </para>
/// <para>
/// <c>group_ride_id</c> becoming nullable is the only destructive-looking part of this, and it is
/// a widening: every existing row keeps its value and the new check constraint is satisfied by all
/// of them, because <c>track_id</c> defaults to null. Nothing is backfilled and nothing is
/// rewritten.
/// </para>
/// <para>
/// <strong>Two unique indexes for one idempotency rule, not one over both columns.</strong>
/// PostgreSQL treats nulls as distinct in a unique index, so
/// <c>ux_ride_comment_client</c> - which leads on <c>group_ride_id</c> - cannot decide anything
/// about a route comment, whose <c>group_ride_id</c> is null. Every drain of an outbox would slip
/// past it. <c>ux_ride_comment_track_client</c> is the same rule with a leading column that is
/// never null for the rows it has to judge.
/// </para>
/// <para>
/// <c>track_rating</c> is keyed on <c>(track_id, user_id)</c>, which is the "one rating per rider"
/// rule as a shape rather than as something a write path has to remember. There is no row for "no
/// opinion" and the check constraint refuses a nought: withdrawing a rating deletes it, because a
/// stored zero would average in as the worst possible score against every rider who tapped a star
/// and thought better of it.
/// </para>
/// </remarks>
public partial class AddTrackRatingsAndRouteComments : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AlterColumn<Guid>(
			name: "group_ride_id",
			table: "ride_comment",
			type: "uuid",
			nullable: true,
			oldClrType: typeof(Guid),
			oldType: "uuid");

		migrationBuilder.AddColumn<Guid>(
			name: "track_id",
			table: "ride_comment",
			type: "uuid",
			nullable: true);

		migrationBuilder.CreateTable(
			name: "track_rating",
			columns: table => new
			{
				track_id = table.Column<Guid>(type: "uuid", nullable: false),
				user_id = table.Column<Guid>(type: "uuid", nullable: false),
				stars = table.Column<short>(type: "smallint", nullable: false),
				created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
				updated_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_track_rating", x => new { x.track_id, x.user_id });
				table.CheckConstraint("ck_track_rating_stars", "stars BETWEEN 1 AND 5");
				table.ForeignKey(
					name: "fk_track_rating_asp_net_users_user_id",
					column: x => x.user_id,
					principalTable: "asp_net_users",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
				table.ForeignKey(
					name: "fk_track_rating_track_track_id",
					column: x => x.track_id,
					principalTable: "track",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			name: "ix_ride_comment_track_pinned",
			table: "ride_comment",
			column: "track_id",
			filter: "is_pinned");

		migrationBuilder.CreateIndex(
			name: "ix_ride_comment_track_posted",
			table: "ride_comment",
			columns: new[] { "track_id", "posted_utc" },
			descending: new[] { false, true });

		migrationBuilder.CreateIndex(
			name: "ux_ride_comment_track_client",
			table: "ride_comment",
			columns: new[] { "track_id", "author_id", "client_guid" },
			unique: true);

		migrationBuilder.AddCheckConstraint(
			name: "ck_ride_comment_one_thread",
			table: "ride_comment",
			sql: "(group_ride_id IS NULL) <> (track_id IS NULL)");

		migrationBuilder.CreateIndex(
			name: "ix_track_rating_track_stars",
			table: "track_rating",
			columns: new[] { "track_id", "stars" });

		migrationBuilder.CreateIndex(
			name: "ix_track_rating_user_id",
			table: "track_rating",
			column: "user_id");

		migrationBuilder.AddForeignKey(
			name: "fk_ride_comment_track_track_id",
			table: "ride_comment",
			column: "track_id",
			principalTable: "track",
			principalColumn: "id",
			onDelete: ReferentialAction.Cascade);
	}

	/// <inheritdoc />
	/// <remarks>
	/// Going back narrows <c>group_ride_id</c> again, which fails outright if any route comment
	/// has been written - as it should. There is nowhere for those rows to go, and a
	/// <c>Down</c> that quietly discarded a conversation to make the column fit would be worse
	/// than one that refuses.
	/// </remarks>
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropForeignKey(
			name: "fk_ride_comment_track_track_id",
			table: "ride_comment");

		migrationBuilder.DropTable(
			name: "track_rating");

		migrationBuilder.DropIndex(
			name: "ix_ride_comment_track_pinned",
			table: "ride_comment");

		migrationBuilder.DropIndex(
			name: "ix_ride_comment_track_posted",
			table: "ride_comment");

		migrationBuilder.DropIndex(
			name: "ux_ride_comment_track_client",
			table: "ride_comment");

		migrationBuilder.DropCheckConstraint(
			name: "ck_ride_comment_one_thread",
			table: "ride_comment");

		migrationBuilder.DropColumn(
			name: "track_id",
			table: "ride_comment");

		migrationBuilder.AlterColumn<Guid>(
			name: "group_ride_id",
			table: "ride_comment",
			type: "uuid",
			nullable: false,
			defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
			oldClrType: typeof(Guid),
			oldType: "uuid",
			oldNullable: true);
	}
}
