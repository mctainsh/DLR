using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DLR.Server.Data.Migrations;

/// <summary>
/// The home private area moves from the phone's own settings store onto the account (§10.1).
/// <para>
/// Three nullable columns and no backfill, because there is nothing to backfill from: the
/// circle only ever existed on the device that set it, and the server has never been told one.
/// Every account therefore starts here with no area, and the first phone to open the Location
/// screen after the update pushes whatever it still holds. A rider whose device store had
/// already been cleared — the reason this moved — sets it again, which is the outcome the old
/// design gave them silently and this one at least makes visible.
/// </para>
/// <para>
/// These columns are personal data at rest and are documented as such on <c>AppUser</c>. They
/// are readable by nobody but the account that owns them.
/// </para>
/// </summary>
public partial class AddPrivateArea : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<double>(
			name: "private_area_lat",
			table: "asp_net_users",
			type: "double precision",
			nullable: true);

		migrationBuilder.AddColumn<double>(
			name: "private_area_lon",
			table: "asp_net_users",
			type: "double precision",
			nullable: true);

		migrationBuilder.AddColumn<double>(
			name: "private_area_radius_m",
			table: "asp_net_users",
			type: "double precision",
			nullable: true);
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropColumn(
			name: "private_area_lat",
			table: "asp_net_users");

		migrationBuilder.DropColumn(
			name: "private_area_lon",
			table: "asp_net_users");

		migrationBuilder.DropColumn(
			name: "private_area_radius_m",
			table: "asp_net_users");
	}
}
