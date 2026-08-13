namespace BlazorDLR.Shared.Services.Platform;

/// <summary>
/// The <see cref="IMapPackServer"/> both browser hosts bind, and the one the SSR pass gets.
/// <para>
/// There is nothing to serve: <see cref="UnavailableMapPackStore"/> holds no archives on these
/// hosts (§18.6), so a listener would be a bound port answering 404 to itself. Answering
/// <c>null</c> is what sends <c>MapSourceState.Effective</c> back to an online source — a working
/// map under the routes and pins, which is what the screen is for.
/// </para>
/// </summary>
public sealed class UnavailableMapPackServer : IMapPackServer
{
	/// <inheritdoc />
	public bool IsSupported => false;

	/// <inheritdoc />
	public ValueTask<Uri?> ResolveAsync(string packId, CancellationToken cancellationToken = default) =>
		ValueTask.FromResult<Uri?>(null);
}
