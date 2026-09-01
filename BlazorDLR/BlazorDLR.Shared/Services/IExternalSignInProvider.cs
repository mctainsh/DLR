using DLR.Core.Contracts.Identity;

namespace BlazorDLR.Shared.Services;

/// <summary>
/// Social sign-in - Apple and Google (§7.16).
/// <para>
/// <strong>Scaffolded, not shipped.</strong> The design decision is that Apple and Google
/// ship together, using <em>native</em> flows (<c>ASWebAuthenticationSession</c> on iOS,
/// Credential Manager on Android) rather than embedded web views - Google refuses embedded
/// web-view sign-in and the two stores' rules line up to make one-provider shipping the
/// wrong shape. Real bindings need registrations at Apple / Google Cloud, a URL scheme in
/// each mobile manifest, and a server endpoint that verifies the returned ID token against
/// the provider's JWKS. All of that lands with the Phase 3 exit-criterion store submission.
/// </para>
/// <para>
/// This interface is the seam the Welcome page will render against, so those provider
/// bindings are additive rather than a rewrite when they arrive.
/// </para>
/// </summary>
public interface IExternalSignInProvider
{
	/// <summary>What this provider identifies as. Used to pick a button icon and label.</summary>
	ExternalProvider Provider { get; }

	/// <summary>
	/// Whether this provider has been configured for the current deployment. False today
	/// on every host - the Welcome page shows "not yet available" and does not call
	/// <see cref="StartAsync"/>.
	/// </summary>
	bool IsAvailable { get; }

	/// <summary>
	/// Kicks off the provider's sign-in flow. On mobile this opens the platform's native
	/// auth session; on the web this navigates to the provider's authorize endpoint. Both
	/// return through a server callback that posts <c>/api/v1/auth/external</c>, which
	/// answers with a <see cref="TokenResponse"/> the caller applies to <see cref="State.AuthState"/>.
	/// </summary>
	Task<TokenResponse?> StartAsync(CancellationToken cancellationToken = default);
}

/// <summary>Which provider is behind <see cref="IExternalSignInProvider"/>.</summary>
public enum ExternalProvider
{
	/// <summary>Sign in with Apple. Mandatory on iOS whenever Google is also offered (§10.2).</summary>
	Apple = 0,

	/// <summary>Sign in with Google.</summary>
	Google = 1,
}
