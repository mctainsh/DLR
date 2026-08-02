using DLR.Architecture.Tests.Rules;

namespace DLR.Architecture.Tests;

/// <summary>
/// Image decoding happens in <c>DLR.Server/Photos/</c> and nowhere else (§10.4, §16.4).
/// <para>
/// This is the guard that makes metadata stripping structural rather than careful. §15.6 lets a
/// rider trim the first 400 m off a track so a ride does not start at their house; an EXIF GPS tag
/// in a photograph taken in the driveway puts the house back, in a file handed to every member of
/// the ride. One ingest path is what stops a second one — a thumbnailer, an avatar endpoint, a
/// helper that "just checks the file is valid" — from being a way around it, because the second
/// path would have to re-implement all of this and would not.
/// </para>
/// </summary>
public sealed class ImageRules
{
	/// <summary>The one folder allowed to touch a decoder.</summary>
	private const string TheOnlyIngestPath = "src/BlazorDLR.Web/Photos/";

	/// <summary>
	/// Identifiers that decode, encode or resample an image. Named rather than matched on the
	/// <c>SkiaSharp</c> namespace alone, so a rule still fires on a <c>using</c>-aliased type
	/// or on a second imaging library arriving under a different name.
	/// </summary>
	private static readonly string[] DecoderEntryPoints =
	[
		"SkiaSharp",
		"SKCodec",
		"SKBitmap",
		"SKImage",
		"SKData",
		"ImageSharp",
		"System.Drawing",
	];

	/// <summary>
	/// The rule governs production code. A test has to decode the stored image to assert that a
	/// portrait photograph came out portrait, so constraining <c>tests/</c> would make the
	/// guarantee unassertable — §10.4 names a folder under <c>src/</c>, which is what ships.
	/// </summary>
	[Fact]
	public void ImageDecodingHappensInOnePlaceOnly()
	{
		List<string> offenders = SourceTree.Under("src/")
			.Where(file => !file.Path.StartsWith(TheOnlyIngestPath, StringComparison.Ordinal))
			.SelectMany(file => file.Cite(file.LinesContaining(DecoderEntryPoints)))
			.ToList();

		offenders.ShouldBeEmpty(
			$"Images are decoded in {TheOnlyIngestPath} and nowhere else. Every byte that reaches " +
			"a decoder is a byte a stranger chose, and every image that leaves this server was " +
			"re-encoded from a pixel buffer so it cannot carry a tag. A second decode path is a " +
			"second set of both promises. If new code needs an image, call ImageIngest — do not " +
			"widen the rule.");
	}

	/// <summary>
	/// Checked against metadata as well, because the source rule can only see the code that is
	/// there today. This one fails the moment somebody adds the package reference, before any
	/// call site exists for the text rule to find.
	/// <para>
	/// <strong>Two assemblies are allowed to link SkiaSharp</strong>, for two different reasons:
	/// <see cref="DLR.Server"/> is the one image <em>ingest</em> path (§16.4) — decoder input is
	/// hostile and metadata stripping must not be bypassable. <see cref="BlazorDLR.Shared"/> is
	/// the shared UI's <em>drawing</em> path (§4.5 v0.21) — one Skia canvas draws every map
	/// overlay on every host, which closes the "two map code paths drift on marker rendering"
	/// class of bug v0.13 warned about. Draw ≠ ingest, so the rule permits both explicitly.
	/// </para>
	/// </summary>
	[Fact]
	public void OnlyTheServerAndSharedUiLinkAnImagingLibrary()
	{
		string[] permittedAssemblies = ["DLR.Server", "BlazorDLR.Shared"];

		List<string> offenders = CompiledAssembly.All
			.Where(assembly => !permittedAssemblies.Contains(assembly.Name, StringComparer.Ordinal))
			.Where(assembly => assembly.AssemblyReferences.Contains("SkiaSharp", StringComparer.Ordinal))
			.Select(assembly => assembly.Name)
			.ToList();

		offenders.ShouldBeEmpty(
			"DLR.Server ingests images (§16.4); BlazorDLR.Shared draws them for the map overlay " +
			"(§4.5 v0.21). Any other assembly linking SkiaSharp is a third path — either a second " +
			"decoder, which defeats §16.4's guarantee that photos are stripped by re-encoding on the " +
			"one ingest path, or a second draw surface, which defeats §4.5 v0.21's guarantee that " +
			"map overlays are drawn once and look the same on every host.");
	}
}
