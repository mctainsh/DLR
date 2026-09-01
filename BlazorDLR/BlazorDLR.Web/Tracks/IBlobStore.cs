namespace DLR.Server.Tracks;

/// <summary>
/// Where track blobs and photos live (§9.1).
/// <para>
/// A Docker volume on the VPS, not object storage. One place to back up, no S3 credentials in
/// the running process, and no egress bill - and the workload is two to three orders of
/// magnitude inside a CX22's 20 TB allowance, so a CDN and a bucket would solve a problem this
/// project does not have.
/// </para>
/// <para>
/// The interface is the seam that keeps that decision cheap to revisit: moving to S3-compatible
/// storage later is a registration change. Doing it now would add a dependency to save nothing.
/// </para>
/// </summary>
public interface IBlobStore
{
	/// <summary>Stores a blob and returns the reference to read it back by.</summary>
	/// <param name="content">The bytes; read to the end.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	Task<string> PutAsync(Stream content, CancellationToken cancellationToken = default);

	/// <summary>Opens a blob for reading, or null if there is no such blob.</summary>
	/// <param name="blobRef">What <see cref="PutAsync"/> returned.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	Task<Stream?> OpenAsync(string blobRef, CancellationToken cancellationToken = default);

	/// <summary>
	/// Removes a blob. Deleting one that is already gone is not an error - the nightly sweep
	/// for orphaned blobs (§7.11) would otherwise have to distinguish "already tidy" from
	/// "failed", and both mean the same thing.
	/// </summary>
	/// <param name="blobRef">What <see cref="PutAsync"/> returned.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	Task DeleteAsync(string blobRef, CancellationToken cancellationToken = default);

	/// <summary>Whether a blob is there.</summary>
	/// <param name="blobRef">What <see cref="PutAsync"/> returned.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	Task<bool> ExistsAsync(string blobRef, CancellationToken cancellationToken = default);

	/// <summary>
	/// Every blob in the store, with when it was written - the input to the orphan sweep (§7.11).
	/// <para>
	/// <c>ON DELETE CASCADE</c> reaches rows and not files, so the only way to find a blob nothing
	/// points at is to enumerate what is there and subtract what is referenced. Streamed rather
	/// than returned as a list because the answer is "everything on the volume".
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancellation.</param>
	IAsyncEnumerable<BlobEntry> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>One stored blob, as the orphan sweep sees it.</summary>
/// <param name="BlobRef">The reference, as a row would hold it.</param>
/// <param name="WrittenUtc">
/// When it was written. <strong>Read from the store, never from the database</strong> - the whole
/// question the sweep is asking is what the database does not know about.
/// </param>
public readonly record struct BlobEntry(string BlobRef, DateTimeOffset WrittenUtc);
