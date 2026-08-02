using Microsoft.Extensions.Options;
using SkiaSharp;

namespace DLR.Server.Photos;

/// <summary>Why an upload was refused, or <see cref="None"/> (§16.4).</summary>
public enum PhotoProblem
{
	/// <summary>It was accepted.</summary>
	None = 0,

	/// <summary>
	/// Nothing here is an image in any format we accept. Decided by sniffing the content, never
	/// by the extension or the client's <c>Content-Type</c>.
	/// </summary>
	NotAnImage,

	/// <summary>
	/// The header declares more pixels than <see cref="PhotoOptions.MaxDecodedPixels"/> allows —
	/// a decompression bomb. Refused before any bitmap exists.
	/// </summary>
	TooManyPixels,

	/// <summary>
	/// The header parsed and the image data did not. A truncated or corrupt file rather than a
	/// hostile one, and the caller gets told which.
	/// </summary>
	DecodeFailed,
}

/// <summary>
/// A re-encoded, metadata-free image and its thumbnail.
/// </summary>
/// <param name="Full">The stored image, downscaled to the long-edge cap.</param>
/// <param name="Thumbnail">The callout image.</param>
/// <param name="WidthPx">The stored image's width, <em>after</em> orientation is applied.</param>
/// <param name="HeightPx">The stored image's height, after orientation is applied.</param>
public sealed record IngestedImage(byte[] Full, byte[] Thumbnail, int WidthPx, int HeightPx);

/// <summary>An accepted image, or the reason it was not (§16.4).</summary>
/// <param name="Problem">The refusal, or <see cref="PhotoProblem.None"/>.</param>
/// <param name="Image">The result, when there was no problem.</param>
public sealed record IngestOutcome(PhotoProblem Problem, IngestedImage? Image)
{
	/// <summary>Whether an image came out.</summary>
	public bool Accepted => Problem is PhotoProblem.None;
}

/// <summary>
/// <strong>The only place in this server that decodes an image (§16.4).</strong>
/// <para>
/// One ingest path is what makes metadata stripping non-optional rather than well-intentioned. A
/// second decoder anywhere — a thumbnailer, an avatar endpoint, a "just check it is valid" helper —
/// is a path that has to re-implement all of this and will not, so
/// <c>ImageDecodingHappensInOnePlaceOnly</c> is a build failure to add one.
/// </para>
/// <para>
/// The order of operations here is the whole feature. Sniff, then check the declared size, then
/// decode, then apply the orientation, then downscale, then re-encode <em>writing no metadata</em>.
/// Getting the last two the wrong way round leaves every portrait photo sideways; getting the
/// second one wrong hands a 40 KB file hundreds of megabytes of address space.
/// </para>
/// </summary>
/// <param name="options">The §16.4 caps and sizes.</param>
public sealed class ImageIngest(IOptions<PhotoOptions> options)
{
	/// <summary>
	/// Formats accepted, by content (§16.4). Anything else — an SVG, a TIFF, an ICO — is refused
	/// rather than handed to a decoder we have no reason to trust with a stranger's bytes.
	/// </summary>
	private static readonly SKEncodedImageFormat[] Accepted =
	[
		SKEncodedImageFormat.Jpeg,
		SKEncodedImageFormat.Png,
		SKEncodedImageFormat.Heif,
		SKEncodedImageFormat.Webp,
	];

	private readonly PhotoOptions _options = options.Value;

	/// <summary>
	/// Turns whatever a caller sent into a stored image and a thumbnail, or says why not.
	/// </summary>
	/// <param name="content">The uploaded bytes, already inside the byte cap.</param>
	public IngestOutcome Read(byte[] content)
	{
		using MemoryStream stream = new(content, writable: false);

		// SKCodec parses the header and nothing else, which is what makes the size check below
		// able to run before a bitmap exists. Null means no decoder recognised the bytes.
		using SKCodec? codec = SKCodec.Create(stream);

		if (codec is null || !Accepted.Contains(codec.EncodedFormat))
		{
			return new IngestOutcome(PhotoProblem.NotAnImage, null);
		}

		SKImageInfo declared = codec.Info;

		// long, not int: 60000 x 60000 is a perfectly writable PNG header and overflows an int
		// multiply into a small positive number, which would pass a cap written the obvious way.
		long pixels = (long)declared.Width * declared.Height;

		if (declared.Width <= 0 || declared.Height <= 0 || pixels > _options.MaxDecodedPixels)
		{
			return new IngestOutcome(PhotoProblem.TooManyPixels, null);
		}

		using SKBitmap? decoded = SKBitmap.Decode(codec);

		if (decoded is null)
		{
			return new IngestOutcome(PhotoProblem.DecodeFailed, null);
		}

		// Applied here, and then thrown away with the rest of the metadata. Doing it in the other
		// order — or not at all — is why photographs arrive sideways from iPhones.
		using SKBitmap upright = Upright(decoded, codec.EncodedOrigin);

		using SKBitmap full = Downscale(upright, _options.MaxDimension);
		using SKBitmap thumbnail = Downscale(upright, _options.ThumbDimension);

		return new IngestOutcome(
			PhotoProblem.None,
			new IngestedImage(Encode(full), Encode(thumbnail), full.Width, full.Height));
	}

