using DLR.Server.Data.Identity;
using DLR.Server.Data.Rides;

namespace DLR.Server.Data.Positions;

/// <summary>
/// Where one rider was, in one ride, most recently (§5.5).
/// <para>
/// <strong>There is no history table, and that is deliberate.</strong> The row is overwritten in
/// place, and the composite primary key makes "one row per rider per ride" a database invariant
/// rather than a convention somebody has to keep. A ride that kept a trail would be a ride that
/// could be reconstructed afterwards, which is not what anybody consented to when they tapped
/// <em>Share</em> (§10.1).
/// </para>
/// <para>
/// A rider not sharing with a ride has <em>no row in it at all</em> — not a hidden one (§5.7).
/// Consent is filtered on the write, so there is never a position at rest that somebody asked not
/// to be kept.
/// </para>
/// </summary>
public sealed class RiderPosition
{
	/// <summary>Which ride.</summary>
	public Guid GroupRideId { get; set; }

	/// <summary>Which rider.</summary>
	public Guid UserId { get; set; }

	/// <summary>Latitude, scaled by 1e5 (§5.3) — about a metre, and no float drift.</summary>
	public int Lat { get; set; }

	/// <summary>Longitude, scaled by 1e5.</summary>
	public int Lon { get; set; }

	/// <summary>Metres per second, when the fix carried one.</summary>
	public short? SpeedMps { get; set; }

	/// <summary>Degrees from north, when the fix carried one.</summary>
	public short? HeadingDeg { get; set; }

	/// <summary>Reported accuracy in metres.</summary>
	public short? AccuracyM { get; set; }

	/// <summary>
	/// When the fix was taken on the device, not when the server received it. The flush upsert
	/// compares on this column so a retried or out-of-order batch cannot regress a newer row.
	/// </summary>
	public DateTimeOffset RecordedUtc { get; set; }

	/// <summary>The ride, for cascade deletion.</summary>
	public GroupRide? Ride { get; set; }

	/// <summary>The account.</summary>
	public AppUser? User { get; set; }
}
