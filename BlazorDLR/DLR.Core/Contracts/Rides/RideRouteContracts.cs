using DLR.Core.Tracks;

namespace DLR.Core.Contracts.Rides;

/// <summary>
/// One track attached to a group ride as a planned route (§5.2, §5.4).
/// <para>
/// <strong>A ride has a list of these, not one.</strong> The original outline said
/// <c>PUT /group-rides/{id}/route</c> - attach or replace a single planned route - and a real
/// day out does not fit that shape: the short option and the long option, the way out and the
/// way home, a detour somebody added the night before. So the attachment is a set, and the
/// screens draw all of it.
/// </para>
/// <para>
/// <strong>The line travels encoded, not as an array of points.</strong> Same reasoning as
/// §15.5's editor payload: a simplified route is still thousands of points, and this list is
/// fetched by every member on every load of the ride. <see cref="PolylineCodec"/> is the one
/// implementation of the encoding on both sides, so there is no second decoder to disagree
/// with the server about precision.
/// </para>
/// </summary>
/// <param name="TrackId">The track this route is.</param>
/// <param name="Name">
/// What the track is called, or null when it was never named - nullable for the same reason
/// <see cref="Tracks.TrackSummary.Name"/> is, so a screen chooses its own placeholder rather
/// than the server inventing one.
/// </param>
/// <param name="DistanceM">Metres along the ground.</param>
/// <param name="PointCount">Points in the full-resolution track, not in the encoded line.</param>
/// <param name="EncodedPolyline">The simplified line, for display (§15.5).</param>
/// <param name="Bounds">The box to frame the map on, or null for a track with no points.</param>
/// <param name="AddedUtc">When it was attached. Also what the list is ordered by.</param>
/// <param name="AddedByUserId">Who attached it.</param>
/// <param name="AddedByUserName">Their handle, so the panel names them without a second call.</param>
public sealed record RideRoute(
	Guid TrackId,
	string? Name,
	double DistanceM,
	int PointCount,
	string EncodedPolyline,
	TrackBounds? Bounds,
	DateTimeOffset AddedUtc,
	Guid AddedByUserId,
	string AddedByUserName);

/// <summary>
/// <c>POST /api/v1/group-rides/{id}/routes</c> (§5.2).
/// <para>
/// The track must be the caller's own. §15.4 already draws that line for editing - "not the
/// group-ride organiser, even for a route they were handed" - and attaching is the same
/// question asked in the other direction: a rider hands a route over by exporting the GPX,
/// which is the copy feature, so no endpoint needs to reach into somebody else's library.
/// </para>
/// </summary>
/// <param name="TrackId">Which of the caller's tracks to attach.</param>
public sealed record AddRideRouteRequest(Guid TrackId);
