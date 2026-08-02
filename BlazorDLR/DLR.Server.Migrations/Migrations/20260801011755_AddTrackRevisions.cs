using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddTrackRevisions : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<bool>(
			name: "is_fully_uploaded",
			table: "track",
			type: "boolean",
			nullable: false,
			defaultValue: false);

		migrationBuilder.CreateTable(
			name: "track_revision",
			columns: table => new
			{
				track_id = table.Column<Guid>(type: "uuid", nullable: false),
				version = table.Column<int>(type: "integer", nullable: false),
				blob_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
				replaced_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
				purge_after_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_track_revision", x => x.track_id);
				table.ForeignKey(
					name: "fk_track_revision_track_track_id",
					column: x => x.track_id,
					principalTable: "track",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			name: "ix_track_revision_purge_after",
			table: "track_revision",
			column: "purge_after_utc");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "track_revision");

		migrationBuilder.DropColumn(
			name: "is_fully_uploaded",
			table: "track");
	}
}
