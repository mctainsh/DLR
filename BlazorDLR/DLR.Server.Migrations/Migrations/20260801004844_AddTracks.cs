using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddTracks : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "track",
			columns: table => new
			{
				id = table.Column<Guid>(type: "uuid", nullable: false),
				owner_id = table.Column<Guid>(type: "uuid", nullable: false),
				client_guid = table.Column<Guid>(type: "uuid", nullable: false),
				name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
				created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
				started_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
				ended_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
				distance_m = table.Column<double>(type: "double precision", nullable: false),
				duration_s = table.Column<double>(type: "double precision", nullable: true),
				ascent_m = table.Column<double>(type: "double precision", nullable: true),
				max_speed_mps = table.Column<double>(type: "double precision", nullable: true),
				bounds_min_lat = table.Column<double>(type: "double precision", nullable: false),
				bounds_min_lon = table.Column<double>(type: "double precision", nullable: false),
				bounds_max_lat = table.Column<double>(type: "double precision", nullable: false),
				bounds_max_lon = table.Column<double>(type: "double precision", nullable: false),
				point_count = table.Column<int>(type: "integer", nullable: false),
				segment_count = table.Column<int>(type: "integer", nullable: false),
				visibility = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
				blob_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
				simplified_polyline = table.Column<byte[]>(type: "bytea", nullable: false),
				source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
				version = table.Column<int>(type: "integer", nullable: false),
				edited_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
				content_hash = table.Column<byte[]>(type: "bytea", nullable: false),
				imported_file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: true),
				imported_format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("pk_track", x => x.id);
				table.ForeignKey(
					name: "fk_track_asp_net_users_owner_id",
					column: x => x.owner_id,
					principalTable: "asp_net_users",
					principalColumn: "id",
					onDelete: ReferentialAction.Cascade);
			});

		migrationBuilder.CreateIndex(
			name: "ix_track_owner_content_hash",
			table: "track",
			columns: new[] { "owner_id", "content_hash" });

		migrationBuilder.CreateIndex(
			name: "ix_track_owner_created",
			table: "track",
			columns: new[] { "owner_id", "created_utc" });

		migrationBuilder.CreateIndex(
			name: "ux_track_owner_client",
			table: "track",
			columns: new[] { "owner_id", "client_guid" },
			unique: true);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(
			name: "track");
	}
}
