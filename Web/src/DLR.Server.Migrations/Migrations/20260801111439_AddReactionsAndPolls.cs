using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddReactionsAndPolls : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "comment_reaction",
			columns: table => new
			{
				comment_id = table.Column<Guid>(type: "uuid", nullable: false),
				user_id = table.Column<Guid>(type: "uuid", nullable: false),
				reaction = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_comment_reaction", x => new { x.comment_id, x.user_id });
				table.ForeignKey(
					name: "fk_comment_reaction_asp_net_users_user_id",
					column: x => x.user_id,
					principalTable: "asp_net_users",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
				table.ForeignKey(
					name: "fk_comment_reaction_ride_comment_comment_id",
					column: x => x.comment_id,
					principalTable: "ride_comment",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateTable(
			name: "poll",
			columns: table => new
			{
				comment_id = table.Column<Guid>(type: "uuid", nullable: false),
				allow_multiple = table.Column<bool>(type: "boolean", nullable: false),
				closes_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
				closed_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
				closed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_poll", x => x.comment_id);
				table.ForeignKey(
					name: "fk_poll_asp_net_users_closed_by_user_id",
					column: x => x.closed_by_user_id,
					principalTable: "asp_net_users",
					principalColumn: "id",
					onDelete: ReferentialAction.SetNull);
				table.ForeignKey(
					name: "fk_poll_ride_comment_comment_id",
					column: x => x.comment_id,
					principalTable: "ride_comment",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateTable(
			name: "poll_option",
			columns: table => new
			{
				id = table.Column<Guid>(type: "uuid", nullable: false),
				comment_id = table.Column<Guid>(type: "uuid", nullable: false),
				ordinal = table.Column<int>(type: "integer", nullable: false),
				text = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_poll_option", x => x.id);
				table.ForeignKey(
					name: "fk_poll_option_poll_comment_id",
					column: x => x.comment_id,
					principalTable: "poll",
					principalColumn: "comment_id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateTable(
			name: "poll_vote",
			columns: table => new
			{
				poll_option_id = table.Column<Guid>(type: "uuid", nullable: false),
				user_id = table.Column<Guid>(type: "uuid", nullable: false),
				created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_poll_vote", x => new { x.poll_option_id, x.user_id });
				table.ForeignKey(
					name: "fk_poll_vote_asp_net_users_user_id",
					column: x => x.user_id,
					principalTable: "asp_net_users",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
				table.ForeignKey(
					name: "fk_poll_vote_poll_option_poll_option_id",
					column: x => x.poll_option_id,
					principalTable: "poll_option",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			name: "ix_comment_reaction_user_id",
			table: "comment_reaction",
			column: "user_id");

		migrationBuilder.CreateIndex(
			name: "ix_poll_closed_by_user_id",
			table: "poll",
			column: "closed_by_user_id");

		migrationBuilder.CreateIndex(
			name: "ux_poll_option_ordinal",
			table: "poll_option",
			columns: new[] { "comment_id", "ordinal" },
			unique: true);

		migrationBuilder.CreateIndex(
			name: "ix_poll_vote_option",
			table: "poll_vote",
			column: "poll_option_id");

		migrationBuilder.CreateIndex(
			name: "ix_poll_vote_user_id",
			table: "poll_vote",
			column: "user_id");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "comment_reaction");

		migrationBuilder.DropTable(
			name: "poll_vote");

		migrationBuilder.DropTable(
			name: "poll_option");

		migrationBuilder.DropTable(
			name: "poll");
	}
}
