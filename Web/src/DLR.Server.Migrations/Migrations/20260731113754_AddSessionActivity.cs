using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddSessionActivity : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "created_utc",
			table: "device",
			type: "timestamp with time zone",
			nullable: false,
			defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "last_seen_utc",
			table: "device",
			type: "timestamp with time zone",
			nullable: false,
			defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

		migrationBuilder.AddColumn<string>(
			name: "name",
			table: "device",
			type: "character varying(60)",
			maxLength: 60,
			nullable: true);

		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "last_active_utc",
			table: "asp_net_users",
			type: "timestamp with time zone",
			nullable: false,
			defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

		migrationBuilder.CreateIndex(
			name: "ix_users_last_active",
			table: "asp_net_users",
			column: "last_active_utc");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropIndex(
			name: "ix_users_last_active",
			table: "asp_net_users");

		migrationBuilder.DropColumn(
			name: "created_utc",
			table: "device");

		migrationBuilder.DropColumn(
			name: "last_seen_utc",
			table: "device");

		migrationBuilder.DropColumn(
			name: "name",
			table: "device");

		migrationBuilder.DropColumn(
			name: "last_active_utc",
			table: "asp_net_users");
	}
}
