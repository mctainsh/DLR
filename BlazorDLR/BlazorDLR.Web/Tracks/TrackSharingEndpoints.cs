using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.Server.Data;
using DLR.Server.Data.Photos;
using DLR.Server.Data.Tracks;
using DLR.Server.Identity;
using DLR.Server.Moderation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tracks;

/// <summary>Route names for the sharing surface, quoted by the tests and by link generation.</summary>
public static class TrackSharingEndpoints
{
	/// <summary>Route name for the details-and-sharing update.</summary>
	public const string UpdateDetailsRouteName = "UpdateTrackDetails";

	/// <summary>Route name for the browse list.</summary>
	public const string BrowseSharedRouteName = "BrowseSharedTracks";
}

/// <summary>
/// The description, the cover photograph and public sharing of a route (§6.2, §6.3).
/// <para>
/// Separate from <see cref="TrackController"/> on purpose. That controller is about a track as a
/// recording — its points, its stats, its edits — and every action on it is owner-scoped by
/// definition. This one is about a track as something published, and it is the first place in the
/// project where one rider reads another rider's track at all.
/// </para>
/// </summary>
[ApiController]
[Authorize]
public sealed class TrackSharingController : ControllerBase
{
	/// <summary>
	/// Sets the description, the cover photograph and the visibility together (§6.2).
	/// <para>
	/// One panel, one Save, one request. See <see cref="UpdateTrackDetailsRequest"/> for why this
	/// assigns all three rather than patching whichever happened to be sent.
	/// </para>
	/// </summary>
	[HttpPatch("/api/v1/tracks/{id:guid}/details", Name = TrackSharingEndpoints.UpdateDetailsRouteName)]
	[EndpointSummary("Sets a track's description, cover photo and whether it is shared with everyone.")]
	public async Task<IActionResult> UpdateDetailsAsync(
		[FromRoute] Guid id,
		[FromBody] UpdateTrackDetailsRequest request,
		[FromServices] DlrDbContext database,
		[FromServices] IBlobStore blobs,
		[FromServices] TimeProvider clock,
		CancellationToken cancellationToken)
	{
		if (User.UserId() is not { } ownerId)
		{
			return Unauthorized();
		}

		if (!Enum.IsDefined(request.Visibility))
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "Unknown visibility",
				detail: "Visibility is Private, Link or Public.");
		}

		string? description = TrackDescription.Clean(request.Description);

		if (description is { Length: > TrackDescription.MaxLength })
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "Description too long",
				detail: $"A route description is limited to {TrackDescription.MaxLength} characters; "
					+ $"this one is {description.Length}.");
		}

		// Owner-scoped, and 404 to everybody else — the same answer a rename gives, so this
		// cannot be used to ask whether a track id exists (§15.4).
		Track? track = await database
			.Set<Track>()
			.SingleOrDefaultAsync(row => row.Id == id && row.OwnerId == ownerId, cancellationToken);

		if (track is null)
		{
			return NotFound();
		}

		// Checked at the moment of publication rather than on the upload, on the same reasoning
		// as a marker's photo (§5.8, §7.8): recording a route and writing about it are private
		// acts, and §7.8's ladder is aimed at the social surface. Putting a route in front of
		// every rider on the service is that surface.
		if (request.Visibility == TrackVisibilityDto.Public
			&& track.Visibility != TrackVisibility.Public
			&& User.HasClaim(claim => claim.Type == DlrClaims.Restricted))
		{
			return Problem(
				statusCode: StatusCodes.Status403Forbidden,
				title: "Not yet",
				detail: "New accounts cannot share routes publicly. Confirm your email address, and "
					+ "this unlocks with the rest of the social features.");
		}

		if (request.PhotoId is { } photoId)
		{
			// Their own upload, not merely one that exists — MarkerController's reasoning
			// exactly: a guessed identifier would otherwise republish somebody else's
			// photograph as the cover of a route of the caller's choosing.
			bool ownsIt = await database
				.Set<Photo>()
				.AnyAsync(photo => photo.Id == photoId && photo.OwnerId == ownerId, cancellationToken);

			if (!ownsIt)
			{
				return Problem(
					statusCode: StatusCodes.Status404NotFound,
					title: "No such photo",
					detail: "Upload the image first, and attach one you uploaded.");
			}
		}

		// The two rules a route only has to satisfy on the way onto everybody's list (§6.2),
		// checked on the transition and not on every save. A route that is already shared has
		// already passed them, and re-checking here would refuse an edit to a description over a
		// clash the rider has no way to see or to fix.
		if (request.Visibility == TrackVisibilityDto.Public && track.Visibility != TrackVisibility.Public)
		{
			// Filled in on the way through for a track recorded before the fingerprint column
			// existed. A missing blob leaves it empty rather than throwing: a route whose points
			// cannot be read is a problem for the map to report, and refusing to share it here
			// would report it as a duplicate, which it is not.
			if (track.RouteHash.Length == 0)
				track.RouteHash = await FingerprintAsync(blobs, track.BlobRef, cancellationToken);

			if (await SharedRoutes.WithTheSamePointsAsync(database, ownerId, track.RouteHash, cancellationToken) is { } sameRoad)
			{
				return Problem(
					statusCode: StatusCodes.Status409Conflict,
					title: "Already shared",
					detail: $"Another rider has already shared this route as {sameRoad.Describe} — it "
						+ "follows exactly the same points. Look for it on the shared list rather than "
						+ "putting a second copy of the same road on there.");
			}

			// An unnamed route is still shareable. Uniqueness is about telling two entries on a
			// list apart, and refusing the share outright would be inventing a second rule — the
			// one place a name is compulsory is where somebody was asked for one (§15.1).
			if (TrackNaming.Clean(track.Name) is { Length: > 0 } name
				&& await SharedRoutes.NamedAsync(database, track.Id, name, cancellationToken) is { } sameName)
			{
				return Problem(
					statusCode: StatusCodes.Status409Conflict,
					title: "That name is taken",
					detail: $"A route called {sameName.Describe} is already shared with everyone. Give "
						+ "this one a name of its own, and share it again.");
			}
		}

		track.Description = description;
		track.PhotoId = request.PhotoId;
		track.Visibility = request.Visibility switch
		{
			TrackVisibilityDto.Public => TrackVisibility.Public,
			TrackVisibilityDto.Link => TrackVisibility.Link,
			_ => TrackVisibility.Private,
		};

		// Stamped the first time only. Un-sharing and re-sharing must not push a route back to
		// the top of everybody's browse list, which is the one use a reset here would have.
		if (track.Visibility == TrackVisibility.Public && track.FirstSharedUtc is null)
			track.FirstSharedUtc = clock.GetUtcNow();

		await database.SaveChangesAsync(cancellationToken);

		return Ok(TrackStore.Summarise(track));
	}

	/// <summary>
	/// Everybody else's shared routes, filtered and paged (§6.2).
	/// </summary>
	/// <remarks>
	/// The caller's own shared routes are excluded. They are already on the other tab, and a rider
	/// browsing for somewhere new to ride is not looking for the road they recorded themselves.
	/// </remarks>
	[HttpGet("/api/v1/tracks/shared", Name = TrackSharingEndpoints.BrowseSharedRouteName)]
	[EndpointSummary("Routes other riders have shared with everyone. Filtered by name and by area, one page at a time.")]
	public async Task<IActionResult> BrowseAsync(
		[FromQuery] string? name,
		[FromQuery] double? lat,
		[FromQuery] double? lon,
		[FromQuery] double? withinKm,
		[FromQuery] int page,
		[FromServices] DlrDbContext database,
		CancellationToken cancellationToken)
	{
		if (User.UserId() is not { } callerId)
		{
			return Unauthorized();
		}

		SharedTrackQuery query = new(name, lat, lon, withinKm, page < 1 ? 1 : page);

		IReadOnlySet<Guid> hidden = await BlockList.HiddenFromAsync(database, callerId, cancellationToken);

		IQueryable<Track> matches = database
			.Set<Track>()
			.AsNoTracking()
			.Where(track =>
				track.Visibility == TrackVisibility.Public
				&& track.OwnerId != callerId
				&& !hidden.Contains(track.OwnerId)

				// A track whose points have not finished arriving has a bounding box and a
				// distance that are both provisional (§15.4). Listing it would put a route on
				// the map that is a fragment of the one it claims to be.
				&& track.IsFullyUploaded);

		if (TrackNaming.Clean(name) is { Length: > 0 } wanted)
		{
			// ILike rather than ToLower().Contains(): case-insensitivity here is the database's
			// job and collation-aware, and the wildcards in what the rider typed are text
			// rather than syntax.
			string pattern = $"%{Escape(wanted)}%";

			matches = matches.Where(track =>
				track.Name != null && EF.Functions.ILike(track.Name, pattern, EscapeCharacter));
		}

		// Zero when no area was asked for. The distance is still computed in that case and simply
		// never read — see ScoredTrack for why one expression beats two query shapes.
		double centreLat = query.HasArea ? query.Latitude!.Value : 0;
		double centreLon = query.HasArea ? query.Longitude!.Value : 0;
		double radiusKm = query.HasArea ? Math.Min(query.WithinKm!.Value, SharedTrackQuery.MaxWithinKm) : 0;

		if (query.HasArea)
		{
			// Latitude first, and on its own. It is the half of the box that never widens with
			// where you are standing and never wraps at the antimeridian, so it is the half an
			// index can be trusted with; the exact distance below does the rest.
			double latPadding = (radiusKm / KmPerDegreeLatitude) + 0.000_001;

			matches = matches.Where(track =>
				((track.BoundsMinLat + track.BoundsMaxLat) / 2) >= centreLat - latPadding
				&& ((track.BoundsMinLat + track.BoundsMaxLat) / 2) <= centreLat + latPadding);
		}

		// The haversine, written once. See ScoredTrack.
		IQueryable<ScoredTrack> scored = matches.Select(track => new ScoredTrack
		{
			Track = track,
			AwayKm = 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(
				(Math.Sin((((track.BoundsMinLat + track.BoundsMaxLat) / 2) - centreLat) * Radians / 2)
					* Math.Sin((((track.BoundsMinLat + track.BoundsMaxLat) / 2) - centreLat) * Radians / 2))
				+ (Math.Cos(centreLat * Radians)
					* Math.Cos(((track.BoundsMinLat + track.BoundsMaxLat) / 2) * Radians)
					* Math.Sin((((track.BoundsMinLon + track.BoundsMaxLon) / 2) - centreLon) * Radians / 2)
					* Math.Sin((((track.BoundsMinLon + track.BoundsMaxLon) / 2) - centreLon) * Radians / 2))))),
		});

		if (query.HasArea)
		{
			scored = scored.Where(row => row.AwayKm <= radiusKm);
		}

		// Counted after the filter and before the page, so "42 routes" is what the rider narrowed
		// to rather than what exists.
		int total = await scored.CountAsync(cancellationToken);

		IOrderedQueryable<ScoredTrack> ordered = query.HasArea
			// Nearest first once an area is asked for. Sorting a "within 50 km" list by date
			// would answer a question nobody asked with the control they just used.
			? scored
				.OrderBy(row => row.AwayKm)
				.ThenByDescending(row => row.Track!.FirstSharedUtc)
				.ThenBy(row => row.Track!.Id)

			// Newest shared first otherwise, tiebroken on Id for the reason §17.8 gives: the fake
			// clock does not tick unless a test moves it and a real one has finite resolution, so
			// two routes genuinely share an instant — and a sort without a tiebreak drops one row
			// and repeats another across a page boundary.
			: scored
				.OrderByDescending(row => row.Track!.FirstSharedUtc)
				.ThenBy(row => row.Track!.Id);

		bool measured = query.HasArea;

		List<SharedTrackSummary> items = await ordered
			.Skip((query.Page - 1) * SharedTrackQuery.PageSize)
			.Take(SharedTrackQuery.PageSize)
			.Select(row => new SharedTrackSummary(
				row.Track!.Id,
				row.Track.Name,
				row.Track.Description,
				row.Track.PhotoId,

				// The username, never DisplayName. §7.3 is explicit that a self-chosen label never
				// replaces the username on somebody else's screen, and a browse list is exactly
				// the screen a chosen label would be chosen for.
				row.Track.Owner!.UserName ?? "Unknown",
				row.Track.DistanceM,
				row.Track.AscentM,

				// Never null on a public row — FirstSharedUtc is stamped by the transition that
				// makes a track public — but the column is nullable, so the projection says so.
				row.Track.FirstSharedUtc ?? row.Track.CreatedUtc,
				(row.Track.BoundsMinLat + row.Track.BoundsMaxLat) / 2,
				(row.Track.BoundsMinLon + row.Track.BoundsMaxLon) / 2,

				// Null, not zero, when nothing was measured. Zero means "you are standing on it" (§8).
				measured ? row.AwayKm : null))
			.ToListAsync(cancellationToken);

		return Ok(new SharedTrackPage(items, query.Page, SharedTrackQuery.PageSize, total));
	}

	/// <summary>
	/// A candidate row with its distance from the point the rider filtered around, as one
	/// intermediate projection the filter, the sort and the response all read.
	/// <para>
	/// <strong>The reason this type exists is that the haversine has to be written as an
	/// expression tree, not called as a method.</strong> Every operation in it is one Npgsql
	/// emits (<c>sin</c>, <c>cos</c>, <c>asin</c>, <c>sqrt</c>, <c>least</c>), but a call into a
	/// C# method is not translatable at all — EF would refuse the query outright. Projecting once
	/// into this and then filtering, ordering and reading <see cref="AwayKm"/> keeps the formula
	/// in one place instead of three copies drifting apart.
	/// </para>
	/// <para>
	/// It is computed even when no area was asked for, and simply not read — the alternative is
	/// two whole query shapes, and the cost is some trigonometry Postgres does on the twenty rows
	/// of one page.
	/// </para>
	/// <para>
	/// The formula is <see cref="Distance.BetweenM"/>'s, deliberately: §15.7's rule is that one
	/// distance implementation serves the whole project, and a browse list saying a route is 48 km
	/// away while the map that draws it disagrees would be that rule breaking quietly. The
	/// half-angle sine also handles the antimeridian on its own — a longitude difference of 359°
	/// and one of −1° give the same value once halved and squared.
	/// </para>
	/// </summary>
	private sealed class ScoredTrack
	{
		/// <summary>The row.</summary>
		public Track? Track { get; init; }

		/// <summary>Great-circle kilometres from the filter's centre to the track's bounding-box centre.</summary>
		public double AwayKm { get; init; }
	}

	/// <summary>
	/// The fingerprint of a stored track's line, read back from its blob (§6.2).
	/// <para>
	/// Only ever needed for a row written before <c>route_hash</c> existed — every path that
	/// writes points computes it as it goes. Publishing is a deliberate, once-per-route action,
	/// so reading one blob to close that gap costs nothing anybody will notice.
	/// </para>
	/// </summary>
	/// <param name="blobs">Where the points are.</param>
	/// <param name="blobRef">Which blob.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <returns>The fingerprint, or empty when the blob is gone — which means "do not compare".</returns>
	private static async Task<byte[]> FingerprintAsync(IBlobStore blobs, string blobRef, CancellationToken cancellationToken)
	{
		await using Stream? blob = await blobs.OpenAsync(blobRef, cancellationToken);

		return blob is null ? [] : RouteFingerprint.Of(TrackBlobCodec.Read(blob));
	}

	/// <summary>Kilometres in a degree of latitude. Constant everywhere, which is why the box prefilter uses this half.</summary>
	private const double KmPerDegreeLatitude = Math.PI * Distance.EarthRadiusM / 180.0 / 1000.0;

	/// <summary>Mean earth radius in kilometres, so the whole expression stays in the units the query talks in.</summary>
	private const double EarthRadiusKm = Distance.EarthRadiusM / 1000.0;

	/// <summary>Degrees to radians, written out because <c>double.DegreesToRadians</c> has no SQL translation.</summary>
	private const double Radians = Math.PI / 180.0;

	/// <summary>The backslash, as the escape character handed to <c>ILIKE</c>.</summary>
	private const string EscapeCharacter = "\\";

	/// <summary>
	/// Escapes what a rider typed so <c>%</c> and <c>_</c> are the characters they typed rather
	/// than wildcards. Without this, a search for <c>_</c> matches every route on the service.
	/// </summary>
	/// <param name="value">The cleaned search text.</param>
	private static string Escape(string value) => value
		.Replace("\\", "\\\\", StringComparison.Ordinal)
		.Replace("%", "\\%", StringComparison.Ordinal)
		.Replace("_", "\\_", StringComparison.Ordinal);
}
