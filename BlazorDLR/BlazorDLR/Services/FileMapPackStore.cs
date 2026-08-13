using System.Globalization;
using BlazorDLR.Shared.Services;

namespace BlazorDLR.Services;

/// <summary>
/// The mobile binding for <see cref="IMapPackStore"/> — PMTiles archives under
/// <c>FileSystem.AppDataDirectory/mappacks/</c> (§4.5, §13 Q26).
/// <para>
/// <strong>App data, not the cache directory</strong>, for the same reason
/// <see cref="FileOfflineStore"/> is: the OS reclaims the cache directory whenever it feels short,
/// and the rider this exists for is the one who cannot refetch. It is also the largest thing this
/// app puts on a phone by a wide margin, which makes the settings screen's delete button part of
/// the feature rather than a nicety.
/// </para>
/// <para>
/// <strong>One directory per pack: <c>mappacks/{packId}/v{version}.pmtiles</c>.</strong> The
/// obvious flat layout — <c>{packId}.v{version}.pmtiles</c> — cannot be parsed back reliably,
/// because a catalogue id is free to contain a dot and the version then stops being findable.
/// A directory per pack makes the id a name rather than a prefix, makes deleting a pack a
/// directory removal, and makes replacing a version a write-then-delete inside a folder nothing
/// else is looking at.
/// </para>
/// </summary>
public sealed class FileMapPackStore : IMapPackStore
{
	/// <summary>The folder every archive lives under, inside the app's data directory.</summary>
	private const string FolderName = "mappacks";

	/// <summary>What an archive is called inside its pack's folder. The version is the whole name.</summary>
	private const string FilePrefix = "v";

	private const string FileExtension = ".pmtiles";

	/// <summary>Appended while a download is in flight. See <see cref="PartialPathFor"/>.</summary>
	private const string PartialExtension = ".part";

