using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddWebSessions : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<string>(
			name: "kind",
			table: "device",
			type: "character varying(20)",
			maxLength: 20,
			nullable: false,
			defaultValue: "Mobile");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropColumn(
			name: "kind",
			table: "device");
	}
}
