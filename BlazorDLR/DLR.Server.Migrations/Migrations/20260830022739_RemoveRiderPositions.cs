using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class RemoveRiderPositions : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "rider_position");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "rider_position",
			columns: table => new
			{
				group_ride_id = table.Column<Guid>(type: "uuid", nullable: false),
				user_id = table.Column<Guid>(type: "uuid", nullable: false),
				accuracy_m = table.Column<short>(type: "smallint", nullable: true),
				heading_deg = table.Column<short>(type: "smallint", nullable: true),
				lat = table.Column<int>(type: "integer", nullable: false),
				lon = table.Column<int>(type: "integer", nullable: false),
				recorded_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
				speed_mps = table.Column<short>(type: "smallint", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_rider_position", x => new { x.group_ride_id, x.user_id });
				table.ForeignKey(
					name: "fk_rider_position_asp_net_users_user_id",
					column: x => x.user_id,
					principalTable: "asp_net_users",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
				table.ForeignKey(
					name: "fk_rider_position_group_ride_group_ride_id",
					column: x => x.group_ride_id,
					principalTable: "group_ride",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			name: "ix_rider_position_recorded",
			table: "rider_position",
			column: "recorded_utc");

		migrationBuilder.CreateIndex(
			name: "ix_rider_position_user_id",
			table: "rider_position",
			column: "user_id");
	}
}
