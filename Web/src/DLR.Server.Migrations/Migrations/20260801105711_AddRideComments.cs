using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddRideComments : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "ride_comment",
			columns: table => new
			{
				id = table.Column<Guid>(type: "uuid", nullable: false),
				group_ride_id = table.Column<Guid>(type: "uuid", nullable: false),
				author_id = table.Column<Guid>(type: "uuid", nullable: false),
				client_guid = table.Column<Guid>(type: "uuid", nullable: false),
				kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
				body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
				photo_id = table.Column<Guid>(type: "uuid", nullable: true),
				is_pinned = table.Column<bool>(type: "boolean", nullable: false),
				pinned_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
				pinned_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
				created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
				posted_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
				edited_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_ride_comment", x => x.id);
				table.CheckConstraint("ck_ride_comment_has_content", "body IS NOT NULL OR photo_id IS NOT NULL");
				table.ForeignKey(
					name: "fk_ride_comment_asp_net_users_author_id",
					column: x => x.author_id,
					principalTable: "asp_net_users",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
				table.ForeignKey(
					name: "fk_ride_comment_group_ride_group_ride_id",
					column: x => x.group_ride_id,
					principalTable: "group_ride",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
				table.ForeignKey(
					name: "fk_ride_comment_photo_photo_id",
					column: x => x.photo_id,
					principalTable: "photo",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			name: "ix_ride_comment_author_id",
			table: "ride_comment",
			column: "author_id");

		migrationBuilder.CreateIndex(
			name: "ix_ride_comment_photo_id",
			table: "ride_comment",
			column: "photo_id");

		migrationBuilder.CreateIndex(
			name: "ix_ride_comment_pinned",
			table: "ride_comment",
			column: "group_ride_id",
			filter: "is_pinned");

		migrationBuilder.CreateIndex(
			name: "ix_ride_comment_ride_posted",
			table: "ride_comment",
			columns: new[] { "group_ride_id", "posted_utc" },
			descending: new[] { false, true });

		migrationBuilder.CreateIndex(
			name: "ux_ride_comment_client",
			table: "ride_comment",
			columns: new[] { "group_ride_id", "author_id", "client_guid" },
			unique: true);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "ride_comment");
	}
}
