using System.Globalization;
using System.Reflection;

namespace DLR.Server.Api;

/// <summary>
/// What the running build is, read out of its own assembly metadata (§14.6.2).
/// <para>
/// Every value here is embedded by the compiler: <c>InformationalVersion</c> carries the
/// commit because SourceLink puts <c>SourceRevisionId</c> there, and <c>BuildUtc</c> is
/// written by <c>Directory.Build.targets</c>. Nothing in this file is maintained by hand,
/// which is the point — a hand-maintained source pointer is wrong within a week, and a
/// wrong source pointer is worse than none.
/// </para>
/// </summary>
public sealed class BuildInformation
{
	/// <summary>Reported when the build carried no source control information at all.</summary>
	public const string UnknownCommit = "unknown";

	private BuildInformation(string version, string commit, bool isDirty, DateTimeOffset? builtUtc)
	{
		Version = version;
		Commit = commit;
		IsDirty = isDirty;
		BuiltUtc = builtUtc;
	}

	/// <summary>The full informational version, for example <c>0.1.0+9f2c1ab…</c>.</summary>
	public string Version { get; }

	/// <summary>
	/// The commit this build came from, with a <c>+dirty</c> marker when it did not come
	/// from a clean tree.
	/// </summary>
	public string Commit { get; }

	/// <summary>Whether the working tree had uncommitted changes when this was built.</summary>
	public bool IsDirty { get; }

	/// <summary>When the assembly was compiled, if the build recorded it.</summary>
	public DateTimeOffset? BuiltUtc { get; }

	/// <summary>Reads the metadata of the assembly the server is running from.</summary>
	public static BuildInformation ForAssembly(Assembly assembly)
	{
		string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? assembly.GetName().Version?.ToString()
			?? "0.0.0";

		(string commit, bool dirty) = ParseCommit(version);

		return new BuildInformation(version, commit, dirty, ReadBuildTimestamp(assembly));
	}

	/// <summary>
	/// Splits <c>1.4.0+9f2c1ab.dirty</c> into its commit and its cleanliness. Semantic
	/// version build metadata is a dot-separated list after the <c>+</c>; SourceLink puts
	/// the revision first and the dirty build appends to it.
	/// </summary>
	private static (string Commit, bool Dirty) ParseCommit(string informationalVersion)
	{
		int plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);

		if (plus < 0 || plus == informationalVersion.Length - 1)
		{
			// No source control information — a build from a tarball, or from a tree that
			// is not a git repository at all. Say so rather than inventing a commit.
			return (UnknownCommit, false);
		}

		string[] metadata = informationalVersion[(plus + 1)..].Split('.');
		bool dirty = metadata.Contains("dirty", StringComparer.Ordinal);
		string revision = metadata[0];

		return (dirty ? $"{revision}+dirty" : revision, dirty);
	}

	private static DateTimeOffset? ReadBuildTimestamp(Assembly assembly)
	{
		string? stamp = assembly
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(attribute => attribute.Key == "BuildUtc")
			?.Value;

		return DateTimeOffset.TryParse(
			stamp,
			CultureInfo.InvariantCulture,
			DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
			out DateTimeOffset parsed)
			? parsed
			: null;
	}
}
