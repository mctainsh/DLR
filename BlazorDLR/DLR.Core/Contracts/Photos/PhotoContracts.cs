namespace DLR.Core.Contracts.Photos;

/// <summary>
/// What an upload answers with (§16.4).
/// <para>
/// The dimensions come back because they are the <em>stored</em> ones - after the orientation was
/// applied and the long edge was capped - so a client can lay the image out before fetching it,
/// and can see that a portrait photograph stayed portrait.
/// </para>
/// </summary>
/// <param name="PhotoId">The identifier to attach to a marker or a comment.</param>
/// <param name="WidthPx">The stored image's width.</param>
/// <param name="HeightPx">The stored image's height.</param>
/// <param name="ByteSize">Bytes of the stored image, after re-encoding.</param>
public sealed record PhotoUploaded(Guid PhotoId, int WidthPx, int HeightPx, int ByteSize);

/// <summary>Attaching an uploaded image to a marker (§16.4).</summary>
/// <param name="PhotoId">The uploaded image, or null to detach the one that is there.</param>
public sealed record AttachPhotoRequest(Guid? PhotoId);
