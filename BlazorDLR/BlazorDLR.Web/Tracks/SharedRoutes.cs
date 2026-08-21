using DLR.Core.Tracks;
using DLR.Server.Data;
using DLR.Server.Data.Tracks;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tracks;

/// <summary>
/// The two rules a route only has to satisfy once it goes in front of everybody (§6.2).
/// <para>
/// A private track is the rider's own filing system: two of them may be called the same thing,
/// and a second copy of the same ride is nobody else's business (§15.3). The browse list is a
/// catalogue, and both of those become other riders' problem the moment a route is on it — a
/// name that is on three rows identifies nothing, and the same road listed twice is a page of
/// results that is mostly one route.
/// </para>
/// <para>
/// <strong>Checked here rather than by a unique index.</strong> The point of the check is the
/// sentence it produces: <em>this is already shared, as “Coast run north”</em> is something a
/// rider can act on, and a constraint violation is not. The race it loses to — two riders
/// pressing Share on the same name in the same instant — puts a second row on a list rather
/// than corrupting anything, and the loser can rename.
/// </para>
/// </summary>
public static class SharedRoutes
{
	/// <summary>The backslash, as the escape character handed to <c>ILIKE</c>.</summary>
	private const string EscapeCharacter = "\\";

	/// <summary>
	/// Another rider's public route over the same coordinates, or null when this route is new to
	/// the list.
	/// </summary>
	/// <param name="database">The one context.</param>
	/// <param name="ownerId">Who is publishing. Their own rows are not candidates — see below.</param>
	/// <param name="routeHash">The publishing route's <see cref="RouteFingerprint"/>.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <remarks>
	/// <para>
	/// Scoped to <em>other</em> owners deliberately. A rider who has the same line twice — a
	/// recording and the route they planned it from — is entitled to publish whichever of them
	/// they consider the good copy, and to change their mind later; refusing that would be this
	/// check telling somebody they may not share their own route.
	/// </para>
	/// <para>
	/// An empty hash matches nothing. It means the fingerprint is unknown rather than that the
	/// track has no points, and a comparison against it would otherwise match every other row
	/// whose fingerprint is also unknown.
	/// </para>
	/// </remarks>
	public static Task<PublishedRoute?> WithTheSamePointsAsync(
		DlrDbContext database,
		Guid ownerId,
		byte[] routeHash,
		CancellationToken cancellationToken = default)
	{
		if (routeHash.Length == 0)
		{
			return Task.FromResult<PublishedRoute?>(null);
		}

		return database
			.Set<Track>()
			.AsNoTracking()
			.Where(track =>
				track.Visibility == TrackVisibility.Public
				&& track.OwnerId != ownerId
				&& track.RouteHash == routeHash)

			// Oldest first, so the answer is the route that got there first rather than whichever
			// row the planner happened to reach.
			.OrderBy(track => track.FirstSharedUtc)
			.ThenBy(track => track.Id)
			.Select(track => new PublishedRoute(track.Id, track.Name))
			.FirstOrDefaultAsync(cancellationToken);
	}

	/// <summary>
	/// The public route already using this name, or null when the name is free.
	/// </summary>
	/// <param name="database">The one context.</param>
	/// <param name="exceptTrackId">
	/// The route being published or renamed. Excluded so that re-sharing a route, or renaming it
	/// to what it is already called, is not a clash with itself.
	/// </param>
	/// <param name="name">The cleaned name (<see cref="TrackNaming.Clean"/>).</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <remarks>
	/// Case-insensitively, and by <c>ILIKE</c> rather than <c>ToLower()</c> for the reason the
	/// browse filter gives: case folding is the database's job and is collation-aware, and every
	/// wildcard in a name a rider typed is text. "Coast Run North" and "coast run north" are the
	/// same name on a list somebody is reading.
	/// </remarks>
	public static Task<PublishedRoute?> NamedAsync(
		DlrDbContext database,
		Guid exceptTrackId,
		string name,
		CancellationToken cancellationToken = default)
	{
		string pattern = Escape(name);

		return database
			.Set<Track>()
			.AsNoTracking()
			.Where(track =>
				track.Visibility == TrackVisibility.Public
				&& track.Id != exceptTrackId
				&& track.Name != null
				&& EF.Functions.ILike(track.Name, pattern, EscapeCharacter))
			.OrderBy(track => track.FirstSharedUtc)
			.ThenBy(track => track.Id)
			.Select(track => new PublishedRoute(track.Id, track.Name))
			.FirstOrDefaultAsync(cancellationToken);
	}

	/// <summary>
	/// Escapes a name so that <c>%</c> and <c>_</c> in it are the characters somebody typed
	/// rather than wildcards. Without this a route called <c>_</c> would collide with every name
	/// of one character, and one called <c>%</c> with every name there is.
	/// </summary>
	/// <param name="value">The cleaned name.</param>
	private static string Escape(string value) => value
		.Replace("\\", "\\\\", StringComparison.Ordinal)
		.Replace("%", "\\%", StringComparison.Ordinal)
		.Replace("_", "\\_", StringComparison.Ordinal);
}

/// <summary>
/// A route that is already on the browse list, as the two checks above report it.
/// <para>
/// The owner is deliberately not carried. Naming the route is enough for the rider to go and
/// look at it, while naming the rider would say who shared it — including when that is somebody
/// the caller has blocked, whose routes §17.7 has already taken off their screen.
/// </para>
/// </summary>
/// <param name="Id">The route that is already shared.</param>
/// <param name="Name">What it is called, or null if it was shared without a name.</param>
public sealed record PublishedRoute(Guid Id, string? Name)
{
	/// <summary>What to call it in a sentence, when it may not have a name at all.</summary>
	public string Describe => string.IsNullOrWhiteSpace(Name) ? "an unnamed route" : $"“{Name}”";
}
