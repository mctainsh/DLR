using BlazorDLR.Shared.Services;

namespace BlazorDLR.Services;

/// <summary>
/// The phone's <see cref="Shared.Services.IMediaPicker"/> — MAUI's <see cref="MediaPicker"/> for photos
/// and <see cref="FilePicker"/> for GPX (§16.4, §15.2).
/// <para>
/// The share sheet / open-with registration for <c>.gpx</c> is declared in the manifest
/// files (§14.1); this class only implements the "user tapped 'import'" case. Both flows
/// end at the same place — a byte stream, a content type and a file name the caller
/// hands to <see cref="IApiClient"/>.
/// </para>
/// </summary>
public sealed class MauiMediaPicker : Shared.Services.IMediaPicker
{
	/// <inheritdoc />
	public bool CanCapture => MediaPicker.Default.IsCaptureSupported;

	/// <inheritdoc />
	public async Task<PickedMedia?> PickPhotoAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			// PickPhotoAsync is obsolete; PickPhotosAsync is the supported call and returns an
			// empty list when the user cancels. SelectionLimit isn't honoured on every platform,
			// so take the first result rather than trusting the picker to enforce "one".
			IEnumerable<FileResult> results = await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions
			{
				SelectionLimit = 1,
			});

			FileResult? result = results?.FirstOrDefault();
			if (result is null)
			{
				return null;
			}

			Stream stream = await result.OpenReadAsync();
			return new PickedMedia(stream, result.ContentType ?? "application/octet-stream", result.FileName);
		}
		catch (FeatureNotSupportedException)
		{
			// Some MAUI targets (Windows desktop) advertise no picker; the honest answer is
			// "no pick" so the caller renders whatever fallback it has, rather than a crash.
			return null;
		}
		catch (PermissionException)
		{
			// The user declined the "photos" permission. Same shape as a cancelled pick —
			// the caller decides whether to prompt again.
			return null;
		}
	}

	/// <inheritdoc />
	public async Task<PickedMedia?> CapturePhotoAsync(CancellationToken cancellationToken = default)
	{
		if (!CanCapture)
		{
			return null;
		}

		try
		{
			FileResult? result = await MediaPicker.Default.CapturePhotoAsync();
			if (result is null)
			{
				return null;
			}

			Stream stream = await result.OpenReadAsync();
			return new PickedMedia(stream, result.ContentType ?? "image/jpeg", result.FileName);
		}
		catch (FeatureNotSupportedException)
		{
			return null;
		}
		catch (PermissionException)
		{
			return null;
		}
	}

	/// <summary>
	/// Pick a GPX file specifically (§15.2). Used by the GPX import page.
	/// <para>
	/// Not on the shared interface because the browser side of the shared code renders
	/// its own <c>&lt;InputFile&gt;</c> — trying to force a common API here would give a
	/// method that lies on the web ("returns null unless the user is watching a modal
	/// somewhere") or that lies on the phone ("pretends to be an <c>InputFile</c>"). The
	/// GPX page picks its own file per host and hands the stream to <c>IApiClient</c>.
	/// </para>
	/// </summary>
	public static async Task<PickedMedia?> PickGpxAsync(CancellationToken cancellationToken = default)
	{
		FilePickerFileType gpxFileType = new(new Dictionary<DevicePlatform, IEnumerable<string>>
		{
			[DevicePlatform.iOS] = new[] { "public.xml", "com.topografix.gpx" },
			[DevicePlatform.Android] = new[] { "application/gpx+xml", "application/xml", "text/xml" },
			[DevicePlatform.WinUI] = new[] { ".gpx" },
			[DevicePlatform.MacCatalyst] = new[] { "gpx", "xml" },
		});

		try
		{
			FileResult? result = await FilePicker.Default.PickAsync(new PickOptions
			{
				FileTypes = gpxFileType,
				PickerTitle = "Choose a GPX file",
			});
			if (result is null)
			{
				return null;
			}

			Stream stream = await result.OpenReadAsync();
			return new PickedMedia(stream, "application/gpx+xml", result.FileName);
		}
		catch (Exception)
		{
			return null;
		}
	}
}
