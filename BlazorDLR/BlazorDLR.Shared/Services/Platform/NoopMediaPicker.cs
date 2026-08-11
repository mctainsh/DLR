namespace BlazorDLR.Shared.Services.Platform;

/// <summary>
/// The <see cref="IMediaPicker"/> for hosts that cannot pick or capture — the SSR pass,
/// which has no user to present a picker to.
/// <para>
/// Returns <c>null</c> rather than throwing: <c>null</c> is already the contract's "the
/// user picked nothing", so a caller needs no separate branch for a host without a picker.
/// The real mobile picker is MAUI's <c>MediaPicker</c>; the web picker is
/// <c>&lt;InputFile&gt;</c> plumbed through a callback.
/// </para>
/// </summary>
public sealed class NoopMediaPicker : IMediaPicker
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
