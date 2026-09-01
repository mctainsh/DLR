namespace DLR.Core.Contracts.Tracks;

/// <summary>Which GPX element a track was read from (§15.3).</summary>
public enum ImportedFrom
{
	/// <summary>A recorded <c>&lt;trk&gt;</c>.</summary>
	Track = 0,

	/// <summary>A planned <c>&lt;rte&gt;</c>, imported without timestamps.</summary>
	Route = 1,
}

/// <summary>
/// One track a GPX file yielded (§15.3).
/// </summary>
/// <param name="TrackId">
/// What was created, or null on a dry run - which is the one difference between a preview and
/// a commit, and the reason both use this shape.
/// </param>
/// <param name="Name">From the file, if it named it.</param>
/// <param name="From">A recorded track or a planned route.</param>
/// <param name="PointCount">Points read.</param>
/// <param name="SegmentCount">Segments; more than one means a pause or a signal gap.</param>
/// <param name="DistanceM">Metres, never summed across a break.</param>
/// <param name="DurationS">Seconds, or null for a route or a non-monotonic clock.</param>
/// <param name="AscentM">Metres climbed, or null when the file carried no elevation.</param>
/// <param name="DuplicateOfTrackId">
/// An existing track of the caller's with identical content (§15.3). Detection, not prevention:
/// the import proceeds, because a second copy to edit differently is legitimate and doing it by
/// accident is the common case. A warning serves both.
/// </param>
/// <param name="DuplicateImportedUtc">When that earlier copy was stored, for "you imported this on…".</param>
public sealed record ImportedTrackResult(
	Guid? TrackId,
	string? Name,
	ImportedFrom From,
	int PointCount,
	int SegmentCount,
	double DistanceM,
	double? DurationS,
	double? AscentM,
	Guid? DuplicateOfTrackId = null,
	DateTimeOffset? DuplicateImportedUtc = null);

/// <summary>
/// What a GPX file produced, or would produce (§15.3).
/// <para>
/// Preview is <c>?dryRun=true</c> against this same endpoint, not server-side staging. Holding a
/// parsed result between two calls would need its own storage, its own expiry sweep and its own
/// orphan cleanup - a whole mechanism to save re-uploading a file capped at 25 MB. On the app
/// the preview costs nothing at all, because the parse already happened locally.
/// </para>
/// </summary>
/// <param name="DryRun">Whether anything was persisted.</param>
/// <param name="Tracks">One per <c>&lt;trk&gt;</c> and per <c>&lt;rte&gt;</c>.</param>
/// <param name="WaypointCount">
/// How many markers this file would create (§16.6). Counted here from SRV-14 onwards; the
/// markers themselves are persisted in SRV-26.
/// </param>
/// <param name="TracksTruncated">Whether the file held more tracks than the cap accepts.</param>
public sealed record TrackImportResult(
	bool DryRun,
	IReadOnlyList<ImportedTrackResult> Tracks,
	int WaypointCount,
	bool TracksTruncated);

/// <summary>
/// Full-resolution points for the editor (§15.5).
/// <para>
/// Encoded rather than an array of objects: a 12-hour tour at 1 Hz is ~43 000 points, which is
/// roughly 200 KB gzipped this way and several megabytes as JSON. The response is compressed on
/// the wire.
/// </para>
/// </summary>
/// <param name="Version">
/// What an edit must quote back (§15.5). Sent with the points because the two have to agree:
/// indices read against one version and applied to another cut the wrong span.
/// </param>
/// <param name="PointCount">How many points the encoding holds.</param>
/// <param name="Polyline">Coordinates, encoded-polyline at precision 6.</param>
/// <param name="TimeOffsets">
/// Seconds from the first point, delta-encoded, or null for a track with no timestamps.
/// </param>
/// <param name="ElevationDecimetres">
/// Decimetres, delta-encoded, or null when the track carried no elevation. A point that is
/// missing one inside a track that has them is <c>PolylineCodec.MissingElevation</c>, never
/// zero - zero is a real height.
/// </param>
/// <param name="SegmentStarts">Indices where each segment begins; a pause or a signal gap.</param>
public sealed record TrackPointsResponse(
	int Version,
	int PointCount,
	string Polyline,
	string? TimeOffsets,
	string? ElevationDecimetres,
	IReadOnlyList<int> SegmentStarts);
