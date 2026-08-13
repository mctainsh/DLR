namespace BlazorDLR.Shared.Services;

/// <summary>
/// One offline map archive on this device (§4.5, §13 Q26).
/// </summary>
/// <param name="PackId">
/// Which pack — the catalogue's id, <c>au-nsw</c> and the like. Unique on a device: a second
/// version of the same region replaces the first rather than sitting beside it.
/// </param>
/// <param name="Version">
/// The catalogue version this file was built from. What tells a device holding last month's
/// extract that a newer one exists, without opening the archive to find out.
/// </param>
/// <param name="SizeBytes">How much of the phone it is using. The number the settings screen shows.</param>
public sealed record StoredMapPack(string PackId, int Version, long SizeBytes);

/// <summary>
/// Where downloaded map archives live on this device (§4.5).
/// <para>
/// <strong>Separate from <see cref="IOfflineStore"/>, and the difference is what reads them.</strong>
/// That one holds a ride's snapshot: a few kilobytes of JSON, written and read whole by C#. These
/// are hundreds of megabytes read by <em>MapLibre</em>, in ranges, over HTTP — so what this seam
/// has to expose is not "give me the content" but a stream something else can seek around in, and
/// a way for <c>MapPackServer</c> to hand bytes out of it.
/// </para>
/// <para>
/// <strong>Phone only.</strong> §18.6 keeps offline a property of the thing in the rider's pocket;
/// both browser hosts bind a store that holds nothing. A caller does not branch on which host it
/// is running in — it asks, gets nothing, and falls back to an online source, which is what a
/// browser was going to do anyway.
/// </para>
/// <para>
/// <strong>Reading never throws.</strong> Same posture as the rest of the device seams: an archive
/// deleted underneath a running map, a sandbox that moved after a restore and a first run are one
/// answer to a caller — nothing here — and the map falls back rather than failing.
/// </para>
/// <para>
/// <strong>Writing is deliberately split into four small calls</strong> rather than one "save
/// this stream". A pack is hundreds of megabytes over a phone connection, so the download has to
/// survive being interrupted and resume where it stopped — which means the partial file is a
/// first-class thing with a length the downloader can ask about, and the move into place is a
/// separate step that only happens once the bytes are all there. Keeping all four here is what
/// keeps the file layout knowledge in one type.
/// </para>
/// </summary>
public interface IMapPackStore
{
	/// <summary>
	/// Whether this host can hold archives at all. False on both browser hosts, where
	/// <see cref="ListAsync"/> is always empty and <see cref="OpenReadAsync"/> always null.
	/// </summary>
	bool IsSupported { get; }

	/// <summary>
	/// Every archive on this device, in no particular order. Empty rather than null on a host
	/// that holds none.
	/// </summary>
	/// <param name="cancellationToken">Cancels the enumeration.</param>
	ValueTask<IReadOnlyList<StoredMapPack>> ListAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Opens an archive for random access, or answers <c>null</c> when this device does not hold
	/// it.
	/// <para>
	/// <strong>The stream must be seekable</strong>, because that is the whole point: PMTiles is
	/// read by range, and a forward-only stream would mean reading a gigabyte to serve a tile in
	/// the middle of it. The caller owns the stream and disposes it.
	/// </para>
	/// <para>
	/// Opened for shared reading. Several tiles are fetched at once on any pan, so a stream that
	/// locked the file would serialise the map behind itself.
	/// </para>
	/// </summary>
	/// <param name="packId">Which archive.</param>
	/// <param name="cancellationToken">Cancels the open.</param>
	ValueTask<Stream?> OpenReadAsync(string packId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Removes an archive, freeing what is usually the largest single thing this app has put on
	/// the phone. Silent when there is nothing to remove.
	/// </summary>
	/// <param name="packId">Which archive.</param>
	/// <param name="cancellationToken">Cancels the removal.</param>
	ValueTask DeleteAsync(string packId, CancellationToken cancellationToken = default);

	// -- Writing (§4.4's downloader) ----------------------------------------------------------

	/// <summary>
	/// How many bytes of a part-downloaded archive are already on this device, or <c>0</c> when
	/// there is nothing to resume. This is what the downloader turns into a <c>Range</c> header.
	/// </summary>
	/// <param name="packId">Which archive.</param>
	/// <param name="version">Which version of it.</param>
	/// <param name="cancellationToken">Cancels the read.</param>
	ValueTask<long> PartialLengthAsync(string packId, int version, CancellationToken cancellationToken = default);

	/// <summary>
	/// Opens the part-downloaded archive for appending, creating it if it does not exist. Answers
	/// <c>null</c> on a host that stores nothing, or for a pack id this store refuses.
	/// </summary>
	/// <param name="packId">Which archive.</param>
	/// <param name="version">Which version of it.</param>
	/// <param name="restart">
	/// True to truncate anything already there first. What the downloader passes when the server
	/// ignored its <c>Range</c> and answered <c>200</c> — appending a whole file onto a partial one
	/// would produce a corrupt archive of exactly the expected length, which is the worst kind.
	/// </param>
	/// <param name="cancellationToken">Cancels the open.</param>
	ValueTask<Stream?> OpenWriteAsync(string packId, int version, bool restart, CancellationToken cancellationToken = default);

	/// <summary>
	/// Opens a part-downloaded archive for reading, or <c>null</c> when there is none.
	/// <para>
	/// What the downloader checks its work with before committing: the PMTiles magic bytes, so a
	/// URL that answered with an HTML error page is caught rather than saved, and the SHA-256. Both
	/// have to happen while the file is still a partial — after <see cref="CommitAsync"/> a bad
	/// archive is the live one, and the renderer's failure happens inside a WebView where nothing
	/// can explain it.
	/// </para>
	/// </summary>
	/// <param name="packId">Which archive.</param>
	/// <param name="version">Which version of it.</param>
	/// <param name="cancellationToken">Cancels the open.</param>
	ValueTask<Stream?> OpenPartialReadAsync(string packId, int version, CancellationToken cancellationToken = default);

	/// <summary>
	/// Moves a completed download into place and removes every older version of the same pack.
	/// <para>
	/// The last step, and the only one that changes what <see cref="OpenReadAsync"/> answers — so
	/// a download interrupted at any point before this leaves the previous archive intact and
	/// still readable.
	/// </para>
	/// </summary>
	/// <param name="packId">Which archive.</param>
	/// <param name="version">Which version of it.</param>
	/// <param name="cancellationToken">Cancels the move.</param>
	/// <returns>True when the archive is now the live one.</returns>
	ValueTask<bool> CommitAsync(string packId, int version, CancellationToken cancellationToken = default);

	/// <summary>
	/// Throws a part-downloaded archive away — a failed checksum, or a rider who cancelled. Leaves
	/// any committed version of the same pack alone.
	/// </summary>
	/// <param name="packId">Which archive.</param>
	/// <param name="version">Which version of it.</param>
	/// <param name="cancellationToken">Cancels the removal.</param>
	ValueTask DiscardAsync(string packId, int version, CancellationToken cancellationToken = default);

	/// <summary>
	/// The version a fresh download should claim: one past the newest already on the device, or
	/// <c>1</c> when there is none.
	/// <para>
	/// Monotonic rather than reusing the number, so a re-download never writes into the file a map
	/// is currently reading — the new one lands beside it and <see cref="CommitAsync"/> retires the
	/// old one only once it is whole.
	/// </para>
	/// </summary>
	/// <param name="packId">Which archive.</param>
	/// <param name="cancellationToken">Cancels the read.</param>
	ValueTask<int> NextVersionAsync(string packId, CancellationToken cancellationToken = default);
}
