namespace BlazorDLR.Shared.Services.Platform;

/// <summary>
/// The <see cref="IOfflineStore"/> both browser hosts bind, and the one the SSR pass gets.
/// <para>
/// §18.6 keeps offline-first a mobile property: the phone has a file system, a background
/// receiver and a rider in a dead zone, and the web app is the big-screen surface for planning
/// and reading. Rather than have shared code ask which host it is in, this answers every read
/// with "you have no copy" - which is the truthful answer for a browser - and drops every
/// write.
/// </para>
/// <para>
/// <strong>Dropping the write silently is the point.</strong> The alternative is a throwing
/// stub, which would make every caller wrap a cache write in a try/catch to keep working on a
/// host that was never going to store it. <see cref="IsSupported"/> is there for the one caller
/// that genuinely needs to know the difference: a screen deciding whether to offer "you are
/// looking at your last copy" as an explanation.
/// </para>
/// </summary>
public sealed class UnavailableOfflineStore : IOfflineStore
{
	/// <inheritdoc />
	public bool IsSupported => false;

	/// <inheritdoc />
	public ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken = default) =>
		ValueTask.FromResult<string?>(null);

	/// <inheritdoc />
	public ValueTask WriteAsync(string name, string content, CancellationToken cancellationToken = default) =>
		ValueTask.CompletedTask;

	/// <inheritdoc />
	public ValueTask RemoveAsync(string name, CancellationToken cancellationToken = default) =>
		ValueTask.CompletedTask;
}
