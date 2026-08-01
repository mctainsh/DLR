using System.Security.Claims;
using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.Server.Data;
using DLR.Server.Data.Tracks;
using DLR.Server.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DLR.Server.Tracks;

/// <summary>How long a pre-edit original is kept (§15.6, §15.8).</summary>
public sealed class TrackEditOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "Tracks";

	/// <summary>Days a retained original stays restorable.</summary>
	public int EditUndoDays { get; set; } = 7;
}

/// <summary>Editing a track, undoing it, and purging the original (§15.5, §15.6).</summary>
public static class TrackEditEndpoints
{
	/// <summary>Route name for the edit.</summary>
	public const string EditRouteName = "EditTrack";

	/// <summary>Route name for undo.</summary>
	public const string UndoRouteName = "UndoTrackEdit";

	/// <summary>Route name for purging the retained original.</summary>
	public const string PurgeRouteName = "PurgeTrackRevision";

	/// <summary>Maps the three editing endpoints.</summary>
	public static IEndpointRouteBuilder MapTrackEditing(this IEndpointRouteBuilder endpoints)
	{
		endpoints
			.MapPost("/api/v1/tracks/{id:guid}/edit", EditAsync)
			.RequireAuthorization()
			.WithName(EditRouteName)
			.WithSummary("Removes half-open ranges of raw point indices.");

		endpoints
			.MapPost("/api/v1/tracks/{id:guid}/edit/undo", UndoAsync)
			.RequireAuthorization()
			.WithName(UndoRouteName)
			.WithSummary("Restores the pre-edit original, within the undo window.");

		endpoints
			.MapDelete("/api/v1/tracks/{id:guid}/previous-version", PurgeAsync)
			.RequireAuthorization()
			.WithName(PurgeRouteName)
			.WithSummary("Deletes the retained original now, without waiting for the window.");

		return endpoints;
	}

	private static async Task<IResult> EditAsync(
		Guid id,
		EditTrackRequest request,
		ClaimsPrincipal caller,
		DlrDbContext database,
		IBlobStore blobs,
		TimeProvider clock,
		IOptions<TrackEditOptions> options)
	{
		if (caller.UserId() is not { } callerId)
		{
			return Results.Unauthorized();
		}

		Track? track = await database.Set<Track>().SingleOrDefaultAsync(row => row.Id == id);

		if (track is null)
		{
			return Results.NotFound();
		}

		// 403 rather than 404, which is the one place in this API that distinction goes the
		// other way (§15.4). A share link makes a track's id legitimately known to people who
		// do not own it, so "you cannot edit this" is the honest answer — and a recipient who
		// wants their own variant exports the GPX and re-imports it.
		if (track.OwnerId != callerId)
		{
			return Results.Problem(
				statusCode: StatusCodes.Status403Forbidden,
				title: "Not your track",
				detail: "Only the owner may edit a track. Export it and import your own copy " +
					"if you want a variant.");
		}

		if (!track.IsFullyUploaded)
		{
			return Conflict(
				"Still uploading",
				"This track is still being uploaded at full resolution. You can edit it once " +
				"that finishes.");
		}

		if (track.Version != request.Version)
		{
			return Conflict(
				"Track has changed",
				$"This edit was written against version {request.Version} and the track is now " +
				$"version {track.Version}. Reload it — the indices in the edit no longer point " +
				"at the same places.");
		}

		// The Live-ride precondition (§15.4) attaches in SRV-20. Changing the planned route of
		// a ride in progress silently moves every rider's position in the gap list.

		TrackGeometry geometry = await ReadAsync(blobs, track.BlobRef);

		TrackEditResult edit = TrackEditor.Remove(
			geometry,
			[.. request.Removals.Select(range => new PointRange(range.From, range.To))]);

		if (!edit.IsValid)
		{
			return Results.Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "That edit cannot be applied",
				detail: edit.Message);
		}

		string previousBlob = track.BlobRef;

		await ApplyAsync(track, edit.Result, blobs, clock);

		// The pre-edit blob becomes the retained original, replacing whatever was there and
		// restarting the clock (§15.6). One row per track: undo is a safety net for the last
		// action, not a history feature.
		DateTimeOffset now = clock.GetUtcNow();

		TrackRevision? revision = await database
			.Set<TrackRevision>()
			.SingleOrDefaultAsync(row => row.TrackId == track.Id);

		if (revision is null)
		{
			revision = new TrackRevision { TrackId = track.Id };

			database.Add(revision);
		}
		else
		{
			// The one it displaced is gone for good; keeping it would be the history feature
			// §15.6 declines to build, at a third of the storage on a 40 GB disk.
			await blobs.DeleteAsync(revision.BlobRef);
		}

		revision.Version = track.Version - 1;
		revision.BlobRef = previousBlob;
		revision.ReplacedUtc = now;
		revision.PurgeAfterUtc = now.AddDays(options.Value.EditUndoDays);

		await database.SaveChangesAsync();

