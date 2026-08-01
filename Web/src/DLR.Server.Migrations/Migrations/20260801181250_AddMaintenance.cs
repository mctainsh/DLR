using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddMaintenance : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "inactivity_warned_utc",
			table: "asp_net_users",
			type: "timestamp with time zone",
			nullable: true);

		migrationBuilder.CreateTable(
			name: "deleted_account_token",
			columns: table => new
			{
				token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
				deleted_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_deleted_account_token", x => x.token_hash);
			});

		migrationBuilder.CreateIndex(
			name: "ix_deleted_account_token_deleted",
			table: "deleted_account_token",
			column: "deleted_utc");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "deleted_account_token");

		migrationBuilder.DropColumn(
			name: "inactivity_warned_utc",
			table: "asp_net_users");
	}
}
