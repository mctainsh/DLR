using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddAbuseControls : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<IPAddress>(
			name: "created_by_ip",
			table: "asp_net_users",
			type: "inet",
			nullable: true);

		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "created_utc",
			table: "asp_net_users",
			type: "timestamp with time zone",
			nullable: false,
			defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

		migrationBuilder.AddColumn<bool>(
			name: "requires_email_confirmation",
			table: "asp_net_users",
			type: "boolean",
			nullable: false,
			defaultValue: false);

		migrationBuilder.CreateIndex(
			name: "ix_users_created_ip",
			table: "asp_net_users",
			columns: new[] { "created_by_ip", "created_utc" });
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropIndex(
			name: "ix_users_created_ip",
			table: "asp_net_users");

		migrationBuilder.DropColumn(
			name: "created_by_ip",
			table: "asp_net_users");

		migrationBuilder.DropColumn(
			name: "created_utc",
			table: "asp_net_users");

		migrationBuilder.DropColumn(
			name: "requires_email_confirmation",
			table: "asp_net_users");
	}
}
