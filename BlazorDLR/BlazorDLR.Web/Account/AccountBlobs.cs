using DLR.Server.Data;
using DLR.Server.Data.Photos;
using DLR.Server.Data.Tracks;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Account;

/// <summary>
/// Every blob an account owns — the list that has to be gathered <em>before</em> the rows go
/// (§16.6).
/// <para>
/// <strong>`ON DELETE CASCADE` does not reach a filesystem.</strong> Deleting the account takes
/// `track`, `track_revision` and `photo` with it in one statement, and the moment it does there is
/// nothing left to say which files were theirs. An orphaned blob is a privacy failure that looks
/// like a storage bill: the photograph a rider deleted their account to be rid of is still on the
/// disk, still in tonight's backup, and nothing in the database admits it exists.
/// </para>
/// <para>
/// The nightly sweep (§7.11) is the backstop rather than the mechanism. It runs once a day behind a
/// grace window, so relying on it would mean "deleted" meaning "gone by tomorrow" — which is not
/// what somebody pressing this button is asking for.
/// </para>
/// </summary>
public static class AccountBlobs
{
	/// <summary>
	/// Collects every blob reference the account owns, in one round trip per table.
	/// </summary>
	/// <param name="database">The one context.</param>
	/// <param name="userId">Whose blobs.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	/// <remarks>
	/// <para>
	/// <strong>Scoped to the owner on every table, including the one reached indirectly.</strong>
	/// A revision belongs to a track and the track is what carries the owner, so the join is the
	/// only thing keeping this from returning every retained original on the server. That mistake
	/// deletes other people's data and leaves no trace of having done it.
	/// </para>
	/// </remarks>
	public static async Task<IReadOnlyList<string>> OwnedByAsync(
		DlrDbContext database,
		Guid userId,
		CancellationToken cancellationToken = default)
	{
		List<string> tracks = await database
			.Set<Track>()
			.Where(track => track.OwnerId == userId)
			.Select(track => track.BlobRef)
			.ToListAsync(cancellationToken);

		List<string> revisions = await database
			.Set<TrackRevision>()
			.Where(revision => revision.Track!.OwnerId == userId)
			.Select(revision => revision.BlobRef)
			.ToListAsync(cancellationToken);

		// Both objects. A photo is two files — the stored image and the callout thumbnail — and a
		// sweep or a delete that reads only BlobRef leaves every thumbnail behind. Projected as a
		// pair and flattened in memory, because a two-element array literal is not something the
		// query translator can put into SQL.
		// An anonymous type rather than a tuple: Npgsql reads a projected `ValueTuple` back as a
		// PostgreSQL `record` and refuses it unless records-as-tuples is enabled globally, which
		// is a data-source-wide setting to buy nothing here.
		var photos = await database
			.Set<Photo>()
			.Where(photo => photo.OwnerId == userId)
			.Select(photo => new { photo.BlobRef, photo.ThumbBlobRef })
			.ToListAsync(cancellationToken);

		return
		[
			.. tracks
				.Concat(revisions)
				.Concat(photos.Select(photo => photo.BlobRef))
				.Concat(photos.Select(photo => photo.ThumbBlobRef))
				.Where(reference => reference.Length > 0)
				.Distinct(StringComparer.Ordinal),
		];
	}
}
