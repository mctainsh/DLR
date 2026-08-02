namespace BlazorDLR.Shared.Services;

/// <summary>
/// Photo and file picking (§18.2, §16.4).
/// <para>
/// <strong>Mobile:</strong> <c>MediaPicker.PickPhotoAsync</c> and its take-a-photo counterpart.
/// <strong>Web:</strong> <c>&lt;InputFile&gt;</c> plugged into a callback. In both cases the
/// picked bytes travel to <see cref="IApiClient"/>'s photo endpoint, which is where the
/// hostile-input handling lives (§16.4).
/// </para>
/// </summary>
public interface IMediaPicker
{
	/// <summary>Whether this host can take a picture with the camera. False in the browser (v1, §18.2).</summary>
	bool CanCapture { get; }

	/// <summary>Prompt the rider to pick a photograph from their library.</summary>
	Task<PickedMedia?> PickPhotoAsync(CancellationToken cancellationToken = default);

	/// <summary>Prompt the rider to take a photograph now. Only defined when <see cref="CanCapture"/> is true.</summary>
	Task<PickedMedia?> CapturePhotoAsync(CancellationToken cancellationToken = default);
}

/// <summary>One picked photo, ready to be POSTed to the server (§16.4).</summary>
/// <param name="Content">The bytes as the user chose them. The server re-encodes and strips metadata.</param>
/// <param name="ContentType">What the caller thinks the bytes are — a hint only. The server sniffs.</param>
/// <param name="FileName">What the caller thinks the file is called — never trusted.</param>
public sealed record PickedMedia(
	Stream Content,
	string ContentType,
	string FileName);
