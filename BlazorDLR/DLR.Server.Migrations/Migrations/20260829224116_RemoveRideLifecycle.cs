using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class RemoveRideLifecycle : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropColumn(
			name: "ended_utc",
			table: "group_ride");

		migrationBuilder.DropColumn(
			name: "sharing_ends_utc",
			table: "group_ride");

		migrationBuilder.DropColumn(
			name: "state",
			table: "group_ride");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "ended_utc",
			table: "group_ride",
			type: "timestamp with time zone",
			nullable: true);

		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "sharing_ends_utc",
			table: "group_ride",
			type: "timestamp with time zone",
			nullable: true);

		// 'Live' rather than the scaffolded empty string: on a rollback every existing
		// adventure has to keep taking positions, and the old code read this column.
		migrationBuilder.AddColumn<string>(
			name: "state",
			table: "group_ride",
			type: "character varying(20)",
			maxLength: 20,
			nullable: false,
			defaultValue: "Live");
	}
}
