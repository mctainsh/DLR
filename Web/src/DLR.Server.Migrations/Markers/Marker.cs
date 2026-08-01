using DLR.Server.Data.Identity;
using DLR.Server.Data.Photos;
using DLR.Server.Data.Rides;
using DLR.Server.Data.Tracks;

namespace DLR.Server.Data.Markers;

/// <summary>
/// An authored point of interest on the map (§16.1).
/// <para>
/// <strong>Authored is the word that matters.</strong> Everything else this app puts on a map is
/// <em>measured</em> — a recorded track, a live position — and measured data is governed by the
/// §10.1 rules that delete it when a ride ends. A marker is something a person deliberately placed
/// and typed, so it lives as long as the thing it is attached to. Conflating the two lifecycles is
/// how a privacy-first app quietly starts retaining locations.
/// </para>
/// <para>
/// Exactly one parent, enforced by a <c>CHECK</c> rather than by two tables: the payload, the
/// validation, the photo pipeline and the GPX mapping are identical across both parents, and
/// duplicating all of that to avoid one constraint trades a database invariant for two copies of
/// everything that can drift.
/// </para>
/// </summary>
public sealed class Marker
{
	/// <summary>Row identifier.</summary>
	public Guid Id { get; set; }

	/// <summary>The track it annotates, or null when it belongs to a ride.</summary>
	public Guid? TrackId { get; set; }

	/// <summary>The ride it belongs to, or null when it annotates a track.</summary>
	public Guid? GroupRideId { get; set; }

	/// <summary>Who placed it. They and the organiser may edit it (§16.5).</summary>
	public Guid CreatedByUserId { get; set; }

	/// <summary>Latitude, scaled by 1e5 — one coordinate encoding in the database (§5.5).</summary>
	public int Lat { get; set; }

	/// <summary>Longitude, scaled by 1e5.</summary>
	public int Lon { get; set; }

	/// <summary>
	/// Degrees from true north, or null.
	/// <para>
	/// <strong>Null means "no direction", never north.</strong> Zero is due north and a perfectly
	/// good bearing, so a non-nullable column defaulting to zero would silently claim every fuel
	/// stop points that way.
	/// </para>
	/// </summary>
	public short? DirectionDeg { get; set; }

	/// <summary>A key from the curated set — never a user-supplied image (§16.2).</summary>
	public string Icon { get; set; } = string.Empty;

	/// <summary>Rendered on the map beside the icon.</summary>
	public string Title { get; set; } = string.Empty;

	/// <summary>Shown on tap. Plain text, always.</summary>
	public string? Note { get; set; }

	/// <summary>
	/// The attached image, or null (§16.4).
	/// <para>
	/// One, and optional. A gallery per marker multiplies storage, needs ordering and a carousel,
	/// and none of it serves <em>"here is the pothole"</em>.
	/// </para>
	/// <para>
	/// Nullable and attached afterwards rather than supplied at creation, because the photograph is
	/// taken at the top of the hill and the hill is where there is no signal. The marker appears on
	/// every member's map at once and the image follows out of the outbox; binding the two together
	/// would mean no marker until the ride was over.
	/// </para>
	/// </summary>
	public Guid? PhotoId { get; set; }

	/// <summary>When it was placed.</summary>
	public DateTimeOffset CreatedUtc { get; set; }

	/// <summary>When it was last edited.</summary>
	public DateTimeOffset UpdatedUtc { get; set; }

	/// <summary>The track parent, for cascade deletion.</summary>
	public Track? Track { get; set; }

	/// <summary>The ride parent, for cascade deletion.</summary>
	public GroupRide? Ride { get; set; }

	/// <summary>The author.</summary>
	public AppUser? CreatedBy { get; set; }

	/// <summary>The attached image.</summary>
	public Photo? Photo { get; set; }
}
