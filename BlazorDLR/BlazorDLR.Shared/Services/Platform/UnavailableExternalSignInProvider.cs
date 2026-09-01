using DLR.Core.Contracts.Identity;

namespace BlazorDLR.Shared.Services.Platform;

/// <summary>
/// An <see cref="IExternalSignInProvider"/> that reports "not available" - the binding every
/// host uses today. Real Apple / Google bindings are additive work at store submission
/// (§7.16), and the Welcome page checks <see cref="IExternalSignInProvider.IsAvailable"/>
/// before it offers the button.
/// <para>
/// <see cref="Provider"/> still round-trips so the page can label a dimmed button correctly,
/// and <see cref="StartAsync"/> returns <c>null</c> - the contract's "the user cancelled" -
/// rather than throwing, so a caller that reaches it anyway lands on the cancel path.
/// </para>
/// </summary>
public sealed class UnavailableExternalSignInProvider : IExternalSignInProvider
{
	public UnavailableExternalSignInProvider(ExternalProvider provider) => Provider = provider;

	/// <inheritdoc />
	public ExternalProvider Provider { get; }

	/// <inheritdoc />
	public bool IsAvailable => false;

	/// <inheritdoc />
	public Task<TokenResponse?> StartAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult<TokenResponse?>(null);
}
