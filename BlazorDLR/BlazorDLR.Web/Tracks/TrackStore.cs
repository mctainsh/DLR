using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.Server.Data;
using DLR.Server.Data.Tracks;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tracks;

/// <summary>
/// Turning a geometry into a stored track (§8, §9.1, §15.7).
/// <para>
/// One place, because upload and import both arrive here and §15.7 is emphatic that three
/// entry points into one pipeline is how a track's ascent comes out different depending on
/// where it came from. The stats, the simplified line, the content hash and the blob are all
/// produced the same way whichever door the points came through.
/// </para>
/// </summary>
/// <param name="database">The one context.</param>
/// <param name="blobs">Where the points go.</param>
/// <param name="clock">The project's clock (§10.4).</param>
public sealed class TrackStore(DlrDbContext database, IBlobStore blobs, TimeProvider clock)
{
	/// <summary>
	/// Builds a track from a geometry and writes its blob. Not saved — the caller decides
	/// whether this is one track or several, and a partly-committed import is worse than none.
	/// </summary>
	/// <param name="ownerId">Whose track.</param>
	/// <param name="clientGuid">The idempotency key (§4.4).</param>
	/// <param name="geometry">The points.</param>
	/// <param name="name">What to call it.</param>
	/// <param name="source">Recorded or imported.</param>
	/// <param name="importedFileName">The file it came from, when it came from one.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public async Task<Track> StageAsync(
		Guid ownerId,
		Guid clientGuid,
		TrackGeometry geometry,
		string? name,
		TrackSource source,
		string? importedFileName = null,
		CancellationToken cancellationToken = default)
	{
		using MemoryStream points = new();

		TrackBlobCodec.Write(geometry, points);
		points.Position = 0;

		string blobRef = await blobs.PutAsync(points, cancellationToken);

		Track track = new()
		{
			Id = Guid.NewGuid(),
			OwnerId = ownerId,
			ClientGuid = clientGuid,
			Name = name,
			CreatedUtc = clock.GetUtcNow(),
			Visibility = TrackVisibility.Private,
			Source = source,
			BlobRef = blobRef,
			SimplifiedPolyline = SimplifiedBytes(geometry),
			ContentHash = TrackBlobCodec.ContentHash(geometry),
			ImportedFileName = importedFileName,
			ImportedFormat = importedFileName is null ? null : "gpx",
			Version = 1,
		};

		Apply(track, TrackStats.From(geometry));

		database.Add(track);

		return track;
	}

	/// <summary>
	/// The caller's track with this content, if they already have one (§15.3).
	/// <para>
	/// Duplicate <em>detection</em>, not prevention. Re-importing on purpose is legitimate — a
	/// second copy to edit differently — and doing it by accident is the common case. A warning
	/// serves both; a refusal serves neither.
	/// </para>
	/// </summary>
	/// <param name="ownerId">Whose tracks to look in. Two riders with the same ride is a group.</param>
	/// <param name="contentHash">The hash to match.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public Task<Track?> FindByContentAsync(
		Guid ownerId,
		byte[] contentHash,
		CancellationToken cancellationToken = default) =>
		database.Set<Track>()
			.AsNoTracking()
			.Where(track => track.OwnerId == ownerId && track.ContentHash == contentHash)
			.OrderBy(track => track.CreatedUtc)
			.FirstOrDefaultAsync(cancellationToken);

	/// <summary>Removes blobs staged for tracks that were never saved.</summary>
	/// <param name="tracks">What was staged.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public async Task DiscardAsync(IEnumerable<Track> tracks, CancellationToken cancellationToken = default)
	{
		foreach (Track track in tracks)
		{
			database.Entry(track).State = EntityState.Detached;

			await blobs.DeleteAsync(track.BlobRef, cancellationToken);
		}
	}

	/// <summary>
	/// The list and detail projection, for the owner (§6.2).
	/// </summary>
	/// <param name="track">The stored track.</param>
	/// <remarks>
	/// Every caller of this overload has already scoped its query to the caller's own rows, which
	/// is why <see cref="TrackSummary.IsMine"/> comes back true and no owner name is carried — it
	/// would be the reader's own username on every row of their own list.
	/// </remarks>
	public static TrackSummary Summarise(Track track) => Project(track, ownerName: null, isMine: true);

	/// <summary>
	/// The same projection for a track the caller does not own — a public route opened from the
	/// browse list (§6.2).
	/// </summary>
	/// <param name="track">The stored track.</param>
	/// <param name="ownerName">
	/// Whose it is. The username, never a self-chosen display name (§7.3).
	/// </param>
	/// <remarks>
	/// A separate method rather than a defaulted argument, so that publishing somebody else's
	/// track through the owner overload is a call that does not compile rather than a screen that
	/// quietly offers Delete on a stranger's route.
	/// </remarks>
	public static TrackSummary SummariseForReader(Track track, string ownerName) =>
		Project(track, ownerName, isMine: false);

	private static TrackSummary Project(Track track, string? ownerName, bool isMine) => new(
		track.Id,
		track.Name,
		track.CreatedUtc,
		track.StartedUtc,
		track.EndedUtc,
		track.DistanceM,
		track.DurationS,
		track.AscentM,
		track.MaxSpeedMps,
		track.PointCount,
		track.SegmentCount,
		track.Source == TrackSource.Imported ? TrackSourceDto.Imported : TrackSourceDto.Recorded,
		track.Version,
		track.Visibility switch
		{
			TrackVisibility.Public => TrackVisibilityDto.Public,
			TrackVisibility.Link => TrackVisibilityDto.Link,
			_ => TrackVisibilityDto.Private,
		},
		track.Description,
		track.PhotoId,
		ownerName,
		isMine);

	private static byte[] SimplifiedBytes(TrackGeometry geometry)
	{
		using MemoryStream buffer = new();

		TrackBlobCodec.Write(TrackSimplifier.Simplify(geometry), buffer);

		return buffer.ToArray();
	}

	private static void Apply(Track track, TrackStats stats)
	{
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
	}
}
