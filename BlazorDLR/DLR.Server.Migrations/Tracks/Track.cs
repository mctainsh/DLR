using DLR.Server.Data.Identity;

namespace DLR.Server.Data.Tracks;

/// <summary>How a track came to exist (§15.1).</summary>
public enum TrackSource
{
	/// <summary>The rider turned <em>save track</em> on and off in the app (§4.2).</summary>
	Recorded = 0,

	/// <summary>Read from a <c>.gpx</c> file, on the app or the website (§15.2).</summary>
	Imported = 1,
}

/// <summary>Who can see a track.</summary>
public enum TrackVisibility
{
	/// <summary>The owner, and nobody else. The default, and the only state SRV-16 sets.</summary>
	Private = 0,

	/// <summary>Anyone holding the share link (§6.3's share endpoint, a later task).</summary>
	Link = 1,

	/// <summary>Anyone.</summary>
	Public = 2,
}

/// <summary>
/// A ride or a route (§8, §15.1).
/// <para>
/// Recorded and imported tracks are the same entity on purpose: one list, one detail screen, one
/// share model, one GPX export, and either can be attached to a group ride as a planned route.
/// <see cref="Source"/> records which is which for display and for support, never for
/// authorisation.
/// </para>
/// <para>
/// The points are not here. They live in a blob (§9.1) — write-once and read-whole, so a row per
/// point would be a mistake, and an edit rewrites the blob whole rather than mutating points in
/// place (§15.5).
/// </para>
/// </summary>
public sealed class Track
{
	/// <summary>Row identifier.</summary>
	public Guid Id { get; set; }

	/// <summary>Whose track.</summary>
	public Guid OwnerId { get; set; }

	/// <summary>
	/// The identifier the client generated before it had a server (§4.4, §6.3).
	/// <para>
	/// What makes the upload idempotent. A phone drains its outbox over a flaky connection and
	/// will re-send an upload it never saw acknowledged — without this, a ride recorded in a
	/// tunnel appears three times.
	/// </para>
	/// </summary>
	public Guid ClientGuid { get; set; }

	/// <summary>What the rider called it, or what the file did.</summary>
	public string? Name { get; set; }

	/// <summary>When the server first stored it. What the track list sorts on.</summary>
	public DateTimeOffset CreatedUtc { get; set; }

	/// <summary>First timestamp, or null for a route (§15.1).</summary>
	public DateTimeOffset? StartedUtc { get; set; }

	/// <summary>Last timestamp, or null for a route.</summary>
	public DateTimeOffset? EndedUtc { get; set; }

	/// <summary>Metres along the ground, never summed across a segment break.</summary>
	public double DistanceM { get; set; }

	/// <summary>Seconds ridden, or null for a route. Never zero in place of null (§8).</summary>
	public double? DurationS { get; set; }

	/// <summary>Metres climbed, or null when the source carried no elevation.</summary>
	public double? AscentM { get; set; }

	/// <summary>Fastest leg, or null for a route.</summary>
	public double? MaxSpeedMps { get; set; }

	/// <summary>Southern edge of the bounding box.</summary>
	public double BoundsMinLat { get; set; }

	/// <summary>Western edge.</summary>
	public double BoundsMinLon { get; set; }

	/// <summary>Northern edge.</summary>
	public double BoundsMaxLat { get; set; }

	/// <summary>Eastern edge.</summary>
	public double BoundsMaxLon { get; set; }

	/// <summary>Points in the track.</summary>
	public int PointCount { get; set; }

	/// <summary>Segments, which is one more than the number of breaks.</summary>
	public int SegmentCount { get; set; }

	/// <summary>Who can see it.</summary>
	public TrackVisibility Visibility { get; set; }

	/// <summary>Where the full-resolution points are (§9.1).</summary>
	public string BlobRef { get; set; } = string.Empty;

	/// <summary>
	/// The reduced line the map draws (§4.2). Never what an edit addresses — the editor works
	/// in full-resolution indices throughout (§15.5).
	/// </summary>
	public byte[] SimplifiedPolyline { get; set; } = [];

	/// <summary>Recorded or imported.</summary>
	public TrackSource Source { get; set; }

	/// <summary>
	/// Incremented on every edit. The device replaces its cached copy rather than merging
	/// (§4.4, §15.4), and an edit against a stale version is a <c>409</c> (§15.5).
	/// </summary>
	public int Version { get; set; }

	/// <summary>When it was last edited, if it has been.</summary>
	public DateTimeOffset? EditedUtc { get; set; }

	/// <summary>
	/// SHA-256 over the normalised point stream (§15.3). Duplicate <em>detection</em>, not
	/// prevention: importing a file whose hash matches an existing track of the same owner
	/// warns and proceeds if the rider says so.
	/// </summary>
	public byte[] ContentHash { get; set; } = [];

	/// <summary>The file it was imported from, for display and for support.</summary>
	public string? ImportedFileName { get; set; }

	/// <summary>The format it arrived in.</summary>
	public string? ImportedFormat { get; set; }

	/// <summary>
	/// Whether the device has finished handing over the full-resolution points (§15.4).
	/// <para>
	/// A track has exactly one writer at any moment: the recording device owns it until the
	/// upload completes, the server owns it from then on, and the device never edits. Editing
	/// before that hand-over would have the server rewriting a display copy while the device
	/// still held the real one.
	/// </para>
	/// <para>
	/// Always true for a single-request upload or an import, which is every path that exists
	/// today. The chunked upload that can set it false arrives with the recorder.
	/// </para>
	/// </summary>
	public bool IsFullyUploaded { get; set; } = true;

	/// <summary>The account, for cascade deletion.</summary>
	public AppUser? Owner { get; set; }
}
