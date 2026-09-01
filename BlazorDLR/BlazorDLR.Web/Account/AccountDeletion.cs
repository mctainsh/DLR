using DLR.Server.Data;
using DLR.Server.Data.Identity;
using DLR.Server.Data.Moderation;
using DLR.Server.Data.Rides;
using DLR.Server.Hubs;
using DLR.Server.Tracks;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Account;

/// <summary>
/// Erasing one account and everything hanging off it (§6.3, §16.6).
/// <para>
/// <strong>One implementation, because there are two doors to it.</strong> A rider deletes their
/// own account with their password; an administrator deletes somebody else's from the roster
/// screen (§14.6). The authorisation differs and nothing else does - and the parts that are easy
/// to forget are the ones that are not a cascade: the blob list has to be read before the rows go,
/// <c>user_block.blocked_id</c> has to be cleared by hand, and the account's live hub connections
/// have to be evicted. A second copy of this would be a second chance to miss one.
/// </para>
/// </summary>
/// <param name="database">The one context.</param>
/// <param name="blobs">Where the files are.</param>
/// <param name="connections">The live connections this account still holds.</param>
/// <param name="logger">Where a blob that would not delete is recorded.</param>
public sealed class AccountDeletion(
	DlrDbContext database,
	IBlobStore blobs,
	RideConnections connections,
	ILogger<AccountDeletion> logger)
{
	/// <summary>Deletes the account's rows, evicts its connections, then deletes its blobs.</summary>
	/// <param name="userId">Which account.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public async Task DeleteAsync(Guid userId, CancellationToken cancellationToken = default)
	{
		// Read before the cascade takes the membership rows: afterwards nothing says which
		// adventures this account was watching, and its connections are still in their groups.
		List<Guid> rides = await database
			.Set<GroupRideMember>()
			.Where(member => member.UserId == userId)
			.Select(member => member.GroupRideId)
			.ToListAsync(cancellationToken);

		// Gathered before the rows go, for the reason on AccountBlobs: ON DELETE CASCADE reaches
		// rows and not a filesystem.
		IReadOnlyList<string> owned = await AccountBlobs.OwnedByAsync(database, userId, cancellationToken);

		// user_block.blocked_id is NO ACTION, not a cascade - two cascade paths into asp_net_users
		// through one table is an error in PostgreSQL (§16.5). The nightly sweep does the same
		// thing for the same reason; both would fail the whole delete without it.
		await database
			.Set<UserBlock>()
			.Where(block => block.BlockedId == userId)
			.ExecuteDeleteAsync(cancellationToken);

		await database
			.Set<AppUser>()
			.Where(row => row.Id == userId)
			.ExecuteDeleteAsync(cancellationToken);

		// After the rows, on the removal path's reasoning (§5.2): the account is gone and its hub
		// connections are not, and nothing re-runs JoinRide's membership check. Without this the
		// account just erased keeps receiving live positions for every adventure it was on.
		foreach (Guid rideId in rides)
		{
			await connections.EvictAsync(rideId, userId, cancellationToken);
		}

		// Rows first, blobs second, and a failure here is logged rather than thrown. The account is
		// already gone; answering 500 would say the deletion failed when it did not, and the §7.11
		// orphan sweep collects whatever is left as the backstop it is meant to be.
		foreach (string blobRef in owned)
		{
			try
			{
				await blobs.DeleteAsync(blobRef, cancellationToken);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				logger.LogError(
					exception,
					"Could not delete blob {BlobRef} for a deleted account; the nightly sweep will.",
					blobRef);
			}
		}
	}
}
