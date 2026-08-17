using DLR.Architecture.Tests.Rules;
using DLR.Core.Markers;

namespace DLR.Architecture.Tests;

/// <summary>
/// Every curated icon key has artwork, and every piece of artwork has a key (§16.2, §16.3).
/// <para>
/// The lookup from key to picture is string concatenation — <c>markers/{key}.png</c> — which is
/// what makes a newer client's unknown key resolvable at all. The cost of that simplicity is
/// that a typo or a missing file is not a compile error: <c>Known</c> would advertise a key the
/// composer offers, the rider would pick it, and the map would draw the plain-pin fallback
/// forever with nothing anywhere saying why. Metadata cannot see a <c>wwwroot</c> folder, so
/// this rule looks at the files.
/// </para>
/// </summary>
public sealed class MarkerIconAssetRules
{
	/// <summary>The shared RCL's icon folder, relative to the repository root.</summary>
	private const string IconFolder = "BlazorDLR.Shared/wwwroot/markers";

	/// <summary>Every <c>.png</c> basename actually present in that folder.</summary>
	private static IReadOnlySet<string> ArtworkOnDisk()
	{
		string folder = Path.Combine(SourceTree.Root, IconFolder.Replace('/', Path.DirectorySeparatorChar));

		Directory.Exists(folder).ShouldBeTrue($"{IconFolder} is where the marker artwork lives.");

		return Directory.EnumerateFiles(folder, "*.png")
			.Select(Path.GetFileNameWithoutExtension)
			.Where(name => !string.IsNullOrEmpty(name))
			.Select(name => name!)
			.ToHashSet(StringComparer.Ordinal);
	}

	[Fact]
	public void EveryKnownKey_HasArtwork()
	{
		IReadOnlySet<string> artwork = ArtworkOnDisk();

		List<string> missing = MarkerIcons.Known
			.Where(key => !artwork.Contains(key))
			.Order(StringComparer.Ordinal)
			.ToList();

		missing.ShouldBeEmpty(
			$"a key in MarkerIcons.Known with no {IconFolder}/{{key}}.png is offered in the composer " +
			"and then drawn as the plain-pin fallback for the rest of the adventure. Add the artwork in " +
			"the same change as the key, or leave the key out until the artwork exists.");
	}

	/// <summary>
	/// The other direction. Unreferenced artwork is not a correctness bug, but it ships in every
	/// app bundle and it is nearly always half of a change that stopped short — the PNG landed
	/// and the key never made it into <see cref="MarkerIcons.Known"/>, so nothing can select it.
	/// <para>
	/// Scoped to files whose name could be a key at all (<see cref="MarkerIcons.IsStorable"/>).
	/// A <c>Sample Images.png</c> dropped in the folder is not failed artwork, it is not artwork,
	/// and a rule that cannot tell the difference is one people learn to silence.
	/// </para>
	/// </summary>
	[Fact]
	public void EveryPieceOfArtwork_HasAKnownKey()
	{
		List<string> orphans = ArtworkOnDisk()
			.Where(MarkerIcons.IsStorable)
			.Where(name => !MarkerIcons.IsKnown(name))
			.Order(StringComparer.Ordinal)
			.ToList();

		orphans.ShouldBeEmpty(
			$"artwork in {IconFolder} with no matching key in MarkerIcons.Known cannot be chosen " +
			"by anybody — it is dead weight in the bundle. Either register the key or delete the file.");
	}
}
