namespace BlazorDLR.Shared.Services.Platform;

/// <summary>
/// The <see cref="ITokenStore"/> both browser hosts bind, because on the web the refresh
/// token lives in an <c>HttpOnly</c> cookie the JS heap cannot touch (§18.5).
/// <para>
/// Writing is deliberately silent — the cookie the server set is authoritative and this
/// method has nothing meaningful to persist. Reading returns <c>null</c> because there is
/// no readable value, not because storage failed.
/// </para>
/// </summary>
public sealed class CookieBackedTokenStore : ITokenStore
{
	/// <inheritdoc />
	public ValueTask<string?> ReadRefreshTokenAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromResult<string?>(null);

	/// <inheritdoc />
	public ValueTask WriteRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default) =>
		ValueTask.CompletedTask;

	/// <inheritdoc />
	public ValueTask ClearAsync(CancellationToken cancellationToken = default) =>
		ValueTask.CompletedTask;
}