	/// <summary>
	/// Re-encodes to JPEG. <strong>This is the metadata strip</strong> (§16.4).
	/// <para>
	/// Re-encoding rather than running a stripping pass is deliberate: a stripper removes the tags
	/// it knows about and says nothing about the ones it does not, so its failure mode is silent
	/// and its correctness expires as formats gain new segments. An encoder writing a fresh file
	/// from a pixel buffer cannot carry a tag it was never given.
	/// </para>
	/// </summary>
	private byte[] Encode(SKBitmap bitmap)
	{
		using SKImage image = SKImage.FromBitmap(bitmap);
		using SKData encoded = image.Encode(SKEncodedImageFormat.Jpeg, _options.Quality);

		return encoded.ToArray();
	}

	/// <summary>
	/// Rotates and flips stored pixels into the orientation they are meant to be seen in.
	/// </summary>
	private static SKBitmap Upright(SKBitmap source, SKEncodedOrigin origin)
	{
		if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
		{
			return Plain(source);
		}

		// The four origins whose 0th row is a *column* of the displayed image swap the axes, so
		// the target is not the same shape as the source.
		bool swapsAxes = origin
			is SKEncodedOrigin.LeftTop
			or SKEncodedOrigin.RightTop
			or SKEncodedOrigin.RightBottom
			or SKEncodedOrigin.LeftBottom;

		SKBitmap target = new(
			swapsAxes ? source.Height : source.Width,
			swapsAxes ? source.Width : source.Height,
			source.ColorType,
			source.AlphaType);

		using (SKCanvas canvas = new(target))
		{
			switch (origin)
			{
				case SKEncodedOrigin.TopRight:
					canvas.Translate(target.Width, 0);
					canvas.Scale(-1, 1);
					break;

				case SKEncodedOrigin.BottomRight:
					canvas.Translate(target.Width, target.Height);
					canvas.Scale(-1, -1);
					break;

				case SKEncodedOrigin.BottomLeft:
					canvas.Translate(0, target.Height);
					canvas.Scale(1, -1);
					break;

				case SKEncodedOrigin.LeftTop:
					canvas.RotateDegrees(90);
					canvas.Translate(0, -target.Width);
					canvas.Scale(-1, 1);
					canvas.Translate(-target.Height, 0);
					break;

				case SKEncodedOrigin.RightTop:
					// The common one: every phone held in portrait. The stored top-left belongs
					// at the top-right, which is a 90° clockwise turn.
					canvas.Translate(target.Width, 0);
					canvas.RotateDegrees(90);
					break;

				case SKEncodedOrigin.RightBottom:
					canvas.Translate(target.Width, target.Height);
					canvas.RotateDegrees(90);
					canvas.Scale(1, -1);
					canvas.Translate(-target.Height, 0);
					break;

				case SKEncodedOrigin.LeftBottom:
					canvas.Translate(0, target.Height);
					canvas.RotateDegrees(270);
					break;

				default:
					break;
			}

			canvas.DrawBitmap(source, new SKPoint(0, 0), SKSamplingOptions.Default);
		}

		return target;
	}

	/// <summary>
	/// Fits the long edge inside <paramref name="longEdge"/>, preserving the aspect ratio. An
	/// image already smaller is copied rather than enlarged — upscaling a photograph invents
	/// detail and costs storage to do it.
	/// </summary>
	private static SKBitmap Downscale(SKBitmap source, int longEdge)
	{
		int longest = Math.Max(source.Width, source.Height);

		if (longest <= longEdge)
		{
			return Plain(source);
		}

		double scale = (double)longEdge / longest;

		SKImageInfo target = new(
			Math.Max(1, (int)Math.Round(source.Width * scale)),
			Math.Max(1, (int)Math.Round(source.Height * scale)),
			source.ColorType,
			source.AlphaType);

		return source.Resize(target, new SKSamplingOptions(SKCubicResampler.Mitchell))
			?? throw new InvalidOperationException(
				$"Could not resize a {source.Width}x{source.Height} image to {target.Width}x{target.Height}.");
	}

	/// <summary>
	/// Copies pixels into a bitmap carrying <strong>no colour profile</strong>.
	/// <para>
	/// <c>SKBitmap.Copy</c> would be the obvious call and is the wrong one: it preserves the
	/// decoded image's <c>SKColorSpace</c>, and the JPEG encoder then writes that out as an ICC
	/// profile in an <c>APP2</c> segment. An ICC profile is metadata — it can name the device that
	/// produced it — so a file carrying one is not the metadata-free file §16.4 promises.
	/// </para>
	/// <para>
	/// It is worth knowing <em>which</em> images took that path: the ones needing neither rotation
	/// nor downscaling, because every other route already builds its target from an
	/// <see cref="SKImageInfo"/> with no colour space. That is the small, upright photograph — the
	/// ordinary case, and the one a spot check is least likely to look at.
	/// </para>
	/// </summary>
	private static SKBitmap Plain(SKBitmap source)
	{
		SKBitmap target = new(new SKImageInfo(
			source.Width,
			source.Height,
			source.ColorType,
			source.AlphaType));

		using (SKCanvas canvas = new(target))
		{
			canvas.DrawBitmap(source, new SKPoint(0, 0), SKSamplingOptions.Default);
		}

		return target;
	}
}
