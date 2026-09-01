using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <summary>
/// A route gains a description, a cover photograph and the ability to be shared with everyone
/// (§6.2, §6.3).
/// <para>
/// Three nullable columns and no backfill. <c>visibility</c> already existed and every row is
/// already <c>Private</c>, so nothing becomes visible to anybody as a result of this running -
/// which is the property a sharing migration most needs to have. <c>first_shared_utc</c> stays
/// null until a rider actually shares something, and the browse list only ever reads rows where
/// it is set.
/// </para>
/// <para>
/// The photo foreign key is <c>SET NULL</c>, matching <c>marker.photo_id</c>: losing a cover
/// picture must not delete the route it was the cover of.
/// </para>
/// <para>
/// Both new indexes are partial, filtered on <c>visibility = 'Public'</c>. Shared routes are the
/// small minority of the table and these are read on every page of a list expected to get long,
/// so a partial index is a few kilobytes doing the work of a full one - and it costs nothing on
/// the recording path, which never writes a public row.
/// </para>
/// </summary>
public partial class AddTrackSharing : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<string>(
			name: "description",
			table: "track",
			type: "character varying(2000)",
			maxLength: 2000,
			nullable: true);

		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "first_shared_utc",
			table: "track",
			type: "timestamp with time zone",
			nullable: true);

		migrationBuilder.AddColumn<Guid>(
			name: "photo_id",
			table: "track",
			type: "uuid",
			nullable: true);

		migrationBuilder.CreateIndex(
			name: "ix_track_photo_id",
			table: "track",
			column: "photo_id");

		migrationBuilder.CreateIndex(
			name: "ix_track_shared",
			table: "track",
			columns: new[] { "visibility", "first_shared_utc" },
			filter: "visibility = 'Public'");

		migrationBuilder.CreateIndex(
			name: "ix_track_shared_bounds_lat",
			table: "track",
			columns: new[] { "bounds_min_lat", "bounds_max_lat" },
			filter: "visibility = 'Public'");

		migrationBuilder.AddForeignKey(
			name: "fk_track_photo_photo_id",
			table: "track",
			column: "photo_id",
			principalTable: "photo",
			principalColumn: "id",
			onDelete: ReferentialAction.SetNull);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropForeignKey(
			name: "fk_track_photo_photo_id",
			table: "track");

		migrationBuilder.DropIndex(
			name: "ix_track_photo_id",
			table: "track");

		migrationBuilder.DropIndex(
			name: "ix_track_shared",
			table: "track");

		migrationBuilder.DropIndex(
			name: "ix_track_shared_bounds_lat",
			table: "track");

		migrationBuilder.DropColumn(
			name: "description",
			table: "track");

		migrationBuilder.DropColumn(
			name: "first_shared_utc",
			table: "track");

		migrationBuilder.DropColumn(
			name: "photo_id",
			table: "track");
	}
}
