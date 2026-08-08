using System.Text.RegularExpressions;
using DLR.Architecture.Tests.Rules;

namespace DLR.Architecture.Tests;

/// <summary>
/// The one way a scoped stylesheet can be completely dead and look completely fine.
/// <para>
/// Blazor's CSS isolation rewrites <c>.foo</c> in <c>Widget.razor.css</c> to
/// <c>.foo[b-abc123]</c>, and stamps <c>b-abc123</c> onto the plain HTML elements written in
/// <c>Widget.razor</c>. It does <strong>not</strong> stamp the markup a child *component*
/// renders. So <c>&lt;NavLink class="rail-item"&gt;</c> produces an <c>&lt;a class="rail-item"&gt;</c>
/// with no scope attribute, and every <c>.rail-item</c> rule in that stylesheet matches
/// nothing at all.
/// </para>
/// <para>
/// Nothing else catches it. It compiles, the class is right there in the markup, bUnit
/// asserts on markup and never evaluates CSS, and the page renders — just unstyled. The
/// nav rail shipped like that: the left rail looked correct by accident, because the
/// column's own <c>align-items: center</c> did what the missing rules would have done.
/// </para>
/// <para>
/// The fix is always <c>::deep</c> from an element the component does own, which is why a
/// selector guarded by <c>::deep</c> is exempt below.
/// </para>
/// </summary>
public sealed class ScopedCssRules
{
	[Fact]
	public void NoScopedStylesheet_TargetsAClassItOnlyPutsOnAChildComponent()
	{
		List<string> offences = [];

		foreach (string stylesheet in Directory.EnumerateFiles(
			SourceTree.Root, "*.razor.css", SearchOption.AllDirectories))
		{
			if (IsBuildOutput(stylesheet))
			{
				continue;
			}

			string companion = stylesheet[..^".css".Length];
			if (!File.Exists(companion))
			{
				continue;
			}

			string markup = File.ReadAllText(companion);

			HashSet<string> onComponents = ClassesOnTags(markup, componentTags: true);
			HashSet<string> onElements = ClassesOnTags(markup, componentTags: false);

			// Only a class the component never puts on an element of its own is unreachable.
			onComponents.ExceptWith(onElements);

			foreach (string unreachable in onComponents.Where(name => TargetedWithoutDeep(stylesheet, name)))
			{
				offences.Add(
					$"{Relative(stylesheet)} styles '.{unreachable}', but {Relative(companion)} only puts that "
					+ "class on a child component. The rendered element gets no scope attribute, so the rule "
					+ "never matches.");
			}
		}

		offences.ShouldBeEmpty(
			string.Join(
				Environment.NewLine,
				["A scoped rule that can never match is invisible: it compiles, the class is in the markup, "
					+ "and the page just renders unstyled.",
				 "Move the rule to the stylesheet of a component that owns a real element above it and reach "
					+ "down with ::deep — see `.rail ::deep .rail-item` in MainLayout.razor.css.",
				 .. offences]));
	}

	/// <summary>
	/// Class names appearing in <c>class="..."</c> on either component tags (capitalised, the
	/// Razor convention) or plain HTML elements (lower-case). Attribute order varies, so the
	/// tag is matched first and its attributes scanned second.
	/// </summary>
	private static HashSet<string> ClassesOnTags(string markup, bool componentTags)
	{
		string opener = componentTags ? "[A-Z]" : "[a-z]";

		HashSet<string> classes = new(StringComparer.Ordinal);

		foreach (Match tag in Regex.Matches(markup, $"<{opener}[^<>]*?>", RegexOptions.Singleline))
		{
			Match classAttribute = Regex.Match(tag.Value, @"class\s*=\s*""([^""]*)""");
			if (!classAttribute.Success)
			{
				continue;
			}

			foreach (string name in classAttribute.Groups[1].Value.Split(
				' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				// Razor expressions inside the attribute are conditional classes, not literals.
				if (!name.Contains('@', StringComparison.Ordinal))
				{
					classes.Add(name);
				}
			}
		}

		return classes;
	}

	/// <summary>
	/// Whether the stylesheet has a selector naming the class with no <c>::deep</c> ahead of
	/// it. Comments are stripped first so a class named only in prose does not count.
	/// </summary>
	private static bool TargetedWithoutDeep(string stylesheet, string className)
	{
		string css = Regex.Replace(File.ReadAllText(stylesheet), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

		foreach (Match selector in Regex.Matches(css, @"(?<selector>[^{}]+)\{", RegexOptions.Singleline))
		{
			string text = selector.Groups["selector"].Value;

			if (!Regex.IsMatch(text, $@"\.{Regex.Escape(className)}\b"))
			{
				continue;
			}

			if (!text.Contains("::deep", StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}

	private static bool IsBuildOutput(string path) =>
		path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
		|| path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

	private static string Relative(string path) =>
		Path.GetRelativePath(SourceTree.Root, path).Replace('\\', '/');
}
