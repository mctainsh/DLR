namespace DLR.Core.Tracks;

/// <summary>
/// The caps a GPX read is bounded by (§15.3, §15.8).
/// <para>
/// A class rather than a record struct, and that is not a style choice. A struct's parameterless
/// <c>new()</c> zeroes every field instead of running the primary constructor's defaults, so
/// <c>default(GpxLimits)</c> would be a cap of <em>nothing</em> - a limit object whose accidental
/// value permits no tracks and no points, failing closed here but failing silently in any caller
/// that read the defaults as documentation.
/// </para>
/// </summary>
public sealed record GpxLimits
{
	/// <summary>
	/// Enforced <em>mid-parse</em>, aborting the read the moment it is exceeded. A file can be
	/// small and still be pathological, so a byte cap alone does not bound the point count.
	/// </summary>
	public int MaxPointsPerFile { get; init; } = 500_000;

	/// <summary>How many <c>&lt;trk&gt;</c> and <c>&lt;rte&gt;</c> elements are accepted.</summary>
	public int MaxTracksPerFile { get; init; } = 20;

	/// <summary>The §15.8 defaults.</summary>
	public static GpxLimits Default { get; } = new();
}

/// <summary>One track read out of a GPX file.</summary>
/// <param name="Name">From <c>&lt;name&gt;</c>, if the file supplied one.</param>
/// <param name="Geometry">The points and their segment breaks.</param>
/// <param name="Source">Which element it came from.</param>
public sealed record GpxTrack(string? Name, TrackGeometry Geometry, GpxTrackSource Source);

/// <summary>Which GPX element a track was read from.</summary>
public enum GpxTrackSource
{
	/// <summary>A recorded track: <c>&lt;trk&gt;</c>.</summary>
	Track = 0,

	/// <summary>
	/// A planned route: <c>&lt;rte&gt;</c>, imported as a track with no timestamps.
	/// <para>
	/// Rejecting these would fail the most common import there is - planning tools emit routes,
	/// not tracks (§15.3).
	/// </para>
	/// </summary>
	Route = 1,
}

/// <summary>
/// A <c>&lt;wpt&gt;</c>, which §16.6 turns into a marker.
/// <para>
/// Read here and persisted in SRV-26, so a file's waypoints can be counted in the import
/// preview before there is anywhere to put them.
/// </para>
/// </summary>
/// <param name="Latitude">Degrees.</param>
/// <param name="Longitude">Degrees.</param>
/// <param name="Name">Becomes the marker title.</param>
/// <param name="Description">Becomes the marker note.</param>
/// <param name="Symbol">From <c>&lt;sym&gt;</c>; maps to an icon key.</param>
/// <param name="DirectionDeg">
/// From our own <c>dlr:direction</c> extension (§16.6). Other readers ignore it and we read ours
/// back, which is what makes the round trip lossless without inventing a GPX element nobody else
/// understands.
/// </param>
public sealed record GpxWaypoint(
	double Latitude,
	double Longitude,
	string? Name,
	string? Description,
	string? Symbol,
	short? DirectionDeg = null);

/// <summary>Our GPX extension namespace (§16.6).</summary>
public static class DlrGpx
{
	/// <summary>
	/// The namespace URI for <c>dlr:</c> elements.
	/// <para>
	/// Uses the app's own <c>dlr://</c> scheme rather than an <c>https://</c> domain, because an
	/// XML namespace is an identifier and not an address - nothing should ever fetch it, and
	/// pointing it at a real domain invites something to try.
	/// </para>
	/// </summary>
	public const string Namespace = "dlr://gpx/v1";

	/// <summary>The conventional prefix.</summary>
	public const string Prefix = "dlr";
}

/// <summary>Everything one GPX file yielded.</summary>
/// <param name="Tracks">One per <c>&lt;trk&gt;</c> and per <c>&lt;rte&gt;</c>.</param>
/// <param name="Waypoints">Every <c>&lt;wpt&gt;</c>.</param>
/// <param name="TracksTruncated">
/// Whether the file held more tracks than <see cref="GpxLimits.MaxTracksPerFile"/>. Reported
/// rather than thrown: the preview lists what was read and says what was left, which is more
/// use than refusing a file for having twenty-one tracks in it.
/// </param>
public sealed record GpxDocument(
	IReadOnlyList<GpxTrack> Tracks,
	IReadOnlyList<GpxWaypoint> Waypoints,
	bool TracksTruncated)
{
	/// <summary>Total points across every track, for the preview.</summary>
	public int PointCount => Tracks.Sum(track => track.Geometry.Points.Count);
}
