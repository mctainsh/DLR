namespace DLR.Server;

/// <summary>
/// Settings without which the server cannot do its job, checked before it accepts a request.
/// <para>
/// The signing key has its own check with its own rules about <em>where</em> a value may come
/// from (§7.4). This is the plainer companion: a value that is simply absent, told at startup
/// rather than discovered as a stack trace on somebody's first request.
/// </para>
/// </summary>
public static class RequiredSettings
{
	/// <summary>Configuration key for the database.</summary>
	public const string ConnectionStringPath = "ConnectionStrings:Dlr";

	/// <summary>The same setting as an environment variable, with its double underscore.</summary>
	public const string ConnectionStringVariable = "ConnectionStrings__Dlr";

	/// <summary>Configuration key for the blob volume.</summary>
	public const string BlobRootPath = "Blobs:RootPath";

	/// <summary>The same setting as an environment variable, with its double underscore.</summary>
	public const string BlobRootVariable = "Blobs__RootPath";

	/// <summary>Refuses to start without a database to connect to.</summary>
	/// <param name="configuration">The application's configuration, as built.</param>
	/// <exception cref="InvalidOperationException">There is no connection string.</exception>
	public static void ValidateConnectionString(IConfiguration configuration)
	{
		if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString("Dlr")))
		{
			return;
		}

		// appsettings.json carries an empty placeholder, so the value is present and useless -
		// which without this check surfaces as an Npgsql failure on the first request that
		// touches a table, several layers below anything that names the cause.
		throw new InvalidOperationException(
			$"""
			No database: {ConnectionStringPath} is not set.

			Locally, use user secrets:

			    dotnet user-secrets set "{ConnectionStringPath}" "Host=localhost;Database=dlr;Username=dlr;Password=…" --project BlazorDLR/BlazorDLR.Web

			In production, an environment variable ({ConnectionStringVariable}) or a Docker
			secret.

			The placeholder in appsettings.json is deliberately empty (§14.3) - a connection
			string is a credential, and that file ships with the code.

			Running the tests needs none of this: they start their own PostgreSQL container.
			""".ReplaceLineEndings("\n"));
	}

	/// <summary>
	/// Refuses to start without an absolute path for the blob volume (§9.1).
	/// <para>
	/// <strong>Absolute, not merely present.</strong> <c>BlobStoreOptions.RootPath</c> defaults to
	/// the empty string, which <c>Path.Combine</c> turns into a relative path - so an unset value
	/// does not fail, it silently writes every photograph and every track into whatever the
	/// process happened to have as a working directory. Run from the project folder, that is the
	/// source tree: uploads land beside the code, git reports the tree dirty, and
	/// <c>SourceRevisionId</c> gains its <c>.dirty</c> suffix in a build users can see
	/// (§14.6.2). A relative path that <em>is</em> set is the same bug wearing a value, so it is
	/// refused too.
	/// </para>
	/// <para>
	/// Existence is deliberately not checked. <c>FileSystemBlobStore</c> creates each blob's
	/// directory on the way past, and a volume that is mounted a moment after the container
	/// starts is normal - what cannot be recovered from later is not knowing where to write.
	/// </para>
	/// </summary>
	/// <param name="configuration">The application's configuration, as built.</param>
	/// <exception cref="InvalidOperationException">The blob root is missing or relative.</exception>
	public static void ValidateBlobRoot(IConfiguration configuration)
	{
		string? root = configuration[BlobRootPath];

		if (!string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root))
		{
			return;
		}

		string problem = string.IsNullOrWhiteSpace(root)
			? $"No blob storage: {BlobRootPath} is not set."
			: $"Blob storage path '{root}' is relative; {BlobRootPath} must be absolute.";

		throw new InvalidOperationException(
			$"""
			{problem}

			This is where uploaded photos and track files are written. Unset, they go to the
			process working directory - which when run from the project folder is the source
			tree itself.

			Locally, use user secrets:

			    dotnet user-secrets set "{BlobRootPath}" "C:\dlr-blobs" --project BlazorDLR/BlazorDLR.Web

			In production, the mounted volume's path, via an environment variable
			({BlobRootVariable}) or compose.

			The tests need none of this: each one points this at its own temporary directory.
			""".ReplaceLineEndings("\n"));
	}
}
