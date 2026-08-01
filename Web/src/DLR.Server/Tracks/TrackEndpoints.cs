using System.Security.Claims;
using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.Server.Data;
using DLR.Server.Data.Tracks;
using DLR.Server.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tracks;

/// <summary>Uploading, listing and reading a track (§6.2, §6.3).</summary>
public static class TrackEndpoints
{
	/// <summary>Route name for upload.</summary>
	public const string UploadRouteName = "UploadTrack";

	/// <summary>Route name for the list.</summary>
	public const string ListRouteName = "ListTracks";

	/// <summary>Route name for the detail.</summary>
	public const string DetailRouteName = "GetTrack";

	/// <summary>Maps the three track endpoints.</summary>
	public static IEndpointRouteBuilder MapTracks(this IEndpointRouteBuilder endpoints)
	{
		endpoints
			.MapPost("/api/v1/tracks", UploadAsync)
			.RequireAuthorization()
			.WithName(UploadRouteName)
			.WithSummary("Stores a track. Idempotent on the client's own identifier.");

		endpoints
			.MapGet("/api/v1/tracks", ListAsync)
			.RequireAuthorization()
			.WithName(ListRouteName)
			.WithSummary("The caller's tracks, newest first.");

		endpoints
			.MapGet("/api/v1/tracks/{id:guid}", DetailAsync)
			.RequireAuthorization()
			.WithName(DetailRouteName)
			.WithSummary("One track's metadata and its simplified line.");

		return endpoints;
	}

	/// <summary>
	/// Recording is not gated by §7.8's ladder, deliberately. The restriction targets the
	/// social surface, which is what abuse would be after; stopping a restricted account
	/// keeping its own rides would punish the wrong people for nothing.
	/// </summary>
	private static async Task<IResult> UploadAsync(
		UploadTrackRequest request,
		ClaimsPrincipal caller,
		DlrDbContext database,
		TrackStore tracks,
		IBlobStore blobs)
	{
		if (caller.UserId() is not { } ownerId)
		{
			return Results.Unauthorized();
		}

		if (request.Points.Count < TrackEditor.MinimumSurvivingPoints)
		{
			return Problem(
				"Not a track",
				$"A track needs at least {TrackEditor.MinimumSurvivingPoints} points; " +
				$"this upload has {request.Points.Count}.");
		}

		if (request.Points.Any(point => !point.HasUsableCoordinates))
		{
			// The app parses GPX with the same reader the server does (§15.7), but a
			// client-supplied point list is untrusted input regardless of which of our own
			// clients produced it (§15.2).
			return Problem("Invalid coordinates", "A point is outside the possible range.");
		}

		// Checked before the blob is written. Losing the race still leaves nothing behind,
		// because the unique index below is what actually decides — this only avoids doing the
		// work twice in the common case.
		Track? existing = await database
			.Set<Track>()
			.SingleOrDefaultAsync(track =>
				track.OwnerId == ownerId && track.ClientGuid == request.ClientGuid);

		if (existing is not null)
		{
			return Results.Ok(TrackStore.Summarise(existing));
		}

		TrackGeometry geometry = new(request.Points, request.SegmentStarts);

		Track track = await tracks.StageAsync(
			ownerId,
			request.ClientGuid,
			geometry,
			request.Name,
			request.Source == TrackSourceDto.Imported ? TrackSource.Imported : TrackSource.Recorded,
			request.ImportedFileName);

		try
		{
			await database.SaveChangesAsync();
		}
		catch (DbUpdateException)
		{
			// Two drains of the same outbox arrived together and the unique index decided
			// between them. The loser tidies up after itself rather than leaving a blob that
			// nothing points at for the §7.11 sweep to find.
			await tracks.DiscardAsync([track]);

			Track? winner = await database
				.Set<Track>()
				.AsNoTracking()
				.SingleOrDefaultAsync(row =>
					row.OwnerId == ownerId && row.ClientGuid == request.ClientGuid);

			if (winner is null)
			{
				// Something else failed, and swallowing it would turn a broken write into a
				// silent success.
				throw;
			}

			return Results.Ok(TrackStore.Summarise(winner));
		}

		return Results.Created($"/api/v1/tracks/{track.Id}", TrackStore.Summarise(track));
	}

	private static async Task<IResult> ListAsync(ClaimsPrincipal caller, DlrDbContext database)
	{
		if (caller.UserId() is not { } ownerId)
		{
			return Results.Unauthorized();
		}

		// created_utc, not started_utc (§6.2). An imported route has no start time at all, and
		// a ride imported today belongs at the top of the list whenever it was ridden.
		List<Track> rows = await database
			.Set<Track>()
			.AsNoTracking()
			.OwnedBy(ownerId)
			.ToListAsync();

		return Results.Ok(rows.Select(TrackStore.Summarise).ToList());
	}

	private static async Task<IResult> DetailAsync(
		Guid id,
		ClaimsPrincipal caller,
		DlrDbContext database)
	{
		if (caller.UserId() is not { } ownerId)
		{
			return Results.Unauthorized();
		}

		// Scoped to the owner, and 404 rather than 403 for anybody else's. Sharing arrives with
		// the §6.3 share endpoint; until it does, a distinguishable answer would only be a way
		// to ask whether a track id exists.
		Track? track = await database
			.Set<Track>()
			.AsNoTracking()
			.SingleOrDefaultAsync(row => row.Id == id && row.OwnerId == ownerId);

		if (track is null)
		{
			return Results.NotFound();
		}

		using MemoryStream polyline = new(track.SimplifiedPolyline);

		TrackGeometry simplified = TrackBlobCodec.Read(polyline);

		return Results.Ok(new TrackDetail(
			TrackStore.Summarise(track),
			new TrackBounds(
				track.BoundsMinLat,
				track.BoundsMinLon,
				track.BoundsMaxLat,
				track.BoundsMaxLon),
			simplified.Points));
	}

	private static IResult Problem(string title, string detail) =>
		Results.Problem(new ProblemDetails
		{
			Status = StatusCodes.Status400BadRequest,
			Title = title,
			Detail = detail,
		});
}
