using System.Runtime.CompilerServices;

namespace BlazorDLR.Shared.Services.Platform;

/// <summary>
/// The <see cref="ILocationProvider"/> for hosts with no continuous GPS the app can trust
/// (§18.6) — both browser hosts, and the MAUI host until its platform provider is wired.
/// <para>
/// This is not a placeholder for a browser implementation that is coming: the browser
/// geolocation API cannot deliver the background, high-cadence fixes a live ride needs, so
/// "not supported" is the honest answer rather than an interim one.
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
		AccuracyProfile profile,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		// Yield nothing rather than throw — a component that resolves this and immediately
		// awaits the first fix waits forever unless somebody wires the cancellation token,
		// which is the right posture: "no fixes will ever come" is the accurate answer.
		await Task.CompletedTask;
		yield break;
	}
}
