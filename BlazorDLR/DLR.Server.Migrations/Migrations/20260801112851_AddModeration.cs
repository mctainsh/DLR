using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddModeration : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "content_report",
			columns: table => new
			{
				id = table.Column<Guid>(type: "uuid", nullable: false),
				target_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
				target_id = table.Column<Guid>(type: "uuid", nullable: false),
				group_ride_id = table.Column<Guid>(type: "uuid", nullable: true),
				author_id = table.Column<Guid>(type: "uuid", nullable: true),
				reported_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
				reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
				content_snapshot = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
				created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
				resolved_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_content_report", x => x.id);
				table.ForeignKey(
					name: "fk_content_report_asp_net_users_reported_by_user_id",
					column: x => x.reported_by_user_id,
					principalTable: "asp_net_users",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateTable(
			name: "user_block",
			columns: table => new
			{
				blocker_id = table.Column<Guid>(type: "uuid", nullable: false),
				blocked_id = table.Column<Guid>(type: "uuid", nullable: false),
				created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_user_block", x => new { x.blocker_id, x.blocked_id });
				table.ForeignKey(
					name: "fk_user_block_asp_net_users_blocked_id",
					column: x => x.blocked_id,
					principalTable: "asp_net_users",
					principalColumn: "id");
				table.ForeignKey(
					name: "fk_user_block_asp_net_users_blocker_id",
					column: x => x.blocker_id,
					principalTable: "asp_net_users",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			name: "ix_content_report_reported_by_user_id",
			table: "content_report",
			column: "reported_by_user_id");

		migrationBuilder.CreateIndex(
			name: "ix_content_report_unresolved",
			table: "content_report",
			column: "resolved_utc",
			filter: "resolved_utc IS NULL");

		migrationBuilder.CreateIndex(
			name: "ux_content_report_reporter",
			table: "content_report",
			columns: new[] { "target_kind", "target_id", "reported_by_user_id" },
			unique: true);

		migrationBuilder.CreateIndex(
			name: "ix_user_block_blocked_id",
			table: "user_block",
			column: "blocked_id");

		migrationBuilder.CreateIndex(
			name: "ix_user_block_blocker",
			table: "user_block",
			column: "blocker_id");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "content_report");

		migrationBuilder.DropTable(
			name: "user_block");
	}
}
