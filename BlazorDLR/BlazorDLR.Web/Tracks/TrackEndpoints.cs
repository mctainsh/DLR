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
public static class TrackEndpoints
{
	/// <summary>Route name for upload.</summary>
	public const string UploadRouteName = "UploadTrack";

	/// <summary>Route name for the list.</summary>
	public const string ListRouteName = "ListTracks";

	/// <summary>Route name for the detail.</summary>
	public const string DetailRouteName = "GetTrack";

	/// <summary>Route name for the rename.</summary>
	public const string RenameRouteName = "RenameTrack";

	/// <summary>Route name for the delete.</summary>
	public const string DeleteRouteName = "DeleteTrack";
}

/// <summary>Uploading, listing and reading a track (§6.2, §6.3).</summary>
[ApiController]
[Authorize]
public sealed class TrackController : ControllerBase
{
	/// <summary>
	/// Recording is not gated by §7.8's ladder, deliberately. The restriction targets the
	/// social surface, which is what abuse would be after; stopping a restricted account
	/// keeping its own rides would punish the wrong people for nothing.
	/// </summary>
	[HttpPost("/api/v1/tracks", Name = TrackEndpoints.UploadRouteName)]
	[EndpointSummary("Stores a track. Idempotent on the client's own identifier.")]
	public async Task<IActionResult> UploadAsync(
		[FromBody] UploadTrackRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] TrackStore tracks,
		[FromServices] IBlobStore blobs)
	{
		if (User.UserId() is not { } ownerId)
		{
			return Unauthorized();
		}

		if (request.Points.Count < TrackEditor.MinimumSurvivingPoints)
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "Not a track",
				detail: $"A track needs at least {TrackEditor.MinimumSurvivingPoints} points; " +
				$"this upload has {request.Points.Count}.");
		}

		// Length rather than presence. An upload with no name at all is legitimate — a track saved
		// by an older build, or one whose name the importer will supply — but a name too long for
		// the column is a 500 dressed up as a database error unless it is caught here.
		if (TrackNaming.Clean(request.Name) is { Length: > TrackNaming.MaxLength } overlong)
		{
			return NameTooLong(overlong);
		}

		if (request.Points.Any(point => !point.HasUsableCoordinates))
		{
			// The app parses GPX with the same reader the server does (§15.7), but a
			// client-supplied point list is untrusted input regardless of which of our own
			// clients produced it (§15.2).
			return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid coordinates", detail: "A point is outside the possible range.");
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
			return Ok(TrackStore.Summarise(existing));
		}

		TrackGeometry geometry = new(request.Points, request.SegmentStarts);

		Track track = await tracks.StageAsync(
			ownerId,
			request.ClientGuid,
			geometry,
			TrackNaming.Clean(request.Name),
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

			return Ok(TrackStore.Summarise(winner));
		}

		return Created($"/api/v1/tracks/{track.Id}", TrackStore.Summarise(track));
	}

	[HttpGet("/api/v1/tracks", Name = TrackEndpoints.ListRouteName)]
	[EndpointSummary("The caller's tracks, newest first.")]
	public async Task<IActionResult> ListAsync([FromServices] DlrDbContext database)
	{
		if (User.UserId() is not { } ownerId)
		{
			return Unauthorized();
		}

		// created_utc, not started_utc (§6.2). An imported route has no start time at all, and
		// a ride imported today belongs at the top of the list whenever it was ridden.
		List<Track> rows = await database
			.Set<Track>()
			.AsNoTracking()
			.OwnedBy(ownerId)
			.ToListAsync();

		return Ok(rows.Select(TrackStore.Summarise).ToList());
	}

	[HttpGet("/api/v1/tracks/{id:guid}", Name = TrackEndpoints.DetailRouteName)]
	[EndpointSummary("One track's metadata and its simplified line.")]
	public async Task<IActionResult> DetailAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database)
	{
		if (User.UserId() is not { } ownerId)
		{
			return Unauthorized();
		}

		// The caller's own track, or one anybody may read because its owner shared it with
		// everyone (§6.2). Everything else is 404 rather than 403, and still is: a
		// distinguishable answer on a private track would be a way to ask whether a track id
		// exists (§15.4).
		Track? track = await database
			.Set<Track>()
			.AsNoTracking()
			.SingleOrDefaultAsync(row =>
				row.Id == id
				&& (row.OwnerId == ownerId || row.Visibility == TrackVisibility.Public));

		if (track is null)
		{
			return NotFound();
		}

		bool isMine = track.OwnerId == ownerId;

		// Read only for somebody else's, and only after the row proved to be public. A second
		// query rather than an Include, because the owner's row is wanted on the rare read and
		// joined onto every one of a rider's own.
		string? ownerName = isMine
			? null
			: await database
				.Set<Data.Identity.AppUser>()
				.AsNoTracking()
				.Where(user => user.Id == track.OwnerId)
				.Select(user => user.UserName)
				.SingleOrDefaultAsync();

		using MemoryStream polyline = new(track.SimplifiedPolyline);

		TrackGeometry simplified = TrackBlobCodec.Read(polyline);

		return Ok(new TrackDetail(
			isMine ? TrackStore.Summarise(track) : TrackStore.SummariseForReader(track, ownerName ?? "Unknown"),
			new TrackBounds(
				track.BoundsMinLat,
				track.BoundsMinLon,
				track.BoundsMaxLat,
				track.BoundsMaxLon),
			simplified.Points));
	}

	/// <summary>
	/// Renaming a track — recorded or imported alike (§15.1).
	/// <para>
	/// <strong>Not an edit, and deliberately not versioned.</strong> §15.5's version guards point
	/// indices: an edit quotes the version it was composed against because the indices it carries
	/// stop meaning anything once the line moves. A name moves nothing, so bumping the version here
	/// would refuse an editor open in another tab over a change that cannot have invalidated it,
	/// and would force every cached copy to be re-fetched to learn a single string.
	/// </para>
	/// </summary>
	[HttpPatch("/api/v1/tracks/{id:guid}", Name = TrackEndpoints.RenameRouteName)]
	[EndpointSummary("Renames one of the caller's tracks.")]
	public async Task<IActionResult> RenameAsync(
		[FromRoute] Guid id,
		[FromBody] RenameTrackRequest request,
		[FromServices] DlrDbContext database)
	{
		if (User.UserId() is not { } ownerId)
		{
			return Unauthorized();
		}

		if (TrackNaming.Clean(request.Name) is not { } name)
		{
			// Refused rather than treated as "clear the name". A rider who wants the name gone can
			// say what it should be instead; nobody submits an empty box on purpose.
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "A track needs a name",
				detail: "Type what this adventure should be called.");
		}

		if (name.Length > TrackNaming.MaxLength)
		{
			return NameTooLong(name);
		}

		// Owner-scoped, and 404 to everybody else — the same answer the detail read gives, so a
		// rename cannot be used to ask whether a track id exists (§15.4).
		Track? track = await database
			.Set<Track>()
			.SingleOrDefaultAsync(row => row.Id == id && row.OwnerId == ownerId);

		if (track is null)
		{
			return NotFound();
		}

		// A shared route's name is on a list other riders read, so it has to be its own (§6.2).
		// A private track is the rider's own filing system and may be called whatever they like —
		// the same rule the share itself applies, applied again here because a rename is the other
		// way a route on that list can end up wearing a name that is already on it.
		if (track.Visibility == TrackVisibility.Public
			&& await SharedRoutes.NamedAsync(database, track.Id, name) is { } taken)
		{
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "That name is taken",
				detail: $"A route called {taken.Describe} is already shared with everyone. Try another name.");
		}

		track.Name = name;

		await database.SaveChangesAsync();

		return Ok(TrackStore.Summarise(track));
	}

	/// <summary>
	/// Deleting a track, its markers and its points (§15.5, §16.6).
	/// <para>
	/// <strong>The rows cascade; the blobs do not.</strong> `ON DELETE CASCADE` takes the retained
	/// original, the markers hanging off the track and any ride attachment with it, and reaches no
	/// filesystem at all. The two blob references are therefore read <em>before</em> the row goes —
	/// afterwards nothing is left to say which files were this track's, and a track a rider deleted
	/// to be rid of is still on the disk and in tonight's backup. The §7.11 sweep is the backstop
	/// rather than the mechanism, on <see cref="Account.AccountBlobs"/>'s reasoning.
	/// </para>
	/// </summary>
	[HttpDelete("/api/v1/tracks/{id:guid}", Name = TrackEndpoints.DeleteRouteName)]
	[EndpointSummary("Deletes one of the caller's tracks and its points. Irreversible.")]
	public async Task<IActionResult> DeleteAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] IBlobStore blobs,
		[FromServices] ILoggerFactory loggers,
		CancellationToken cancellationToken)
	{
		if (User.UserId() is not { } ownerId)
		{
			return Unauthorized();
		}

		Track? track = await database
			.Set<Track>()
			.AsNoTracking()
			.SingleOrDefaultAsync(row => row.Id == id && row.OwnerId == ownerId, cancellationToken);

		if (track is null)
		{
			// Including a second press of the same button. The rider asked for it to be gone and it
			// is, but answering 204 to any id at all would make this a way to probe for tracks.
			return NotFound();
		}

		// The §15.4 precondition an edit meets, and a delete meets it for a stronger reason: an
		// edit moves the line an adventure is measured against, and a delete takes it away
		// entirely — the attachment cascades, and every rider's place in §5.4's gap list with it.
		if (await Rides.RideRouteEndpoints.IsTrackAttachedAsync(database, id, cancellationToken))
		{
			return Problem(
				statusCode: StatusCodes.Status409Conflict,
				title: "This track is an adventure's route",
				detail: "An adventure is using this track as its planned route. Remove it from the " +
					"adventure first, or delete the adventure.");
		}

		// Gathered before the delete, for the reason on the method.
		string? revisionBlob = await database
			.Set<TrackRevision>()
			.Where(revision => revision.TrackId == id)
			.Select(revision => revision.BlobRef)
			.SingleOrDefaultAsync(cancellationToken);

		await database
			.Set<Track>()
			.Where(row => row.Id == id && row.OwnerId == ownerId)
			.ExecuteDeleteAsync(cancellationToken);

		ILogger logger = loggers.CreateLogger(typeof(TrackController));

		// Rows first, blobs second, and a failure here is logged rather than thrown: the track is
		// already gone, and answering 500 would tell the rider their deletion failed when it did
		// not. The nightly orphan sweep collects whatever is left.
		foreach (string blobRef in new[] { track.BlobRef, revisionBlob }.OfType<string>().Distinct(StringComparer.Ordinal))
		{
			if (blobRef.Length == 0)
			{
				continue;
			}

			try
			{
				await blobs.DeleteAsync(blobRef, cancellationToken);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				logger.LogError(
					exception,
					"Could not delete blob {BlobRef} for deleted track {TrackId}; the nightly sweep will.",
					blobRef,
					id);
			}
		}

		return NoContent();
	}

	private ObjectResult NameTooLong(string name) =>
		Problem(
			statusCode: StatusCodes.Status400BadRequest,
			title: "Name too long",
			detail: $"A track name is limited to {TrackNaming.MaxLength} characters; " +
				$"this one is {name.Length}.");
}
