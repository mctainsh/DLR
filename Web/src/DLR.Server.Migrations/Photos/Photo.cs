using DLR.Server.Data.Identity;

namespace DLR.Server.Data.Photos;

/// <summary>
/// An image a rider attached to something (§16.4, §16.7).
/// <para>
/// <strong>Its content is metadata-free by construction, not by policy.</strong> Every byte in
/// <see cref="BlobRef"/> came out of a JPEG encoder that was handed a pixel buffer and nothing
/// else, so there is no tag to have missed. That matters here more than it would in most apps:
/// §15.6 lets a rider trim the first 400 m off a track so a ride does not start at their house,
/// and an EXIF GPS tag in a photograph taken in the driveway would put the house straight back —
/// in a file handed to every member of the ride.
/// </para>
/// <para>
/// A photo is its own resource rather than a column on a marker, because the photo is taken at the
/// top of a hill and the hill is where there is no signal (§16.4). The marker appears on everyone's
/// map immediately and the image arrives when the rider next has coverage.
/// </para>
/// </summary>
public sealed class Photo
{
	/// <summary>Row identifier — the <c>photoId</c> the upload returns.</summary>
	public Guid Id { get; set; }

	/// <summary>Who uploaded it. Their account going takes the row and the blobs with it (§16.6).</summary>
	public Guid OwnerId { get; set; }

	/// <summary>The stored image, downscaled to the long-edge cap.</summary>
	public string BlobRef { get; set; } = string.Empty;

	/// <summary>The callout thumbnail. A second object, so a map does not pull megabytes to draw a pin.</summary>
	public string ThumbBlobRef { get; set; } = string.Empty;

	/// <summary>The stored image's width, after orientation was applied.</summary>
	public int WidthPx { get; set; }

	/// <summary>The stored image's height, after orientation was applied.</summary>
	public int HeightPx { get; set; }

	/// <summary>Bytes of the stored image — what counts against the account's quota (§13 Q13).</summary>
	public int ByteSize { get; set; }

	/// <summary>
	/// SHA-256 of the <em>re-encoded</em> bytes, not of the upload.
	/// <para>
	/// Hashing what was uploaded would make the hash a function of the sender's encoder, so the
	/// same photograph sent twice from two devices would look like two different images. Hashing
	/// what was stored describes what is actually on the disk being backed up.
	/// </para>
	/// </summary>
	public byte[] ContentHash { get; set; } = [];

	/// <summary>When it was ingested.</summary>
	public DateTimeOffset CreatedUtc { get; set; }

	/// <summary>The uploader.</summary>
	public AppUser? Owner { get; set; }
}
