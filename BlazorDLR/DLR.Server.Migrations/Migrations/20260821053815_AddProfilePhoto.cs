using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <summary>
/// An account gains an optional profile photograph, shown beside its username wherever that name
/// is read (§7.3, §16.4).
/// <para>
/// One nullable column and no backfill, because there is nothing to backfill from and nothing to
/// be careful about: every account starts here with no photograph, which is exactly the state the
/// app rendered before this ran. Nobody becomes visible to anybody as a result of this migration -
/// the username beside the empty space was already readable by every signed-in rider (§7.2).
/// </para>
/// <para>
/// The foreign key is <c>SET NULL</c>, matching <c>marker.photo_id</c> and <c>track.photo_id</c>:
/// losing a photograph must not delete the account it belonged to.
/// </para>
/// <para>
/// The index on the column is the one EF creates for the foreign key. Nothing queries by it - the
/// batch lookup goes the other way, from a set of usernames to their photo ids - but it is what
/// makes deleting a <c>photo</c> row a lookup rather than a scan of the user table.
/// </para>
/// </summary>
public partial class AddProfilePhoto : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<Guid>(
			name: "avatar_photo_id",
			table: "asp_net_users",
			type: "uuid",
			nullable: true);

		migrationBuilder.CreateIndex(
			name: "ix_asp_net_users_avatar_photo_id",
			table: "asp_net_users",
			column: "avatar_photo_id");

		migrationBuilder.AddForeignKey(
			name: "fk_asp_net_users_photo_avatar_photo_id",
			table: "asp_net_users",
			column: "avatar_photo_id",
			principalTable: "photo",
			principalColumn: "id",
			onDelete: ReferentialAction.SetNull);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropForeignKey(
			name: "fk_asp_net_users_photo_avatar_photo_id",
			table: "asp_net_users");

		migrationBuilder.DropIndex(
			name: "ix_asp_net_users_avatar_photo_id",
			table: "asp_net_users");

		migrationBuilder.DropColumn(
			name: "avatar_photo_id",
			table: "asp_net_users");
	}
}
