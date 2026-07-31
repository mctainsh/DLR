using System.Text;

namespace DLR.Architecture.Tests.Rules;

/// <summary>
/// The repository's C# source files, for the rules that are about <em>where</em> code
/// lives rather than what it links against. Metadata cannot see a folder boundary.
/// </summary>
internal static class SourceTree
{
	/// <summary>The directory containing <c>DLR.sln</c>.</summary>
	public static string Root { get; } = FindRoot();

	/// <summary>
	/// The rulebook cannot scan itself. These files spell out the forbidden identifiers as
	/// string literals, so a text rule matches them every time; the identifiers are code
	/// nowhere else in here, and <see cref="CompiledAssembly"/> inspects this assembly's
	/// own metadata to cover it.
	/// </summary>
	private const string RulebookProject = "tests/DLR.Architecture.Tests/";

	/// <summary>
	/// Every hand-written <c>.cs</c> file under <c>src/</c> and <c>tests/</c>, with paths
	/// relative to <see cref="Root"/> and separators normalised to <c>/</c> so a rule reads
	/// the same on both platforms. Build output, generated files and the rulebook itself
	/// are excluded.
	/// </summary>
	public static IReadOnlyList<SourceFile> Files { get; } = Enumerate()
		.Where(file => !file.Path.StartsWith(RulebookProject, StringComparison.Ordinal))
		.ToList();

	/// <summary>Files whose repository-relative path starts with any of the given prefixes.</summary>
	public static IEnumerable<SourceFile> Under(params string[] prefixes) =>
		Files.Where(file => prefixes.Any(prefix => file.Path.StartsWith(prefix, StringComparison.Ordinal)));

	private static IEnumerable<SourceFile> Enumerate()
	{
		foreach (string top in new[] { "src", "tests" })
		{
			string directory = Path.Combine(Root, top);

			if (!Directory.Exists(directory))
			{
				continue;
			}

			foreach (string path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
			{
				string relative = Path.GetRelativePath(Root, path).Replace('\\', '/');

				bool generated =
					relative.Contains("/obj/", StringComparison.Ordinal) ||
					relative.Contains("/bin/", StringComparison.Ordinal) ||
					relative.EndsWith(".g.cs", StringComparison.Ordinal) ||
					relative.EndsWith(".Designer.cs", StringComparison.Ordinal);

				if (!generated)
				{
					yield return new SourceFile(relative, File.ReadAllText(path));
				}
			}
		}
	}

	private static string FindRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);

		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DLR.sln")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName
			?? throw new InvalidOperationException(
				$"Walked up from '{AppContext.BaseDirectory}' without finding DLR.sln, so the source rules " +
				"have nothing to read. This happens when the test assembly is run from outside the repository.");
	}
}

/// <summary>One source file: its repository-relative path and its text.</summary>
/// <param name="Path">Relative to the repository root, with <c>/</c> separators.</param>
/// <param name="Text">The whole file, comments included.</param>
internal sealed record SourceFile(string Path, string Text)
{
	private string[]? _codeLines;

	/// <summary>
	/// The file's lines with comments blanked out and line numbering preserved.
	/// <para>
	/// Comments have to go, or every rule fires on the paragraph explaining it — this
	/// file discusses <c>DateTime</c> and raw SQL at length and must not trip its own
	/// guards. Blanking rather than deleting keeps reported line numbers honest.
	/// </para>
	/// </summary>
	public string[] CodeLines => _codeLines ??= StripComments(Text).Split('\n');

	/// <summary>
	/// 1-based line numbers whose code — not commentary — contains any of
	/// <paramref name="needles"/>.
	/// </summary>
	public IReadOnlyList<int> LinesContaining(params string[] needles)
	{
		List<int> hits = [];

		for (int i = 0; i < CodeLines.Length; i++)
		{
			if (needles.Any(needle => CodeLines[i].Contains(needle, StringComparison.Ordinal)))
			{
				hits.Add(i + 1);
			}
		}

		return hits;
	}

	/// <summary>Renders a hit list as <c>path:line</c> for an assertion message.</summary>
	public IEnumerable<string> Cite(IEnumerable<int> lines) => lines.Select(line => $"{Path}:{line}");

	/// <summary>
	/// Replaces comment content with spaces, leaving newlines and code alone.
	/// The scanner tracks string and character literals so that a <c>//</c> inside a URL
	/// does not swallow the rest of the line — a false negative in a guard is worse than
	/// a noisy one.
	/// </summary>
	private static string StripComments(string text)
	{
		StringBuilder code = new(text.Length);

		bool inString = false;
		bool inChar = false;
		bool inBlockComment = false;

		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			char next = i + 1 < text.Length ? text[i + 1] : '\0';

			if (inBlockComment)
			{
				if (c == '*' && next == '/')
				{
					inBlockComment = false;
					code.Append("  ");
					i++;
					continue;
				}

				code.Append(c == '\n' ? '\n' : ' ');
				continue;
			}

			if (!inString && !inChar)
			{
				if (c == '/' && next == '/')
				{
					while (i < text.Length && text[i] != '\n')
					{
						i++;
					}

					code.Append('\n');
					continue;
				}

				if (c == '/' && next == '*')
				{
					inBlockComment = true;
					code.Append("  ");
					i++;
					continue;
				}
			}

			// An escaped quote does not close a literal. Verbatim and raw literals
			// double their quotes, which toggles evenly and lands in the right state.
			if (c == '\\' && (inString || inChar) && next != '\0')
			{
				code.Append(c).Append(next);
				i++;
				continue;
			}

			if (c == '"' && !inChar)
			{
				inString = !inString;
			}
			else if (c == '\'' && !inString)
			{
				inChar = !inChar;
			}
			else if (c == '\n')
			{
				// An unterminated literal is a compile error; resync rather than
				// letting one typo blind every rule for the rest of the file.
				inString = false;
				inChar = false;
			}

			code.Append(c);
		}

		return code.ToString();
	}
}
