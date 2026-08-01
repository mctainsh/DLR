using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddContentPermissions : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<bool>(
			name: "allow_member_comments",
			table: "group_ride",
			type: "boolean",
			nullable: false,
			defaultValue: true);

		migrationBuilder.AddColumn<bool>(
			name: "allow_member_markers",
			table: "group_ride",
			type: "boolean",
			nullable: false,
			defaultValue: true);

		migrationBuilder.AddColumn<bool>(
			name: "allow_member_photos",
			table: "group_ride",
			type: "boolean",
			nullable: false,
			defaultValue: true);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropColumn(
			name: "allow_member_comments",
			table: "group_ride");

		migrationBuilder.DropColumn(
			name: "allow_member_markers",
			table: "group_ride");

		migrationBuilder.DropColumn(
			name: "allow_member_photos",
			table: "group_ride");
	}
}
