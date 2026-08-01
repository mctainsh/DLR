using Microsoft.Extensions.Options;

namespace DLR.Server.Tracks;

/// <summary>Where the blob volume is mounted (§9.1).</summary>
public sealed class BlobStoreOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "Blobs";

	/// <summary>
	/// The volume's path. A Docker volume in production; a temporary directory in tests.
	/// <para>
	/// The constraint that replaces bandwidth on this project is disk: a CX22 has 40 GB, and a
	/// full disk stops PostgreSQL writing rather than merely stopping uploads (§9.1). That is
	/// why §9 alerts on disk usage and why per-account quotas are not optional.
	/// </para>
	/// </summary>
	public string RootPath { get; set; } = string.Empty;
}

/// <summary>
/// <see cref="IBlobStore"/> over a directory (§9.1).
/// </summary>
/// <param name="options">Where the volume is.</param>
public sealed class FileSystemBlobStore(IOptions<BlobStoreOptions> options) : IBlobStore
{
	private readonly string _root = options.Value.RootPath;

	/// <inheritdoc />
	public async Task<string> PutAsync(Stream content, CancellationToken cancellationToken = default)
	{
		string blobRef = NewReference();
		string path = PathFor(blobRef);

		Directory.CreateDirectory(Path.GetDirectoryName(path)!);

		// Written beside the target and moved into place. A half-written blob that a reader
		// could open is worse than no blob at all: the track would load, be short, and look
		// like the ride ended early.
		string staging = $"{path}.{Guid.NewGuid():N}.tmp";

		try
		{
			await using (FileStream file = File.Create(staging))
			{
				await content.CopyToAsync(file, cancellationToken);
				await file.FlushAsync(cancellationToken);
			}

			File.Move(staging, path, overwrite: false);
		}
		catch
		{
			if (File.Exists(staging))
			{
				File.Delete(staging);
			}

			throw;
		}

		return blobRef;
	}

	/// <inheritdoc />
	public Task<Stream?> OpenAsync(string blobRef, CancellationToken cancellationToken = default)
	{
		_ = cancellationToken;

		string path = PathFor(blobRef);

		return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
	}

	/// <inheritdoc />
	public Task DeleteAsync(string blobRef, CancellationToken cancellationToken = default)
	{
		_ = cancellationToken;

		string path = PathFor(blobRef);

		if (File.Exists(path))
		{
			File.Delete(path);
		}

		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public Task<bool> ExistsAsync(string blobRef, CancellationToken cancellationToken = default)
	{
		_ = cancellationToken;

		return Task.FromResult(File.Exists(PathFor(blobRef)));
	}

	private static string NewReference() => Guid.NewGuid().ToString("N");

	/// <summary>
	/// A blob's path on disk, two levels of fan-out deep.
	/// <para>
	/// Flat would put every track and every photo in one directory, which most filesystems
	/// tolerate and every tool that has to list it does not — including the backup.
	/// </para>
	/// </summary>
	private string PathFor(string blobRef)
	{
		// The reference is generated here and never comes from a caller, but it does reach
		// this method from a database column, so it is checked rather than trusted: anything
		// that is not 32 hex characters cannot become a path at all.
		if (blobRef.Length != 32 || !blobRef.All(char.IsAsciiHexDigitLower))
		{
			throw new ArgumentException($"'{blobRef}' is not a blob reference.", nameof(blobRef));
		}

		return Path.Combine(_root, blobRef[..2], blobRef[2..4], blobRef);
	}
}
