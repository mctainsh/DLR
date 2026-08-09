using DLR.Server.Data.Identity;
using DLR.Server.Data.Tracks;

namespace DLR.Server.Data.Rides;

/// <summary>
/// A track attached to a group ride as a planned route (§5.2, §5.4).
/// <para>
/// <strong>A join row, so a ride can carry several.</strong> The original outline modelled this
/// as one nullable column on <see cref="GroupRide"/> — attach or replace <em>the</em> route —
/// and a real day out does not fit that shape: the short option and the long option, the way
/// out and the way home, the detour somebody added the night before. The organiser attaches as
/// many as the ride actually has and the map draws all of them.
/// </para>
/// <para>
/// <strong>No copy is taken.</strong> The row points at the owner's <see cref="Tracks.Track"/>,
/// which is what makes "the caller owns the track" (§15.4) mean something on both sides of the
/// attachment: a rider hands a route over by exporting the GPX and letting the other person
/// import it, and that round trip <em>is</em> the copy feature.
/// </para>
/// </summary>
public sealed class GroupRideRoute
{
	/// <summary>Which ride.</summary>
	public Guid GroupRideId { get; set; }

	/// <summary>Which track.</summary>
	public Guid TrackId { get; set; }

	/// <summary>
	/// Where this route sits in the ride's list, ascending. Assigned one past the highest in use.
	/// <para>
	/// <strong>Load-bearing, which is why it is a column and not an ordering by timestamp.</strong>
	/// §5.4's gap list and the off-route warning project a rider against <em>one</em> line — the
	/// first — so a ride whose routes reshuffled themselves would move every rider in that list
	/// without anybody having ridden anywhere. Two routes attached inside the same clock tick is
	/// enough for a timestamp ordering to be undefined, and a test clock that does not advance at
	/// all makes it undefined every time.
	/// </para>
	/// <para>
	/// Gaps are left where a route was removed rather than being closed up: renumbering would
	/// rewrite rows nobody touched to no purpose, and only the order matters.
	/// </para>
	/// </summary>
	public int Position { get; set; }

	/// <summary>When it was attached. Shown to people; never the sort key — see <see cref="Position"/>.</summary>
	public DateTimeOffset AddedUtc { get; set; }

	/// <summary>Which organiser or leader attached it.</summary>
	public Guid AddedByUserId { get; set; }

	/// <summary>The ride, for cascade deletion.</summary>
	public GroupRide? Ride { get; set; }

	/// <summary>The track, for cascade deletion.</summary>
	public Track? Track { get; set; }

	/// <summary>The account that attached it, for cascade deletion.</summary>
	public AppUser? AddedBy { get; set; }
}
