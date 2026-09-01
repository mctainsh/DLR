using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.Server.Data;
using DLR.Server.Data.Tracks;
using DLR.Server.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tracks;

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
public static class TrackPointsEndpoint
{
	/// <summary>Route name.</summary>
	public const string RouteName = "GetTrackPoints";
}

/// <summary>
/// <c>GET /api/v1/tracks/{id}/points</c> - the editor's source (§15.5).
/// <para>
/// The map draws a simplified polyline for performance, and the editor must not. If the browser
/// sent "delete the 412th point I am displaying" it would delete a different point on the
/// server - plausibly hundreds of metres away, invisibly, and only on tracks dense enough for
/// simplification to have done anything. So the editor loads the real thing, and the indices it
/// manipulates are the server's indices throughout. One index space, no mapping layer, no class
/// of bug.
/// </para>
/// </summary>
[ApiController]
[Authorize]
public sealed class TrackPointsController : ControllerBase
{
	[HttpGet("/api/v1/tracks/{id:guid}/points", Name = TrackPointsEndpoint.RouteName)]
	[EndpointSummary("Full-resolution points, encoded, for the editor.")]
	public async Task<IActionResult> GetAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] IBlobStore blobs)
	{
		if (User.UserId() is not { } callerId)
		{
			return Unauthorized();
		}

		Track? track = await database
			.Set<Track>()
			.AsNoTracking()
			.SingleOrDefaultAsync(row => row.Id == id && row.OwnerId == callerId);

		if (track is null)
		{
			return NotFound();
		}

		await using Stream? blob = await blobs.OpenAsync(track.BlobRef);

		if (blob is null)
		{
			return Problem(
				statusCode: StatusCodes.Status500InternalServerError,
				title: "Track points are missing",
				detail: "The stored points for this track could not be read.");
		}

		TrackGeometry geometry = TrackBlobCodec.Read(blob);

		IReadOnlyList<long> times = PolylineCodec.TimeOffsetSeconds(geometry.Points);

		bool hasElevation = geometry.Points.Any(point => point.ElevationM is not null);

		return Ok(new TrackPointsResponse(
			track.Version,
			geometry.Points.Count,
			PolylineCodec.EncodePoints(geometry.Points),

			// Absent rather than a run of zeroes. A route has no timestamps at all, and zero is
			// a time (§15.1).
			times.Count == 0 ? null : PolylineCodec.EncodeValues(times),

			hasElevation
				? PolylineCodec.EncodeValues(PolylineCodec.ElevationDecimetres(geometry.Points))
				: null,

			[.. geometry.SegmentStarts]));
	}
}