	/// <inheritdoc />
	public bool IsSupported => true;

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<StoredMapPack>> ListAsync(CancellationToken cancellationToken = default)
	{
		List<StoredMapPack> packs = [];

		try
		{
			if (Directory.Exists(Root))
			{
				foreach (string directory in Directory.EnumerateDirectories(Root))
				{
					cancellationToken.ThrowIfCancellationRequested();

					if (Newest(directory) is { } pack)
					{
						packs.Add(pack);
					}
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A directory that cannot be read is the same answer as one that is not there. The
			// settings screen shows what it can and the map falls back to an online source.
		}

		return ValueTask.FromResult<IReadOnlyList<StoredMapPack>>(packs);
	}

	/// <inheritdoc />
	public ValueTask<Stream?> OpenReadAsync(string packId, CancellationToken cancellationToken = default)
	{
		if (FolderFor(packId) is not { } folder || Newest(folder) is not { } pack)
		{
			return ValueTask.FromResult<Stream?>(null);
		}

		try
		{
			// FileShare.ReadWrite, not Read: several tiles are fetched at once on any pan, and the
			// downloader may be writing a newer version into the same folder while a map is reading
			// the current one. A stream that locked the file would serialise the map behind itself.
			Stream stream = new FileStream(
				PathFor(folder, pack.Version),
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite,
				bufferSize: 0,
				FileOptions.RandomAccess | FileOptions.Asynchronous);

			return ValueTask.FromResult<Stream?>(stream);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Deleted underneath us between the enumeration and the open, or unreadable. The map
			// falls back to an online source rather than failing.
			return ValueTask.FromResult<Stream?>(null);
		}
	}

	/// <inheritdoc />
	public ValueTask DeleteAsync(string packId, CancellationToken cancellationToken = default)
	{
		if (FolderFor(packId) is { } folder)
		{
			try { Directory.Delete(folder, recursive: true); }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
		}

		return ValueTask.CompletedTask;
	}

	// -- Writing --------------------------------------------------------------------------------

	/// <inheritdoc />
	public ValueTask<long> PartialLengthAsync(string packId, int version, CancellationToken cancellationToken = default)
	{
		if (FolderFor(packId) is not { } folder)
		{
			return ValueTask.FromResult(0L);
		}

		try
		{
			FileInfo partial = new(PartialPathFor(folder, version));
			return ValueTask.FromResult(partial.Exists ? partial.Length : 0L);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Unreadable is the same answer as absent: the download starts from the beginning,
			// which costs bandwidth rather than correctness.
			return ValueTask.FromResult(0L);
		}
	}

	/// <inheritdoc />
	public ValueTask<Stream?> OpenWriteAsync(string packId, int version, bool restart, CancellationToken cancellationToken = default)
	{
		if (FolderFor(packId) is not { } folder)
		{
			return ValueTask.FromResult<Stream?>(null);
		}

		try
		{
			Directory.CreateDirectory(folder);

			string path = PartialPathFor(folder, version);

			if (restart && File.Exists(path))
			{
				File.Delete(path);
			}

			// Append, so a resumed download continues where the last one stopped. The caller has
			// already told the server where that is; opening anywhere else would interleave.
			Stream stream = new FileStream(
				path,
				FileMode.Append,
				FileAccess.Write,
				FileShare.Read,
				bufferSize: 64 * 1024,
				FileOptions.Asynchronous);

			return ValueTask.FromResult<Stream?>(stream);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A full disk is the common one, and it is worth the caller reporting rather than
			// throwing from here — see IMapPackStore's posture.
			return ValueTask.FromResult<Stream?>(null);
		}
	}

	/// <inheritdoc />
	public ValueTask<Stream?> OpenPartialReadAsync(string packId, int version, CancellationToken cancellationToken = default)
	{
		if (FolderFor(packId) is not { } folder)
		{
			return ValueTask.FromResult<Stream?>(null);
		}

		try
		{
			string path = PartialPathFor(folder, version);

			return ValueTask.FromResult<Stream?>(File.Exists(path)
				? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous)
				: null);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return ValueTask.FromResult<Stream?>(null);
		}
	}

	/// <inheritdoc />
	public ValueTask<bool> CommitAsync(string packId, int version, CancellationToken cancellationToken = default)
	{
		if (FolderFor(packId) is not { } folder)
		{
			return ValueTask.FromResult(false);
		}

		try
		{
			string partial = PartialPathFor(folder, version);

			if (!File.Exists(partial))
			{
				return ValueTask.FromResult(false);
			}

			// The move is what makes the archive live, and it is last. Everything before it leaves
			// the previous version readable, so a download that fails at any point costs the rider
			// bandwidth and never the map they already had.
			File.Move(partial, PathFor(folder, version), overwrite: true);

			// Only now are the older ones redundant. A map holding one open keeps reading it until
			// it closes — the delete unlinks the name, and both platforms let the open handle live.
			foreach (string path in Directory.EnumerateFiles(folder, FilePrefix + "*" + FileExtension))
			{
				if (!string.Equals(path, PathFor(folder, version), StringComparison.Ordinal))
				{
					try { File.Delete(path); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
				}
			}

			return ValueTask.FromResult(true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return ValueTask.FromResult(false);
		}
	}

	/// <inheritdoc />
	public ValueTask DiscardAsync(string packId, int version, CancellationToken cancellationToken = default)
	{
		if (FolderFor(packId) is { } folder)
		{
			try { File.Delete(PartialPathFor(folder, version)); }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
		}

		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask<int> NextVersionAsync(string packId, CancellationToken cancellationToken = default)
	{
		if (FolderFor(packId) is not { } folder || !Directory.Exists(folder))
		{
			return ValueTask.FromResult(1);
		}

		try
		{
			return ValueTask.FromResult((Newest(folder)?.Version ?? 0) + 1);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return ValueTask.FromResult(1);
		}
	}

	private static string Root => Path.Combine(FileSystem.AppDataDirectory, FolderName);

	/// <summary>
	/// Where a pack's archives live, or <c>null</c> for an id this store refuses.
	/// <para>
	/// Refused rather than sanitised, exactly as <see cref="FileOfflineStore"/> does it: every id
	/// comes from the server's catalogue and is a slug by construction, so anything else is a bug
	/// rather than input to clean up — and refusing means no id can contain a separator or a
	/// <c>..</c> and address a directory outside this one.
	/// </para>
	/// </summary>
	private static string? FolderFor(string packId)
	{
		if (string.IsNullOrEmpty(packId) || packId.Length > 64 || !char.IsAsciiLetterOrDigit(packId[0]))
		{
			return null;
		}

		foreach (char character in packId)
		{
			if (!char.IsAsciiLetterOrDigit(character) && character is not '-')
			{
				return null;
			}
		}

		return Path.Combine(Root, packId);
	}

	private static string PathFor(string folder, int version) =>
		Path.Combine(folder, FilePrefix + version.ToString(CultureInfo.InvariantCulture) + FileExtension);

	/// <summary>
	/// Where a download in progress is written. A different extension, not a different folder, so
	/// the two live together and <see cref="Newest"/>'s <c>v*.pmtiles</c> glob cannot pick up a
	/// half-finished archive and hand it to the renderer.
	/// </summary>
	private static string PartialPathFor(string folder, int version) =>
		PathFor(folder, version) + PartialExtension;

	/// <summary>
	/// The highest version present in a pack's folder, or <c>null</c> when it holds no readable
	/// archive.
	/// <para>
	/// Highest rather than only, because a replacement writes the new version before removing the
	/// old and a phone killed between the two leaves both. Answering with the newer one is the
	/// behaviour that makes that crash harmless.
	/// </para>
	/// </summary>
	private static StoredMapPack? Newest(string folder)
	{
		string packId = Path.GetFileName(folder);
		StoredMapPack? newest = null;

		foreach (string path in Directory.EnumerateFiles(folder, FilePrefix + "*" + FileExtension))
		{
			string name = Path.GetFileNameWithoutExtension(path);

			if (!int.TryParse(
				name.AsSpan(FilePrefix.Length),
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out int version))
			{
				continue;
			}

			if (newest is not null && version <= newest.Version)
			{
				continue;
			}

			newest = new StoredMapPack(packId, version, new FileInfo(path).Length);
		}

		return newest;
	}
}
