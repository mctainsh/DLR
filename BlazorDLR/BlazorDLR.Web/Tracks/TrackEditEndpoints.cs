using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.Server.Data;
using DLR.Server.Data.Tracks;
using DLR.Server.Identity;
using Microsoft.AspNetCore.Authorization;
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

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
public static class TrackEditEndpoints
{
	/// <summary>Route name for the edit.</summary>
	public const string EditRouteName = "EditTrack";

	/// <summary>Route name for undo.</summary>
	public const string UndoRouteName = "UndoTrackEdit";

	/// <summary>Route name for purging the retained original.</summary>
	public const string PurgeRouteName = "PurgeTrackRevision";
}

/// <summary>Editing a track, undoing it, and purging the original (§15.5, §15.6).</summary>
[ApiController]
[Authorize]
public sealed class TrackEditController : ControllerBase
{
	[HttpPost("/api/v1/tracks/{id:guid}/edit", Name = TrackEditEndpoints.EditRouteName)]
	[EndpointSummary("Removes half-open ranges of raw point indices.")]
	public async Task<IActionResult> EditAsync(
		[FromRoute] Guid id,
		[FromBody] EditTrackRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] IBlobStore blobs,
		[FromServices] TimeProvider clock,
		[FromServices] IOptions<TrackEditOptions> options)
	{
		if (User.UserId() is not { } callerId)
		{
			return Unauthorized();
		}

		Track? track = await database.Set<Track>().SingleOrDefaultAsync(row => row.Id == id);

		if (track is null)
		{
			return NotFound();
		}

		// 403 rather than 404, which is the one place in this API that distinction goes the
		// other way (§15.4). A share link makes a track's id legitimately known to people who
		// do not own it, so "you cannot edit this" is the honest answer — and a recipient who
		// wants their own variant exports the GPX and re-imports it.
		if (track.OwnerId != callerId)
		{
			return Problem(
				statusCode: StatusCodes.Status403Forbidden,
				title: "Not your track",
				detail: "Only the owner may edit a track. Export it and import your own copy " +
					"if you want a variant.");
		}

		if (!track.IsFullyUploaded)
		{
			return ConflictProblem(
				"Still uploading",
				"This track is still being uploaded at full resolution. You can edit it once " +
				"that finishes.");
		}

		if (track.Version != request.Version)
		{
			return ConflictProblem(
				"Track has changed",
				$"This edit was written against version {request.Version} and the track is now " +
				$"version {track.Version}. Reload it — the indices in the edit no longer point " +
				"at the same places.");
		}

		// The Live-ride precondition (§15.4), checkable now that a track can actually be attached
		// to a ride as a planned route. Changing the geometry of a route a ride is *on* silently
		// moves every rider's position in §5.4's gap list — nobody rode anywhere, and the list
		// reorders. Attaching and detaching whole routes stays allowed while Live: adding the long
		// option mid-ride moves nobody, because the gap list projects against the oldest one.
		if (await Rides.RideRouteEndpoints.IsTrackAttachedAsync(database, id))
		{
			return ConflictProblem(
				"This track is an adventure's route",
				"An adventure is using this track as its planned route, and editing the line would move " +
				"every traveller's place in the gap list. Remove it from the adventure first.");
		}

		TrackGeometry geometry = await ReadAsync(blobs, track.BlobRef);

		TrackEditResult edit = TrackEditor.Remove(
			geometry,
			[.. request.Removals.Select(range => new PointRange(range.From, range.To))]);

		if (!edit.IsValid)
		{
			return Problem(
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

		return Ok(new TrackEditResponse(TrackStore.Summarise(track), revision.PurgeAfterUtc));
	}

	/// <summary>
	/// Undo is itself an edit, not a rewind: the restored points become a <em>new</em> version,
	/// so the version chain only ever moves forward (§15.6). A device holding a cached copy
	/// replaces it on the version number either way, and never has to reason about going
	/// backwards.
	/// </summary>
	[HttpPost("/api/v1/tracks/{id:guid}/edit/undo", Name = TrackEditEndpoints.UndoRouteName)]
	[EndpointSummary("Restores the pre-edit original, within the undo window.")]
	public async Task<IActionResult> UndoAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] IBlobStore blobs,
		[FromServices] TimeProvider clock)
	{
		if (User.UserId() is not { } callerId)
		{
			return Unauthorized();
		}

		Track? track = await database.Set<Track>().SingleOrDefaultAsync(row => row.Id == id);

		if (track is null || track.OwnerId != callerId)
		{
			return NotFound();
		}

		// Undo moves the line as surely as the edit did, so it meets the same §15.4 precondition.
		// Leaving it out would make "wait until the ride has ended" advice that a second button
		// on the same screen ignores.
		if (await Rides.RideRouteEndpoints.IsTrackAttachedAsync(database, id))
		{
			return ConflictProblem(
				"This track is an adventure's route",
				"An adventure is using this track as its planned route. Remove it from the adventure " +
				"before undoing the edit.");
		}

		TrackRevision? revision = await database
			.Set<TrackRevision>()
			.SingleOrDefaultAsync(row => row.TrackId == id);

		// The clock decides, not whether the nightly sweep has run yet. Two answers to "can I
		// undo this" is one too many.
		if (revision is null || clock.GetUtcNow() >= revision.PurgeAfterUtc)
		{
			return NotFound();
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

		return Ok(new TrackEditResponse(TrackStore.Summarise(track), null));
	}

	/// <summary>
	/// For the rider who has just trimmed their home address off a track and does not want to
	/// wait seven days for it to go (§15.6).
	/// </summary>
	[HttpDelete("/api/v1/tracks/{id:guid}/previous-version", Name = TrackEditEndpoints.PurgeRouteName)]
	[EndpointSummary("Deletes the retained original now, without waiting for the window.")]
	public async Task<IActionResult> PurgeAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] IBlobStore blobs)
	{
		if (User.UserId() is not { } callerId)
		{
			return Unauthorized();
		}

		bool isOurs = await database
			.Set<Track>()
			.AnyAsync(track => track.Id == id && track.OwnerId == callerId);

		if (!isOurs)
		{
			return NotFound();
		}

		TrackRevision? revision = await database
			.Set<TrackRevision>()
			.SingleOrDefaultAsync(row => row.TrackId == id);

		if (revision is null)
		{
			// Nothing to purge is the state the caller asked for, so it is not an error. The
			// nightly sweep and an impatient rider must not race each other into a 404.
			return NoContent();
		}

		database.Remove(revision);

		await database.SaveChangesAsync();

		await blobs.DeleteAsync(revision.BlobRef);

		return NoContent();
	}

	/// <summary>
	/// Writes a new blob and rebuilds every derived figure from the surviving points (§15.5).
	/// <para>
	/// The simplified line, the content hash and the route fingerprint are regenerated too. A
	/// stale polyline would keep drawing the trimmed span on a map, which for the privacy case
	/// is the entire failure; a stale fingerprint would have the browse list's duplicate check
	/// (§6.2) comparing a shared route against a line it no longer follows.
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
		track.RouteHash = RouteFingerprint.Of(geometry);

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

	private ObjectResult ConflictProblem(string title, string detail) =>
		Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail);
}
