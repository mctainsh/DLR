namespace DLR.Server.Photos;

/// <summary>The §16.4 caps and sizes, as settings rather than constants (§14.5).</summary>
public sealed class PhotoOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "Photos";

	/// <summary>
	/// Request-level cap; anything larger is a <c>413</c>. Bounds the transfer, and bounds it
	/// alone - a small file can still declare an enormous canvas, which is what
	/// <see cref="MaxDecodedPixels"/> is for.
	/// </summary>
	public long MaxUploadBytes { get; set; } = 12 * 1024 * 1024;

	/// <summary>
	/// The decompression-bomb cap, checked against the header's declared dimensions
	/// <strong>before anything is allocated</strong> (§16.4).
	/// <para>
	/// A 40 KB PNG can expand to hundreds of megabytes of bitmap, and a byte cap does not see it
	/// coming. 40 MP is comfortably above any phone camera and far below anything that would hurt.
	/// </para>
	/// </summary>
	public long MaxDecodedPixels { get; set; } = 40_000_000;

	/// <summary>The long edge of the stored image. Anything larger is downscaled.</summary>
	public int MaxDimension { get; set; } = 2048;

	/// <summary>The long edge of the thumbnail drawn in a map callout.</summary>
	public int ThumbDimension { get; set; } = 320;

	/// <summary>
	/// JPEG quality for both outputs. High enough that a re-encode of an already-conformant
	/// photograph is not visibly worse, which matters because re-encoding is unconditional.
	/// </summary>
	public int Quality { get; set; } = 85;

	/// <summary>Uploads per hour per user (§16.5).</summary>
	public int UploadsPerHourPerUser { get; set; } = 30;

	/// <summary>Uploads per day per user (§16.5).</summary>
	public int UploadsPerDayPerUser { get; set; } = 200;
}
