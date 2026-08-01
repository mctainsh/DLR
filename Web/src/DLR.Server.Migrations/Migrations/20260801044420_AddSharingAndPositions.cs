using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddSharingAndPositions : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<bool>(
			name: "share_location",
			table: "group_ride_member",
			type: "boolean",
			nullable: false,
			defaultValue: false);

		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "ended_utc",
			table: "group_ride",
			type: "timestamp with time zone",
			nullable: true);

		migrationBuilder.CreateTable(
			name: "rider_position",
			columns: table => new
			{
				group_ride_id = table.Column<Guid>(type: "uuid", nullable: false),
				user_id = table.Column<Guid>(type: "uuid", nullable: false),
				lat = table.Column<int>(type: "integer", nullable: false),
				lon = table.Column<int>(type: "integer", nullable: false),
				speed_mps = table.Column<short>(type: "smallint", nullable: true),
				heading_deg = table.Column<short>(type: "smallint", nullable: true),
				accuracy_m = table.Column<short>(type: "smallint", nullable: true),
				recorded_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "rider_position");

		migrationBuilder.DropColumn(
			name: "share_location",
			table: "group_ride_member");

		migrationBuilder.DropColumn(
			name: "ended_utc",
			table: "group_ride");
	}
}
