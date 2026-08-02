using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddRefreshTokens : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "device",
			columns: table => new
			{
				id = table.Column<Guid>(type: "uuid", nullable: false),
				user_id = table.Column<Guid>(type: "uuid", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_device", x => x.id);
				table.ForeignKey(
					name: "fk_device_asp_net_users_user_id",
					column: x => x.user_id,
					principalTable: "asp_net_users",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateTable(
			name: "refresh_token",
			columns: table => new
			{
				id = table.Column<Guid>(type: "uuid", nullable: false),
				user_id = table.Column<Guid>(type: "uuid", nullable: false),
				device_id = table.Column<Guid>(type: "uuid", nullable: false),
				family_id = table.Column<Guid>(type: "uuid", nullable: false),
				token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
				successor_id = table.Column<Guid>(type: "uuid", nullable: true),
				issued_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
				expires_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
				used_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
				revoked_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
				revoked_reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_refresh_token", x => x.id);
				table.ForeignKey(
					name: "fk_refresh_token_asp_net_users_user_id",
					column: x => x.user_id,
					principalTable: "asp_net_users",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
				table.ForeignKey(
					name: "fk_refresh_token_device_device_id",
					column: x => x.device_id,
					principalTable: "device",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			name: "ix_device_user",
			table: "device",
			column: "user_id");

		migrationBuilder.CreateIndex(
			name: "ix_refresh_token_device_id",
			table: "refresh_token",
			column: "device_id");

		migrationBuilder.CreateIndex(
			name: "ix_refresh_token_expires",
			table: "refresh_token",
			column: "expires_utc");

		migrationBuilder.CreateIndex(
			name: "ix_refresh_token_family",
			table: "refresh_token",
			column: "family_id");

		migrationBuilder.CreateIndex(
			name: "ix_refresh_token_user_device",
			table: "refresh_token",
			columns: new[] { "user_id", "device_id" });

		migrationBuilder.CreateIndex(
			name: "ux_refresh_token_hash",
			table: "refresh_token",
			column: "token_hash",
			unique: true);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "refresh_token");

		migrationBuilder.DropTable(
			name: "device");
	}
}
