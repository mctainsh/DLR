using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddRideRoutes : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "group_ride_route",
			columns: table => new
			{
				group_ride_id = table.Column<Guid>(type: "uuid", nullable: false),
				track_id = table.Column<Guid>(type: "uuid", nullable: false),
				position = table.Column<int>(type: "integer", nullable: false),
				added_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
				added_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_group_ride_route", x => new { x.group_ride_id, x.track_id });
				table.ForeignKey(
					name: "fk_group_ride_route_asp_net_users_added_by_user_id",
					column: x => x.added_by_user_id,
					principalTable: "asp_net_users",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
				table.ForeignKey(
					name: "fk_group_ride_route_group_ride_group_ride_id",
					column: x => x.group_ride_id,
					principalTable: "group_ride",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
				table.ForeignKey(
					name: "fk_group_ride_route_track_track_id",
					column: x => x.track_id,
					principalTable: "track",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			name: "ix_group_ride_route_added_by_user_id",
			table: "group_ride_route",
			column: "added_by_user_id");

		migrationBuilder.CreateIndex(
			name: "ix_group_ride_route_ride_position",
			table: "group_ride_route",
			columns: new[] { "group_ride_id", "position" });

		migrationBuilder.CreateIndex(
			name: "ix_group_ride_route_track",
			table: "group_ride_route",
			column: "track_id");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "group_ride_route");
	}
}
