using BlazorDLR.Shared.Services;

namespace DLR.UI.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="ITokenStore"/> for tests. Mirrors the shape of the mobile
/// SecureStorage-backed store - reading and writing a single "refresh" key - without
/// touching a Keychain.
/// </summary>
public sealed class FakeTokenStore : ITokenStore
{
	public string? StoredToken { get; private set; }
	public int WriteCount { get; private set; }
	public int ClearCount { get; private set; }

	public ValueTask<string?> ReadRefreshTokenAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(StoredToken);

	public ValueTask WriteRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
	{
		StoredToken = refreshToken;
		WriteCount++;
		return ValueTask.CompletedTask;
	}

	public ValueTask ClearAsync(CancellationToken cancellationToken = default)
	{
		StoredToken = null;
		ClearCount++;
		return ValueTask.CompletedTask;
	}
}