		return Results.Ok(new TrackEditResponse(TrackStore.Summarise(track), revision.PurgeAfterUtc));
	}

	/// <summary>
	/// Undo is itself an edit, not a rewind: the restored points become a <em>new</em> version,
	/// so the version chain only ever moves forward (§15.6). A device holding a cached copy
	/// replaces it on the version number either way, and never has to reason about going
	/// backwards.
	/// </summary>
	private static async Task<IResult> UndoAsync(
		Guid id,
		ClaimsPrincipal caller,
		DlrDbContext database,
		IBlobStore blobs,
		TimeProvider clock)
	{
		if (caller.UserId() is not { } callerId)
		{
			return Results.Unauthorized();
		}

		Track? track = await database.Set<Track>().SingleOrDefaultAsync(row => row.Id == id);

		if (track is null || track.OwnerId != callerId)
		{
			return Results.NotFound();
		}

		TrackRevision? revision = await database
			.Set<TrackRevision>()
			.SingleOrDefaultAsync(row => row.TrackId == id);

		// The clock decides, not whether the nightly sweep has run yet. Two answers to "can I
		// undo this" is one too many.
		if (revision is null || clock.GetUtcNow() >= revision.PurgeAfterUtc)
		{
			return Results.NotFound();
		}

		TrackGeometry original = await ReadAsync(blobs, revision.BlobRef);

		string editedBlob = track.BlobRef;

		await ApplyAsync(track, original, blobs, clock);

		// The revision is consumed rather than replaced by the edited state. §15.6 is explicit
		// that this is a safety net for the last action — turning it into a redo would make it
		// the history feature it declines to be.
		database.Remove(revision);

		await database.SaveChangesAsync();

		await blobs.DeleteAsync(revision.BlobRef);
		await blobs.DeleteAsync(editedBlob);

		return Results.Ok(new TrackEditResponse(TrackStore.Summarise(track), null));
	}

	/// <summary>
	/// For the rider who has just trimmed their home address off a track and does not want to
	/// wait seven days for it to go (§15.6).
	/// </summary>
	private static async Task<IResult> PurgeAsync(
		Guid id,
		ClaimsPrincipal caller,
		DlrDbContext database,
		IBlobStore blobs)
	{
		if (caller.UserId() is not { } callerId)
		{
			return Results.Unauthorized();
		}

		bool isOurs = await database
			.Set<Track>()
			.AnyAsync(track => track.Id == id && track.OwnerId == callerId);

		if (!isOurs)
		{
			return Results.NotFound();
		}

		TrackRevision? revision = await database
			.Set<TrackRevision>()
			.SingleOrDefaultAsync(row => row.TrackId == id);

		if (revision is null)
		{
			// Nothing to purge is the state the caller asked for, so it is not an error. The
			// nightly sweep and an impatient rider must not race each other into a 404.
			return Results.NoContent();
		}

		database.Remove(revision);

		await database.SaveChangesAsync();

		await blobs.DeleteAsync(revision.BlobRef);

		return Results.NoContent();
	}

	/// <summary>
	/// Writes a new blob and rebuilds every derived figure from the surviving points (§15.5).
	/// <para>
	/// The simplified line and the content hash are regenerated too. A stale polyline would
	/// keep drawing the trimmed span on a map, which for the privacy case is the entire
	/// failure.
	/// </para>
	/// </summary>
	private static async Task ApplyAsync(
		Track track,
		TrackGeometry geometry,
		IBlobStore blobs,
		TimeProvider clock)
	{
		using MemoryStream points = new();

		TrackBlobCodec.Write(geometry, points);
		points.Position = 0;

		track.BlobRef = await blobs.PutAsync(points);

		using MemoryStream simplified = new();

		TrackBlobCodec.Write(TrackSimplifier.Simplify(geometry), simplified);

		track.SimplifiedPolyline = simplified.ToArray();
		track.ContentHash = TrackBlobCodec.ContentHash(geometry);

		TrackStats stats = TrackStats.From(geometry);

		track.DistanceM = stats.DistanceM;
		track.DurationS = stats.DurationS;
		track.AscentM = stats.AscentM;
		track.MaxSpeedMps = stats.MaxSpeedMps;
		track.StartedUtc = stats.StartedUtc;
		track.EndedUtc = stats.EndedUtc;
		track.PointCount = stats.PointCount;
		track.SegmentCount = stats.SegmentCount;

		if (stats.Bounds is { } bounds)
		{
			track.BoundsMinLat = bounds.MinLatitude;
			track.BoundsMinLon = bounds.MinLongitude;
			track.BoundsMaxLat = bounds.MaxLatitude;
			track.BoundsMaxLon = bounds.MaxLongitude;
		}

		track.Version++;
		track.EditedUtc = clock.GetUtcNow();
	}

	private static async Task<TrackGeometry> ReadAsync(IBlobStore blobs, string blobRef)
	{
		await using Stream? blob = await blobs.OpenAsync(blobRef)
			?? throw new InvalidOperationException($"Track blob '{blobRef}' is missing.");

		return TrackBlobCodec.Read(blob);
	}

	private static IResult Conflict(string title, string detail) =>
		Results.Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail);
}
