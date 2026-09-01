using BlazorDLR.Shared.Services;

namespace BlazorDLR.Web.Client.Services;

/// <summary>
/// The web's <see cref="IMediaPicker"/>. A browser has no "please pick a photo" API a
/// service can call directly - the picker is <c>&lt;InputFile&gt;</c>, a component that
/// must be triggered by a real user gesture on an element that is in the DOM. So this
/// implementation always answers "not picked"; pages that want file input on the web
/// render <c>&lt;InputFile&gt;</c> inline and handle the browser event themselves.
/// <para>
/// The GPX import page and the marker photo composer both do this today - one line of
/// per-host code that keeps the shared interface honest about what a browser can and
/// cannot do without a real click.
/// </para>
/// </summary>
public sealed class BrowserMediaPicker : IMediaPicker
{
	/// <inheritdoc />
	public bool CanCapture => false;

	/// <inheritdoc />
	public Task<PickedMedia?> PickPhotoAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult<PickedMedia?>(null);

	/// <inheritdoc />
	public Task<PickedMedia?> CapturePhotoAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult<PickedMedia?>(null);
}
