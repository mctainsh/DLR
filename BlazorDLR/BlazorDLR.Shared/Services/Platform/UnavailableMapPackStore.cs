namespace BlazorDLR.Shared.Services.Platform;

/// <summary>
/// The <see cref="IMapPackStore"/> both browser hosts bind, and the one the SSR pass gets (§18.6).
/// <para>
/// A browser has nowhere to put a few hundred megabytes that survives a tab closing, and the web
/// app is the big-screen surface for planning and reading rather than the thing clamped to a set
/// of bars. So this holds nothing and says so, and <c>MapSourceState</c> resolves an offline
/// source to OpenStreetMap here — a working map under the routes and pins, which is what the
/// screen is actually for.
/// </para>
/// </summary>
public sealed class UnavailableMapPackStore : IMapPackStore
{
	/// <inheritdoc />
	public bool IsSupported => false;

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<StoredMapPack>> ListAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromResult<IReadOnlyList<StoredMapPack>>([]);

	/// <inheritdoc />
	public ValueTask<Stream?> OpenReadAsync(string packId, CancellationToken cancellationToken = default) =>
		ValueTask.FromResult<Stream?>(null);

	/// <inheritdoc />
	public ValueTask DeleteAsync(string packId, CancellationToken cancellationToken = default) =>
		ValueTask.CompletedTask;

	/// <inheritdoc />
	public ValueTask<long> PartialLengthAsync(string packId, int version, CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(0L);

	/// <summary>
	/// <c>null</c>, which is what stops the downloader before it opens a connection: there is
	/// nowhere for the bytes to go, and finding that out after a few hundred megabytes would be a
	/// poor way to learn it.
	/// </summary>
	public ValueTask<Stream?> OpenWriteAsync(string packId, int version, bool restart, CancellationToken cancellationToken = default) =>
		ValueTask.FromResult<Stream?>(null);

	/// <inheritdoc />
	public ValueTask<Stream?> OpenPartialReadAsync(string packId, int version, CancellationToken cancellationToken = default) =>
		ValueTask.FromResult<Stream?>(null);

	/// <inheritdoc />
	public ValueTask<bool> CommitAsync(string packId, int version, CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(false);

	/// <inheritdoc />
	public ValueTask DiscardAsync(string packId, int version, CancellationToken cancellationToken = default) =>
		ValueTask.CompletedTask;

	/// <inheritdoc />
	public ValueTask<int> NextVersionAsync(string packId, CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(1);
}
