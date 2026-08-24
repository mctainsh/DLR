using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Identity;

namespace BlazorDLR.Shared.State;

/// <summary>
/// Whether the signed-in account may see the administration screens (§14.6).
/// <para>
/// <strong>This decides what is offered, never what is allowed.</strong> Every route behind it is
/// checked again by the server against its own configured roster, and a client that answered
/// <c>true</c> here without cause would find three endpoints returning 403. It exists so that a
/// rider who is not an administrator is not shown a menu entry that only ever leads to an error.
/// </para>
/// <para>
/// Asked once per session and remembered. The answer comes from <see cref="OwnProfile.IsAdmin"/>,
/// which is the same roster lookup the server's own policy makes — so the two cannot disagree the
/// way a claim minted at sign-in and a config file edited since would.
/// </para>
/// </summary>
/// <param name="api">Where the profile comes from.</param>
public sealed class AdminAccess(IApiClient api)
{
	/// <summary>
	/// The in-flight or completed answer.
	/// <para>
	/// The <em>task</em> is cached rather than the result, so several components asking at once
	/// during one render share a single request instead of racing to make their own.
	/// </para>
	/// </summary>
	private Task<bool>? _asked;

	/// <summary>
	/// Whether this account is on the server's admin roster.
	/// </summary>
	/// <param name="cancellationToken">Abandons the first call only; later callers get the cache.</param>
	/// <returns><c>false</c> for anybody not on the roster, and for any call that could not be made.</returns>
	public Task<bool> IsAdminAsync(CancellationToken cancellationToken = default) =>
		_asked ??= AskAsync(cancellationToken);

	/// <summary>
	/// Forgets the answer, so the next ask goes back to the server.
	/// </summary>
	/// <remarks>
	/// <strong>Nothing calls this yet.</strong> It is the seam for sign-out: on a host where this is
	/// scoped for the life of the tab, the next account signed in without it inherits the previous
	/// one's menu — which is only a menu, but it would be the wrong menu. Wiring it is not as simple
	/// as calling it from <c>AuthState.SignOutAsync</c>, which cannot take an <c>AdminAccess</c>
	/// without closing a cycle through <see cref="IApiClient"/>; the answer is probably for this to
	/// listen to <c>AuthenticationStateChanged</c> instead.
	/// </remarks>
	public void Forget() => _asked = null;

	/// <summary>
	/// One profile fetch, with every failure answered as "not an administrator".
	/// </summary>
	/// <remarks>
	/// Swallowing is right here and only here: this is a question about what to <em>offer</em>, and
	/// the safe answer to "could not tell" is to offer nothing. The screens behind it report their
	/// own failures properly, because there the failure is the thing the reader came to see.
	/// </remarks>
	private async Task<bool> AskAsync(CancellationToken cancellationToken)
	{
		try
		{
			OwnProfile profile = await api.GetProfileAsync(cancellationToken);

			return profile.IsAdmin;
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			return false;
		}
	}
}
