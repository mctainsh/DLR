using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IMapPackStore"/>. Stands in for the phone's file-backed store without
/// putting a few hundred megabytes on a build agent — the archives here are a few bytes each, and
/// the server under test cannot tell the difference because all it does is seek and copy.
/// </summary>
public sealed class FakeMapPackStore : IMapPackStore
{
	private readonly Dictionary<string, byte[]> _packs = new(StringComparer.Ordinal);

	/// <summary>Whether this store holds anything. Settable, so a test can play the browser (§18.6).</summary>
	public bool IsSupported { get; set; } = true;

	/// <summary>How many streams have been handed out and not yet disposed — a leak check.</summary>
	public int OpenStreams => _open;

	private int _open;

	/// <summary>Puts an archive on the device.</summary>
	public void Add(string packId, byte[] content, int version = 1)
	{
		_packs[packId] = content;
		Versions[packId] = version;
	}

	/// <summary>The version recorded for each pack, so <see cref="ListAsync"/> can report it.</summary>
	public Dictionary<string, int> Versions { get; } = new(StringComparer.Ordinal);

	public ValueTask<IReadOnlyList<StoredMapPack>> ListAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromResult<IReadOnlyList<StoredMapPack>>(IsSupported
			? [.. _packs.Select(pair => new StoredMapPack(pair.Key, Versions[pair.Key], pair.Value.LongLength))]
			: []);

	public ValueTask<Stream?> OpenReadAsync(string packId, CancellationToken cancellationToken = default)
	{
		if (!IsSupported || !_packs.TryGetValue(packId, out byte[]? content))
		{
			return ValueTask.FromResult<Stream?>(null);
		}

		Interlocked.Increment(ref _open);

		// Seekable, which is the property the interface actually requires — PMTiles is read by
		// range, and a forward-only stream would mean reading the whole archive to serve a tile
		// in the middle of it.
		return ValueTask.FromResult<Stream?>(new CountingMemoryStream(content, () => Interlocked.Decrement(ref _open)));
	}

	public ValueTask DeleteAsync(string packId, CancellationToken cancellationToken = default)
	{
		_packs.Remove(packId);
		Versions.Remove(packId);
		return ValueTask.CompletedTask;
	}

	// -- Writing ---------------------------------------------------------------------------------

	/// <summary>Part-downloaded archives, keyed the way the real store names its <c>.part</c> files.</summary>
	private readonly Dictionary<string, MemoryStream> _partials = new(StringComparer.Ordinal);

	/// <summary>Set to refuse every write, standing in for a full disk.</summary>
	public bool WritesFail { get; set; }

	/// <summary>The bytes of a partial, for a test that wants to see what a resume actually wrote.</summary>
	public byte[]? Partial(string packId, int version) =>
		_partials.TryGetValue(Key(packId, version), out MemoryStream? partial) ? partial.ToArray() : null;

	private static string Key(string packId, int version) => $"{packId}/v{version}";

	public ValueTask<long> PartialLengthAsync(string packId, int version, CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(_partials.TryGetValue(Key(packId, version), out MemoryStream? partial) ? partial.Length : 0L);

	public ValueTask<Stream?> OpenWriteAsync(string packId, int version, bool restart, CancellationToken cancellationToken = default)
	{
		if (!IsSupported || WritesFail)
		{
			return ValueTask.FromResult<Stream?>(null);
		}

		string key = Key(packId, version);

		if (restart || !_partials.TryGetValue(key, out MemoryStream? partial))
		{
			partial = new MemoryStream();
			_partials[key] = partial;
		}

		// Appending, like the real store's FileMode.Append — and left undisposed by the caller's
		// `await using` without losing the bytes, which a plain MemoryStream survives.
		partial.Seek(0, SeekOrigin.End);
		return ValueTask.FromResult<Stream?>(new NonClosingStream(partial));
	}

	public ValueTask<Stream?> OpenPartialReadAsync(string packId, int version, CancellationToken cancellationToken = default) =>
		ValueTask.FromResult<Stream?>(_partials.TryGetValue(Key(packId, version), out MemoryStream? partial)
			? new MemoryStream(partial.ToArray(), writable: false)
			: null);

	public ValueTask<bool> CommitAsync(string packId, int version, CancellationToken cancellationToken = default)
	{
		string key = Key(packId, version);

		if (!_partials.TryGetValue(key, out MemoryStream? partial))
		{
			return ValueTask.FromResult(false);
		}

		_packs[packId] = partial.ToArray();
		Versions[packId] = version;
		_partials.Remove(key);

		return ValueTask.FromResult(true);
	}

	public ValueTask DiscardAsync(string packId, int version, CancellationToken cancellationToken = default)
	{
		_partials.Remove(Key(packId, version));
		return ValueTask.CompletedTask;
	}

	public ValueTask<int> NextVersionAsync(string packId, CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(Versions.TryGetValue(packId, out int version) ? version + 1 : 1);

	/// <summary>
	/// Wraps the backing buffer so the caller's <c>await using</c> does not close it — the real
	/// store hands out a fresh <see cref="FileStream"/> per open over a file that persists, and a
	/// <see cref="MemoryStream"/> that closed would lose everything a resume is meant to keep.
	/// </summary>
	private sealed class NonClosingStream(MemoryStream inner) : Stream
	{
		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length => inner.Length;
		public override long Position { get => inner.Position; set => inner.Position = value; }

		public override void Flush() => inner.Flush();
		public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
			inner.WriteAsync(buffer, cancellationToken);

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();

		protected override void Dispose(bool disposing) { /* the buffer outlives this handle */ }
	}

	private sealed class CountingMemoryStream(byte[] buffer, Action onDisposed) : MemoryStream(buffer, writable: false)
	{
		private bool _counted;

		protected override void Dispose(bool disposing)
		{
			if (!_counted)
			{
				_counted = true;
				onDisposed();
			}

			base.Dispose(disposing);
		}
	}
}
