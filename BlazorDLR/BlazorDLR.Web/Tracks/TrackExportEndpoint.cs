using System.Text;
using DLR.Core.Contracts.Rides;
using DLR.Core.Markers;
using DLR.Core.Tracks;
using DLR.Server.Data;
using DLR.Server.Data.Markers;
using DLR.Server.Data.Tracks;
using DLR.Server.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tracks;

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
public static class TrackExportEndpoint
{
	/// <summary>Route name.</summary>
	public const string RouteName = "ExportTrackGpx";
}

/// <summary>
/// <c>GET /api/v1/tracks/{id}/gpx</c> — the track and its markers, back out again (§16.6).
/// <para>
/// A file exported here and re-imported produces the same markers, which is the test that says the
/// mapping is honest rather than merely present. Photos are not in GPX and are not attempted: the
/// export is a <c>.gpx</c>, not an archive.
/// </para>
/// </summary>
[ApiController]
[Authorize]
public sealed class TrackExportController : ControllerBase
{
	[HttpGet("/api/v1/tracks/{id:guid}/gpx", Name = TrackExportEndpoint.RouteName)]
	[EndpointSummary("The track as GPX, with its markers as waypoints.")]
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

		List<Marker> markers = await database
			.Set<Marker>()
			.AsNoTracking()
			.Where(marker => marker.TrackId == id)
			.OrderBy(marker => marker.CreatedUtc)
			.ToListAsync();

		string gpx = GpxWriter.Write(
			track.Name ?? "Track",
			geometry.Points,
			[
				.. markers.Select(marker => new GpxWaypointOut(
					PositionScale.ToDegrees(marker.Lat),
					PositionScale.ToDegrees(marker.Lon),
					marker.Title,
					marker.Note,
					MarkerIcons.ToGpxSymbol(marker.Icon),
					marker.DirectionDeg)),
			]);

		return File(
			Encoding.UTF8.GetBytes(gpx),
			"application/gpx+xml",
			$"{Filename(track.Name ?? "track")}.gpx");
	}

	/// <summary>
	/// A filename a filesystem will accept, from a name a rider typed.
	/// </summary>
	private static string Filename(string name)
	{
		char[] cleaned = [.. name.Select(character =>
			Path.GetInvalidFileNameChars().Contains(character) ? '-' : character)];

		string trimmed = new string(cleaned).Trim();

		return trimmed.Length == 0 ? "track" : trimmed;
	}
}
