using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <summary>
/// A route carries a fingerprint of its coordinates, so that sharing one that is already on
/// the browse list can be refused rather than duplicated (§6.2).
/// </summary>
/// <remarks>
/// <para>
/// One column, empty by default, and <strong>no backfill</strong> - the points are in blobs on
/// the volume rather than in the table (§9.1), so there is nothing SQL could hash. Every path
/// that writes points fills it from now on, and a track written before this ran has it filled
/// from its blob the first time somebody shares it. Empty therefore reads as "not known yet",
/// and the duplicate check skips a row rather than treating every un-fingerprinted route as a
/// copy of every other one.
/// </para>
/// <para>
/// The index is partial on <c>visibility = 'Public'</c>, like the two the sharing migration
/// added: a private route is never a candidate for the check, and private routes are very
/// nearly the whole table.
/// </para>
/// </remarks>
public partial class AddTrackRouteHash : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<byte[]>(
			name: "route_hash",
			table: "track",
			type: "bytea",
			nullable: false,
			defaultValue: new byte[0]);

		migrationBuilder.CreateIndex(
			name: "ix_track_route_hash",
			table: "track",
			column: "route_hash",
			filter: "visibility = 'Public'");
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropIndex(
			name: "ix_track_route_hash",
			table: "track");

		migrationBuilder.DropColumn(
			name: "route_hash",
			table: "track");
	}
}
