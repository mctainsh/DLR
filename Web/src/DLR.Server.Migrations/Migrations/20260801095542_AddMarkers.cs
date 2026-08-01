using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddMarkers : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "marker",
			columns: table => new
			{
				id = table.Column<Guid>(type: "uuid", nullable: false),
				track_id = table.Column<Guid>(type: "uuid", nullable: true),
				group_ride_id = table.Column<Guid>(type: "uuid", nullable: true),
				created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
				lat = table.Column<int>(type: "integer", nullable: false),
				lon = table.Column<int>(type: "integer", nullable: false),
				direction_deg = table.Column<short>(type: "smallint", nullable: true),
				icon = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
				title = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
				note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
				created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
				updated_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_marker", x => x.id);
				table.CheckConstraint("ck_marker_one_parent", "(track_id IS NULL) <> (group_ride_id IS NULL)");
				table.ForeignKey(
					name: "fk_marker_asp_net_users_created_by_user_id",
					column: x => x.created_by_user_id,
					principalTable: "asp_net_users",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
				table.ForeignKey(
					name: "fk_marker_group_ride_group_ride_id",
					column: x => x.group_ride_id,
					principalTable: "group_ride",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
				table.ForeignKey(
					name: "fk_marker_track_track_id",
					column: x => x.track_id,
					principalTable: "track",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			name: "ix_marker_created_by_user_id",
			table: "marker",
			column: "created_by_user_id");

		migrationBuilder.CreateIndex(
			name: "ix_marker_group_ride",
			table: "marker",
			column: "group_ride_id");

		migrationBuilder.CreateIndex(
			name: "ix_marker_ride_author",
			table: "marker",
			columns: new[] { "group_ride_id", "created_by_user_id" });

		migrationBuilder.CreateIndex(
			name: "ix_marker_track",
			table: "marker",
			column: "track_id");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "marker");
	}
}
