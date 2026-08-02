using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddPhotos : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<Guid>(
			name: "photo_id",
			table: "marker",
			type: "uuid",
			nullable: true);

		migrationBuilder.CreateTable(
			name: "photo",
			columns: table => new
			{
				id = table.Column<Guid>(type: "uuid", nullable: false),
				owner_id = table.Column<Guid>(type: "uuid", nullable: false),
				blob_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
				thumb_blob_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
				width_px = table.Column<int>(type: "integer", nullable: false),
				height_px = table.Column<int>(type: "integer", nullable: false),
				byte_size = table.Column<int>(type: "integer", nullable: false),
				content_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
				created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_photo", x => x.id);
				table.ForeignKey(
					name: "fk_photo_asp_net_users_owner_id",
					column: x => x.owner_id,
					principalTable: "asp_net_users",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			name: "ix_marker_photo_id",
			table: "marker",
			column: "photo_id");

		migrationBuilder.CreateIndex(
			name: "ix_photo_owner",
			table: "photo",
			column: "owner_id");

		migrationBuilder.AddForeignKey(
			name: "fk_marker_photo_photo_id",
			table: "marker",
			column: "photo_id",
			principalTable: "photo",
			principalColumn: "id",
			onDelete: ReferentialAction.SetNull);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropForeignKey(
			name: "fk_marker_photo_photo_id",
			table: "marker");

		migrationBuilder.DropTable(
			name: "photo");

		migrationBuilder.DropIndex(
			name: "ix_marker_photo_id",
			table: "marker");

		migrationBuilder.DropColumn(
			name: "photo_id",
			table: "marker");
	}
}
