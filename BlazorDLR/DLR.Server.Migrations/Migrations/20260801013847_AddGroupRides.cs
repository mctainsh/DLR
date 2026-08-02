using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddGroupRides : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "group_ride",
			columns: table => new
			{
				id = table.Column<Guid>(type: "uuid", nullable: false),
				owner_id = table.Column<Guid>(type: "uuid", nullable: false),
				name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
				description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
				start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
				state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
				join_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
				join_policy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
				member_cap = table.Column<int>(type: "integer", nullable: false),
				created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_group_ride", x => x.id);
				table.ForeignKey(
					name: "fk_group_ride_asp_net_users_owner_id",
					column: x => x.owner_id,
					principalTable: "asp_net_users",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateTable(
			name: "group_ride_join_request",
			columns: table => new
			{
				id = table.Column<Guid>(type: "uuid", nullable: false),
				group_ride_id = table.Column<Guid>(type: "uuid", nullable: false),
				user_id = table.Column<Guid>(type: "uuid", nullable: false),
				status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
				message = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
				requested_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
				decided_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
				decided_by = table.Column<Guid>(type: "uuid", nullable: true),
				blocked = table.Column<bool>(type: "boolean", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_group_ride_join_request", x => x.id);
				table.ForeignKey(
					name: "fk_group_ride_join_request_asp_net_users_user_id",
					column: x => x.user_id,
					principalTable: "asp_net_users",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
				table.ForeignKey(
					name: "fk_group_ride_join_request_group_ride_group_ride_id",
					column: x => x.group_ride_id,
					principalTable: "group_ride",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateTable(
			name: "group_ride_member",
			columns: table => new
			{
				group_ride_id = table.Column<Guid>(type: "uuid", nullable: false),
				user_id = table.Column<Guid>(type: "uuid", nullable: false),
				role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
				joined_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_group_ride_member", x => new { x.group_ride_id, x.user_id });
				table.ForeignKey(
					name: "fk_group_ride_member_asp_net_users_user_id",
					column: x => x.user_id,
					principalTable: "asp_net_users",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
				table.ForeignKey(
					name: "fk_group_ride_member_group_ride_group_ride_id",
					column: x => x.group_ride_id,
					principalTable: "group_ride",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			name: "ix_group_ride_owner_created",
			table: "group_ride",
			columns: new[] { "owner_id", "created_utc" });

		migrationBuilder.CreateIndex(
			name: "ux_group_ride_join_code",
			table: "group_ride",
			column: "join_code",
			unique: true);

		migrationBuilder.CreateIndex(
			name: "ix_join_request_user_requested",
			table: "group_ride_join_request",
			columns: new[] { "user_id", "requested_utc" });

		migrationBuilder.CreateIndex(
			name: "ux_join_request_pending",
			table: "group_ride_join_request",
			columns: new[] { "group_ride_id", "user_id" },
			unique: true,
			filter: "status = 'Pending'");

		migrationBuilder.CreateIndex(
			name: "ix_group_ride_member_user",
			table: "group_ride_member",
			column: "user_id");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "group_ride_join_request");

		migrationBuilder.DropTable(
			name: "group_ride_member");

		migrationBuilder.DropTable(
			name: "group_ride");
	}
}
