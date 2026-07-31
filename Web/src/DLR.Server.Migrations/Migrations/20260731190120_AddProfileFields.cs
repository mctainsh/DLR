using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddProfileFields : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AlterColumn<string>(
			name: "phone_number",
			table: "asp_net_users",
			type: "character varying(20)",
			maxLength: 20,
			nullable: true,
			oldClrType: typeof(string),
			oldType: "text",
			oldNullable: true);

		migrationBuilder.AddColumn<string>(
			name: "display_name",
			table: "asp_net_users",
			type: "character varying(60)",
			maxLength: 60,
			nullable: true);

		migrationBuilder.AddColumn<bool>(
			name: "share_display_name",
			table: "asp_net_users",
			type: "boolean",
			nullable: false,
			defaultValue: false);

		migrationBuilder.AddColumn<bool>(
			name: "share_email",
			table: "asp_net_users",
			type: "boolean",
			nullable: false,
			defaultValue: false);

		migrationBuilder.AddColumn<bool>(
			name: "share_phone_number",
			table: "asp_net_users",
			type: "boolean",
			nullable: false,
			defaultValue: false);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropColumn(
			name: "display_name",
			table: "asp_net_users");

		migrationBuilder.DropColumn(
			name: "share_display_name",
			table: "asp_net_users");

		migrationBuilder.DropColumn(
			name: "share_email",
			table: "asp_net_users");

		migrationBuilder.DropColumn(
			name: "share_phone_number",
			table: "asp_net_users");

		migrationBuilder.AlterColumn<string>(
			name: "phone_number",
			table: "asp_net_users",
			type: "text",
			nullable: true,
			oldClrType: typeof(string),
			oldType: "character varying(20)",
			oldMaxLength: 20,
			oldNullable: true);
	}
}
