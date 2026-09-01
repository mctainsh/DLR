using System.Runtime.CompilerServices;

namespace BlazorDLR.Shared.Services.Platform;

/// <summary>
/// The <see cref="ILocationProvider"/> for a MAUI target with no receiver behind it - the Windows
/// and macOS heads, which are development surfaces rather than riding ones (§18.6).
/// <para>
/// <strong>Not the browser's answer any more.</strong> The web hosts used to bind this so the
/// shared screens could <c>@inject</c> a broadcaster unconditionally, which cost five inert
/// registrations and a settings screen full of controls that could not move anything on the
/// machine reading them. They now register no GPS seam at all and the screens resolve the
/// broadcaster with <c>GetService</c> - absent is the state they render. This survives because
/// the MAUI head still needs something to bind on a target where <c>#if ANDROID</c> and
/// <c>#if IOS</c> are both false; there, "no receiver" and "not a MAUI host" are different
/// facts, and only this one can be expressed as an implementation.
/// </para>
/// </summary>
public sealed class NoopLocationProvider : ILocationProvider
{
	/// <inheritdoc />
	public bool IsSupported => false;

	/// <inheritdoc />
	public bool IsRecording => false;

	/// <inheritdoc />
	public Task<LocationPermissionState> EnsurePermissionsAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult(LocationPermissionState.NotSupported);

	/// <inheritdoc />
	public async IAsyncEnumerable<LocationFix> WatchAsync(
		LocationUpdateRate rate,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		// Yield nothing rather than throw - a component that resolves this and immediately
		// awaits the first fix waits forever unless somebody wires the cancellation token,
		// which is the right posture: "no fixes will ever come" is the accurate answer.
		await Task.CompletedTask;
		yield break;
	}
}
