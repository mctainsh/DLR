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
	private const string TheOnlyIngestPath = "src/DLR.Server/Photos/";

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
	/// </summary>
	[Fact]
	public void OnlyTheServerLinksAnImagingLibrary()
	{
		List<string> offenders = CompiledAssembly.All
			.Where(assembly => assembly.Name != "DLR.Server")
			.Where(assembly => assembly.AssemblyReferences.Contains("SkiaSharp", StringComparer.Ordinal))
			.Select(assembly => assembly.Name)
			.ToList();

		offenders.ShouldBeEmpty(
			"DLR.Core is the shared contract and pure logic (§3) — an imaging library there would " +
			"put a decoder into every client that references it, including the WASM one, and " +
			"DLR.Server.Migrations describes a schema. Only the server ingests an image.");
	}
}
