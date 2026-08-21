using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DLR.Core.Contracts.Account;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
using DLR.Core.Markers;
using DLR.Core.Tracks;
using DLR.Server.Data;
using DLR.Server.Data.Comments;
using DLR.Server.Data.Identity;
using DLR.Server.Data.Markers;
using DLR.Server.Data.Photos;
using DLR.Server.Data.Rides;
using DLR.Server.Data.Tracks;
using DLR.Server.Tracks;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Account;

/// <summary>
/// Builds the archive <c>GET /api/v1/me/export</c> returns (§6.3, §10.1).
/// <para>
/// <strong>An archive rather than a JSON document, because §16.6 says the export includes markers
/// <em>and their photos</em>.</strong> A response listing photo identifiers would not be an export
/// of anybody's photographs, and a track reduced to its distance would not be an export of their
/// ride. Points go out as GPX — the format the rest of the product already reads and writes, so the
/// file is useful somewhere other than here (§15.1).
/// </para>
/// </summary>
public static class AccountExportBuilder
{
	/// <summary>Where the manifest lives inside the archive.</summary>
	public const string ManifestPath = "export.json";

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
	};

	/// <summary>Writes the whole archive to a stream.</summary>
	/// <param name="output">Where the ZIP goes.</param>
	/// <param name="database">The one context.</param>
	/// <param name="blobs">Where points and images are read from.</param>
	/// <param name="user">Whose data.</param>
	/// <param name="now">The project clock's reading, stamped on the manifest.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public static async Task WriteAsync(
		Stream output,
		DlrDbContext database,
		IBlobStore blobs,
		AppUser user,
		DateTimeOffset now,
		CancellationToken cancellationToken = default)
	{
		using ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true);

		List<Track> tracks = await database
			.Set<Track>()
			.AsNoTracking()
			.Where(track => track.OwnerId == user.Id)
			.OrderBy(track => track.CreatedUtc)
			.ToListAsync(cancellationToken);

		List<Guid> trackIds = [.. tracks.Select(track => track.Id)];

		Dictionary<Guid, TrackRevision> revisions = await database
			.Set<TrackRevision>()
			.AsNoTracking()
			.Where(revision => trackIds.Contains(revision.TrackId))
			.ToDictionaryAsync(revision => revision.TrackId, cancellationToken);

		List<Marker> markers = await database
			.Set<Marker>()
			.AsNoTracking()
			.Where(marker => marker.CreatedByUserId == user.Id)
			.OrderBy(marker => marker.CreatedUtc)
			.ToListAsync(cancellationToken);

		List<Photo> photos = await database
			.Set<Photo>()
			.AsNoTracking()
			.Where(photo => photo.OwnerId == user.Id)
			.OrderBy(photo => photo.CreatedUtc)
			.ToListAsync(cancellationToken);

		List<ExportedTrack> exportedTracks = [];

		foreach (Track track in tracks)
		{
			string gpxPath = $"tracks/{track.Id}.gpx";

			await WriteGpxAsync(archive, gpxPath, blobs, track.BlobRef, track.Name, markers, track.Id);

			// The retained pre-edit original, while it exists (§15.6). It is the rider's data for
			// as long as this server holds it, so leaving it out of the file that claims to be
			// everything would make the claim false for exactly the seven days it matters.
			string? previousPath = null;

			if (revisions.TryGetValue(track.Id, out TrackRevision? revision))
			{
				previousPath = $"tracks/{track.Id}.previous-version.gpx";

				await WriteGpxAsync(
					archive,
					previousPath,
					blobs,
					revision.BlobRef,
					$"{track.Name} (previous version)",
					[],
					track.Id);
			}

			exportedTracks.Add(new ExportedTrack(
				track.Id,
				track.Name,
				track.Source.ToString(),
				track.CreatedUtc,
				track.StartedUtc,
				track.DistanceM,
				track.AscentM,
				track.Version,
				gpxPath,
				previousPath));
		}

		List<ExportedPhoto> exportedPhotos = [];

		foreach (Photo photo in photos)
		{
			string path = $"photos/{photo.Id}.jpg";

			await WriteBlobAsync(archive, path, blobs, photo.BlobRef);

			exportedPhotos.Add(new ExportedPhoto(
				photo.Id,
				photo.WidthPx,
				photo.HeightPx,
				photo.ByteSize,
				photo.CreatedUtc,
				path));
		}

		AccountExport manifest = new(
			now,
			user.Id,
			user.UserName!,
			user.CreatedUtc,
			user.LastActiveUtc,
			new ExportedProfile(
				user.DisplayName,
				user.PhoneNumber,
				user.Email,
				user.EmailConfirmed,
				user.ShareDisplayName,
				user.SharePhoneNumber,
				user.ShareEmail,
				// The account holds the private area now (§10.1), so an export that left it out
				// would be claiming completeness while withholding the one setting a rider is
				// most likely to want to check.
				user is { PrivateAreaLat: { } areaLat, PrivateAreaLon: { } areaLon, PrivateAreaRadiusM: { } areaRadius }
					? new PrivateAreaSettings(areaLat, areaLon, areaRadius)
					: null),
			exportedTracks,
			[
				.. markers.Select(marker => new ExportedMarker(
					marker.Id,
					marker.TrackId,
					marker.GroupRideId,
					PositionScale.ToDegrees(marker.Lat),
					PositionScale.ToDegrees(marker.Lon),
					marker.DirectionDeg,
					marker.Icon,
					marker.Title,
					marker.Note,
					marker.PhotoId,
					marker.CreatedUtc)),
			],
			exportedPhotos,
			await RidesAsync(database, user.Id, cancellationToken),
			await CommentsAsync(database, user.Id, cancellationToken),
			await ReactionsAsync(database, user.Id, cancellationToken),
			await VotesAsync(database, user.Id, cancellationToken),
			await DevicesAsync(database, user.Id, cancellationToken));

		ZipArchiveEntry entry = archive.CreateEntry(ManifestPath, CompressionLevel.Optimal);

		await using Stream manifestStream = entry.Open();

		await JsonSerializer.SerializeAsync(manifestStream, manifest, Json, cancellationToken);
	}

	private static Task<List<ExportedRide>> RidesAsync(
		DlrDbContext database,
		Guid userId,
		CancellationToken cancellationToken) =>
		database
			.Set<GroupRideMember>()
			.AsNoTracking()
			.Where(member => member.UserId == userId)
			.OrderBy(member => member.JoinedUtc)
			.Select(member => new ExportedRide(
				member.GroupRideId,
				member.Ride!.Name,

				// Compared, never cast. Role and State are stored through HasConversion<string>,
				// so an enum-to-int cast inside a translated projection asks PostgreSQL to read
				// 'Owner' as an integer — the trap SRV-29 paid fifteen failing tests for.
				member.Role == GroupRideRole.Owner ? "Owner"
					: member.Role == GroupRideRole.Leader ? "Leader"
					: member.Role == GroupRideRole.Spectator ? "Spectator"
					: "Rider",
				member.Ride.State == GroupRideState.Draft ? "Draft"
					: member.Ride.State == GroupRideState.Open ? "Open"
					: member.Ride.State == GroupRideState.Live ? "Live"
					: member.Ride.State == GroupRideState.Completed ? "Completed"
					: member.Ride.State == GroupRideState.Archived ? "Archived"
					: "Cancelled",
				member.Ride.StartUtc,
				member.JoinedUtc,
				member.ShareLocation))
			.ToListAsync(cancellationToken);

	private static async Task<List<ExportedComment>> CommentsAsync(
		DlrDbContext database,
		Guid userId,
		CancellationToken cancellationToken)
	{
		List<RideComment> comments = await database
			.Set<RideComment>()
			.AsNoTracking()
			.Where(comment => comment.AuthorId == userId)
			.OrderBy(comment => comment.PostedUtc)
			.ToListAsync(cancellationToken);

		List<Guid> ids = [.. comments.Select(comment => comment.Id)];

		// A second pass rather than a correlated sub-select, for the reason SRV-30 already
		// established: a poll is three joined tables and does not belong inside a projection that
		// runs once per row.
		//
		// Fetched flat and grouped in memory, deliberately. An `OrderBy` *before* a translated
		// `GroupBy` does not survive into the result — PostgreSQL is free to return the groups'
		// members in any order, and it does, intermittently. A poll whose options come back
		// reversed is a different poll, and the failure appears in about one run in three.
		List<PollOption> options = await database
			.Set<PollOption>()
			.AsNoTracking()
			.Where(option => ids.Contains(option.CommentId))
			.OrderBy(option => option.CommentId)
			.ThenBy(option => option.Ordinal)
			.ToListAsync(cancellationToken);

		Dictionary<Guid, List<string>> byComment = options
			.GroupBy(option => option.CommentId)
			.ToDictionary(
				group => group.Key,
				group => group.Select(option => option.Text).ToList());

		return
		[
			.. comments.Select(comment => new ExportedComment(
				comment.Id,
				comment.GroupRideId,
				comment.TrackId,
				comment.Kind.ToString(),
				comment.Body,
				comment.PhotoId,
				comment.PostedUtc,
				comment.CreatedUtc,
				comment.EditedUtc,
				byComment.TryGetValue(comment.Id, out List<string>? texts) ? texts : [])),
		];
	}

	private static Task<List<ExportedReaction>> ReactionsAsync(
		DlrDbContext database,
		Guid userId,
		CancellationToken cancellationToken) =>
		database
			.Set<CommentReaction>()
			.AsNoTracking()
			.Where(reaction => reaction.UserId == userId)
			.Select(reaction => new ExportedReaction(reaction.CommentId, reaction.Reaction))
			.ToListAsync(cancellationToken);

	private static Task<List<ExportedVote>> VotesAsync(
		DlrDbContext database,
		Guid userId,
		CancellationToken cancellationToken) =>
		database
			.Set<PollVote>()
			.AsNoTracking()
			.Where(vote => vote.UserId == userId)
			.OrderBy(vote => vote.CreatedUtc)
			.Select(vote => new ExportedVote(
				vote.Option!.CommentId,
				vote.Option.Text,
				vote.CreatedUtc))
			.ToListAsync(cancellationToken);

	private static Task<List<ExportedDevice>> DevicesAsync(
		DlrDbContext database,
		Guid userId,
		CancellationToken cancellationToken) =>
		database
			.Set<Device>()
			.AsNoTracking()
			.Where(device => device.UserId == userId)
			.OrderBy(device => device.CreatedUtc)
			.Select(device => new ExportedDevice(
				device.Id,
				device.Name,
				device.CreatedUtc,
				device.LastSeenUtc))
			.ToListAsync(cancellationToken);

	/// <summary>
	/// Writes one track's points as GPX, with its markers as waypoints — the same mapping
	/// <c>GET /tracks/{id}/gpx</c> uses, so a file out of an export re-imports (§16.6).
	/// </summary>
	private static async Task WriteGpxAsync(
		ZipArchive archive,
		string path,
		IBlobStore blobs,
		string blobRef,
		string? name,
		IReadOnlyList<Marker> markers,
		Guid trackId)
	{
		await using Stream? blob = await blobs.OpenAsync(blobRef);

		if (blob is null)
		{
			// Skipped rather than fatal. A missing blob is a bug somewhere else, and answering an
			// export request with a 500 would leave the rider with nothing at all rather than with
			// everything except one track.
			return;
		}

		TrackGeometry geometry = TrackBlobCodec.Read(blob);

		string gpx = GpxWriter.Write(
			name ?? "Track",
			geometry.Points,
			[
				.. markers
					.Where(marker => marker.TrackId == trackId)
					.Select(marker => new GpxWaypointOut(
						PositionScale.ToDegrees(marker.Lat),
						PositionScale.ToDegrees(marker.Lon),
						marker.Title,
						marker.Note,
						MarkerIcons.ToGpxSymbol(marker.Icon),
						marker.DirectionDeg)),
			]);

		ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);

		await using Stream target = entry.Open();

		await target.WriteAsync(Encoding.UTF8.GetBytes(gpx));
	}

	private static async Task WriteBlobAsync(
		ZipArchive archive,
		string path,
		IBlobStore blobs,
		string blobRef)
	{
		await using Stream? blob = await blobs.OpenAsync(blobRef);

		if (blob is null)
		{
			return;
		}

		ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);

		await using Stream target = entry.Open();

		await blob.CopyToAsync(target);
	}
}
