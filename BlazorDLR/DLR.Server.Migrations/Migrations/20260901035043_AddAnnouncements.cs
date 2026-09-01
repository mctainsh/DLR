using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddAnnouncements : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "announcement",
			columns: table => new
			{
				id = table.Column<Guid>(type: "uuid", nullable: false),
				severity = table.Column<int>(type: "integer", nullable: false),
				title = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
				body = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
				publish_from_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
				expires_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
				created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
				created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_announcement", x => x.id);
				table.ForeignKey(
					name: "fk_announcement_asp_net_users_created_by_user_id",
					column: x => x.created_by_user_id,
					principalTable: "asp_net_users",
					principalColumn: "id",
					onDelete: ReferentialAction.SetNull);
			});

		migrationBuilder.CreateIndex(
			name: "ix_announcement_created_by_user_id",
			table: "announcement",
			column: "created_by_user_id");

		migrationBuilder.CreateIndex(
			name: "ix_announcement_window",
			table: "announcement",
			columns: new[] { "publish_from_utc", "expires_utc" });
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "announcement");
	}
}
