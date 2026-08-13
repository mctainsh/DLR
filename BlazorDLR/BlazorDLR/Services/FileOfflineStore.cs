using System.Text;
using BlazorDLR.Shared.Services;

namespace BlazorDLR.Services;

/// <summary>
/// The mobile binding for <see cref="IOfflineStore"/> — one UTF-8 file per entry under
/// <c>FileSystem.AppDataDirectory/offline/</c> (§4.4).
/// <para>
/// <strong>App data, not the cache directory.</strong> <c>FileSystem.CacheDirectory</c> is
/// storage the OS is entitled to reclaim whenever it feels short, and both platforms do. The
/// whole point of this store is the rider who relaunches in a dead zone, which is precisely the
/// moment there is no way to refetch what the OS threw away — so this is data the app keeps
/// until it decides otherwise, and it is small: one file per ride, tens of kilobytes.
/// </para>
/// <para>
/// <strong>Written through a temporary file.</strong> A phone killed mid-write — the OS
/// reclaiming an app that has just been backgrounded is the ordinary case, not the rare one —
/// would otherwise leave a half-written JSON document where a whole one used to be, and the
/// rider would relaunch with neither a network nor a readable copy. The replace is the last
/// thing that happens, so an entry is either the previous copy or the new one.
/// </para>
/// <para>
/// <strong>Nothing here throws.</strong> See <see cref="IOfflineStore"/>: a sandbox that moved
/// under the app after a restore, a full disk and a first run are one answer to the caller.
/// </para>
/// </summary>
public sealed class FileOfflineStore : IOfflineStore
{
	/// <summary>
	/// The subdirectory every entry lands in, so the app's data directory does not become a
	/// flat bag of files shared with whatever else MAUI puts there.
	/// </summary>
	private const string FolderName = "offline";

	/// <inheritdoc />
	public bool IsSupported => true;

	/// <inheritdoc />
	public async ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken = default)
	{
		if (PathFor(name) is not { } path)
		{
			return null;
		}

		try
		{
			return File.Exists(path)
				? await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken)
				: null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// An unreadable copy is the same answer as no copy: the caller goes to the network,
			// which is what it would have done on a device that had never stored anything.
			return null;
		}
	}

	/// <inheritdoc />
	public async ValueTask WriteAsync(string name, string content, CancellationToken cancellationToken = default)
	{
		if (PathFor(name) is not { } path)
		{
			return;
		}

		string temporary = path + ".tmp";

		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);

			await File.WriteAllTextAsync(temporary, content, Encoding.UTF8, cancellationToken);

			// Move rather than File.Replace: the destination does not exist on the first write of
			// an entry, and Replace requires it to. Delete-then-move is not atomic on either
			// platform, but the window it opens is between two copies of a cache — and the
			// temporary file is fully written before either happens, which is the failure that
			// actually costs the rider something.
			File.Move(temporary, path, overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A cache that cannot be written is not worth failing a screen over. Clean up so a
			// failed write does not leave a partial file behind for the next one to trip on.
			try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
		}
	}

	/// <inheritdoc />
	public ValueTask RemoveAsync(string name, CancellationToken cancellationToken = default)
	{
		if (PathFor(name) is { } path)
		{
			try { File.Delete(path); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
		}

		return ValueTask.CompletedTask;
	}

	/// <summary>
	/// Where an entry lives, or <c>null</c> for a name this store refuses.
	/// <para>
	/// <strong>Refused, not sanitised.</strong> Every name this app stores is assembled from a
	/// <see cref="Guid"/> and a constant prefix, so a name that is not a plain slug is a caller
	/// bug rather than user input to be cleaned up — and quietly rewriting it would map two
	/// different names onto one file. Refusing also means no name can ever contain a separator
	/// or a <c>..</c> and address a file outside this directory.
	/// </para>
	/// </summary>
	private static string? PathFor(string name)
	{
		// A leading dot is refused along with the rest: it is how both platforms spell "hidden",
		// and "." and ".." are directories rather than entries.
		if (string.IsNullOrEmpty(name) || name.Length > 128 || !char.IsAsciiLetterOrDigit(name[0]))
		{
			return null;
		}

		foreach (char character in name)
		{
			if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '.'))
			{
				return null;
			}
		}

		if (name.Contains("..", StringComparison.Ordinal))
		{
			return null;
		}

		return Path.Combine(FileSystem.AppDataDirectory, FolderName, name);
	}
}
