using DLR.Core.Tracks;

namespace DLR.Core.Contracts.Tracks;

/// <summary>How a track came to exist (§15.1). Mirrors the persisted enum.</summary>
public enum TrackSourceDto
{
	/// <summary>Recorded in the app.</summary>
	Recorded = 0,

	/// <summary>Imported from a file.</summary>
	Imported = 1,
}

/// <summary>
/// <c>POST /api/v1/tracks</c> (§6.3).
/// <para>
/// Points travel as JSON here. That is honest for the sizes this endpoint sees today and
/// deliberately not the last word: §15.5 notes a 12-hour tour at 1 Hz is ~43 000 points, and
/// when the recorder exists this wants the compact encoding rather than an array of objects.
/// </para>
/// </summary>
/// <param name="ClientGuid">
/// Generated on the device before it had a server (§4.4). What makes the upload idempotent: a
/// phone draining its outbox over a flaky connection re-sends what it never saw acknowledged.
/// </param>
/// <param name="Points">Every point, in order.</param>
/// <param name="SegmentStarts">Indices where a segment begins; a pause or a signal gap (§15.3).</param>
/// <param name="Name">What to call it.</param>
/// <param name="Source">Recorded or imported.</param>
/// <param name="ImportedFileName">The file it came from, when it came from one.</param>
public sealed record UploadTrackRequest(
	Guid ClientGuid,
	IReadOnlyList<TrackPoint> Points,
	IReadOnlyList<int>? SegmentStarts = null,
	string? Name = null,
	TrackSourceDto Source = TrackSourceDto.Recorded,
	string? ImportedFileName = null);

/// <summary>
/// <c>PATCH /api/v1/tracks/{id}</c> — what a stored track is called (§15.1).
/// <para>
/// A rename is not an edit: it moves no point, so it does not touch
/// <see cref="TrackSummary.Version"/> and carries none. Quoting a version here would mean a rider
/// correcting a typo could be refused because a different device had trimmed the line, which is a
/// conflict between two changes that cannot conflict.
/// </para>
/// </summary>
/// <param name="Name">
/// The new name. Required — the endpoint refuses a blank one rather than quietly clearing the
/// name, which is what "rename it to nothing" would otherwise mean.
/// </param>
public sealed record RenameTrackRequest(string Name);

/// <summary>
/// One row of the track list (§6.2).
/// <para>
/// Every time-derived figure is nullable and rendered as "—" rather than <c>0</c>. A route has a
/// distance and a shape but no duration; zero would claim a measurement nobody took (§8).
/// </para>
/// </summary>
/// <param name="Id">The track.</param>
/// <param name="Name">What it is called.</param>
/// <param name="CreatedUtc">When the server stored it. What the list sorts on.</param>
/// <param name="StartedUtc">When the ride began, if it was a ride.</param>
/// <param name="EndedUtc">When it finished, if it was a ride.</param>
/// <param name="DistanceM">Metres.</param>
/// <param name="DurationS">Seconds, or null.</param>
/// <param name="AscentM">Metres climbed, or null.</param>
/// <param name="MaxSpeedMps">Fastest leg, or null.</param>
/// <param name="PointCount">Points.</param>
/// <param name="SegmentCount">Segments.</param>
/// <param name="Source">Recorded or imported.</param>
/// <param name="Version">Increments on every edit; an edit quotes it back (§15.5).</param>
public sealed record TrackSummary(
	Guid Id,
	string? Name,
	DateTimeOffset CreatedUtc,
	DateTimeOffset? StartedUtc,
	DateTimeOffset? EndedUtc,
	double DistanceM,
	double? DurationS,
	double? AscentM,
	double? MaxSpeedMps,
	int PointCount,
	int SegmentCount,
	TrackSourceDto Source,
	int Version);

/// <summary>
/// A track and the line the map draws (§6.3).
/// </summary>
/// <param name="Track">The metadata.</param>
/// <param name="Bounds">The box to frame the map on.</param>
/// <param name="Polyline">
/// The simplified line, for display only. An edit never addresses these indices — it works
/// against the full-resolution points from <c>GET /tracks/{id}/points</c> (§15.5).
/// </param>
public sealed record TrackDetail(
	TrackSummary Track,
	TrackBounds? Bounds,
	IReadOnlyList<TrackPoint> Polyline);
