using System.Text;
using Microsoft.Extensions.FileProviders;

namespace DLR.Server.Identity;

/// <summary>
/// Refuses to start if the signing key came from a file that ships with the code (§7.4).
/// <para>
/// "Never <c>appsettings.json</c>" is the kind of rule that holds until the afternoon somebody
/// needs the app to run and puts the key where the other settings are. It survives a deadline
/// only as a startup failure, and it has to be a startup failure rather than a review comment
/// because the consequence — a signing key in git history — is permanent in practice: the fix
/// is rotation, not deletion.
/// </para>
/// <para>
/// Environment variables and Docker secrets pass. User secrets pass too, because that file
/// lives in the developer's profile rather than in the repository, which is the whole
/// distinction being drawn.
/// </para>
/// </summary>
public static class SigningKeySource
{
	/// <summary>Configuration path of the key this guards.</summary>
	public const string KeyPath = $"{JwtOptions.Section}:{nameof(JwtOptions.SigningKey)}";

	/// <summary>
	/// The same setting as an environment variable. The separator is a <em>double</em>
	/// underscore — a single one is a different key and binds to nothing, silently.
	/// </summary>
	public const string EnvironmentVariableName =
		$"{JwtOptions.Section}__{nameof(JwtOptions.SigningKey)}";

	/// <summary>Checks the key's length and where it came from.</summary>
	/// <param name="configuration">The application's configuration, as built.</param>
	/// <param name="contentRootPath">Files under here are files that ship with the code.</param>
	/// <exception cref="InvalidOperationException">The key is missing, short, or committed.</exception>
	public static void Validate(IConfiguration configuration, string contentRootPath)
	{
		string? key = configuration[KeyPath];

		if (string.IsNullOrWhiteSpace(key))
		{
			// The rule is "not a file that ships with the code", and the message has to say
			// what to do rather than only what is forbidden. User secrets satisfy it — that
			// file lives in the developer's profile, not the repository — and saying so here
			// is the difference between a one-line fix and a hunt through the design document.
			throw new InvalidOperationException(
				$"""
				No signing key: {KeyPath} is not set, and the server cannot issue an access
				token without one.

				Locally, use user secrets — they live in your profile rather than in the
				repository, so they satisfy §7.4:

				    dotnet user-secrets set "{KeyPath}" "<at least {JwtOptions.MinimumSigningKeyBytes} characters>" --project Web/src/DLR.Server

				In production, an environment variable ({EnvironmentVariableName}) or a Docker
				secret.

				Never appsettings.json. That file ships with the code, and a key that reaches
				git history is fixed by rotating it, not by deleting the line.
				""".ReplaceLineEndings("\n"));
		}

		if (Encoding.UTF8.GetByteCount(key) < JwtOptions.MinimumSigningKeyBytes)
		{
			throw new InvalidOperationException(
				$"{KeyPath} is shorter than {JwtOptions.MinimumSigningKeyBytes} bytes. " +
				"HS256 is only as strong as its key.");
		}

		string? file = CommittedFileSupplying(configuration, KeyPath, contentRootPath);

		if (file is not null)
		{
			throw new InvalidOperationException(
				$"{KeyPath} is set in '{file}', which is inside the content root and so ships " +
				"with the code (§7.4). Move it to an environment variable or a Docker secret. " +
				"If it has already been committed, the fix is to rotate the key, not to delete " +
				"the line.");
		}
	}

	/// <summary>
	/// The path of the file supplying <paramref name="path"/>, if that file ships with the
	/// application. Null when the value came from anywhere else.
	/// </summary>
	private static string? CommittedFileSupplying(
		IConfiguration configuration,
		string path,
		string contentRootPath)
	{
		if (configuration is not IConfigurationRoot root)
		{
			return null;
		}

		// Later providers win, so the one that actually supplied the value is the last one
		// holding it. Reversing and taking the first match is the same thing said forwards.
		foreach (IConfigurationProvider provider in root.Providers.Reverse())
		{
			if (!provider.TryGet(path, out _))
			{
				continue;
			}

			return provider is FileConfigurationProvider source
				? PathIfInside(source, contentRootPath)
				: null;
		}

		return null;
	}

	private static string? PathIfInside(FileConfigurationProvider provider, string contentRootPath)
	{
		if (provider.Source.Path is not { } relative
			|| provider.Source.FileProvider is not PhysicalFileProvider files)
		{
			return null;
		}

		string full = Path.GetFullPath(Path.Combine(files.Root, relative));
		string root = Path.GetFullPath(contentRootPath);

		return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
	}
}
