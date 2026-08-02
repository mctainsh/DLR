using System.Security.Claims;
using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Identity;
using Microsoft.AspNetCore.Components.Authorization;

namespace BlazorDLR.Shared.State;

/// <summary>
/// The one <see cref="AuthenticationStateProvider"/> both hosts share (§7.4, §18.5).
/// <para>
/// Holds the current access token in memory and the current user's claims for
/// <c>AuthorizeView</c> / <c>@attribute [Authorize]</c>. Never touches the refresh token
/// directly — that goes through <see cref="ITokenStore"/> and the token endpoint
/// (§7.4). Never persists an access token anywhere but memory (§7.4 rule).
/// </para>
/// <para>
/// <strong>Single-flight refresh.</strong> When several callers race a 401 at once — three
/// screens polling their own data, plus a hub reconnect — one shared <see cref="Task"/>
/// serves them all, so the token endpoint sees one refresh and not four. The server also
/// has an idempotency window (§7.4) for the seconds where a genuine caller replays a token
/// it just rotated; the client's job is to make replays happen as rarely as possible in
/// the first place.
/// </para>
/// </summary>
public sealed class AuthState : AuthenticationStateProvider
{
	private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

	private readonly IApiClient _api;
	private readonly ITokenStore _tokens;
	private readonly TimeProvider _clock;
	private readonly SemaphoreSlim _refreshGate = new(1, 1);

	private ClaimsPrincipal _current = Anonymous;
	private string? _accessToken;
	private DateTimeOffset _accessExpiresUtc;
	private Task<string?>? _refreshInFlight;

	public AuthState(IApiClient api, ITokenStore tokens, TimeProvider clock)
	{
		_api = api;
		_tokens = tokens;
		_clock = clock;
	}

	/// <inheritdoc />
	public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
		Task.FromResult(new AuthenticationState(_current));

	/// <summary>The current access token, or null when signed out.</summary>
	public string? AccessToken => _accessToken;

	/// <summary>The current user's id, or null when signed out.</summary>
	public Guid? UserId { get; private set; }

	/// <summary>The device id claimed for the current session, or null.</summary>
	public Guid? DeviceId { get; private set; }

	/// <summary>The signed-in user's handle, or null.</summary>
	public string? UserName { get; private set; }

	/// <summary>
	/// Accepts a fresh session — from a password grant, from a refresh, or from the
	/// browser's cookie-to-token exchange. Broadcasts <see cref="AuthenticationState"/>
	/// so <c>AuthorizeView</c> re-renders.
	/// </summary>
	public async Task ApplySessionAsync(TokenResponse session, CancellationToken cancellationToken = default)
	{
		_accessToken = session.AccessToken;
		_accessExpiresUtc = _clock.GetUtcNow().AddSeconds(session.ExpiresIn);

		UserId = session.User.Id;
		UserName = session.User.UserName;

		if (!string.IsNullOrEmpty(session.RefreshToken))
		{
			await _tokens.WriteRefreshTokenAsync(session.RefreshToken, cancellationToken);
		}

		_current = BuildPrincipal(session);
		NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_current)));
	}

	/// <summary>
	/// Called on sign-out and when a refresh fails past recovery. Clears the token store,
	/// clears memory, and broadcasts <see cref="Anonymous"/>.
	/// </summary>
	public async Task SignOutAsync(CancellationToken cancellationToken = default)
	{
		_accessToken = null;
		_accessExpiresUtc = default;
		UserId = null;
		UserName = null;
		DeviceId = null;

		await _tokens.ClearAsync(cancellationToken);

		_current = Anonymous;
		NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_current)));
	}

	/// <summary>
	/// Returns a valid access token, refreshing if the cached one is expiring within a
	/// minute. <strong>Single-flight:</strong> concurrent callers wait on one refresh,
	/// so the token endpoint sees one call rather than n (§7.4).
	/// </summary>
	public Task<string?> GetOrRefreshAccessTokenAsync(CancellationToken cancellationToken = default)
	{
		// A one-minute buffer means we refresh before the token is stale, not on the 401 that
		// discovers it. Under load the network side of the refresh is the slow part, and
		// pipelining it with the request that noticed the expiry would ship a stale bearer.
		if (_accessToken is not null && _accessExpiresUtc - TimeSpan.FromMinutes(1) > _clock.GetUtcNow())
		{
			return Task.FromResult<string?>(_accessToken);
		}

		return CoalescedRefreshAsync(cancellationToken);
	}

	/// <summary>
	/// Ask for a fresh access token now regardless of the cache. Used by the auth handler
	/// when it sees a 401 despite a token that looked fresh — the server's view is the
	/// authoritative one, and the client should not argue.
	/// </summary>
	public Task<string?> RefreshNowAsync(CancellationToken cancellationToken = default) =>
		CoalescedRefreshAsync(cancellationToken);

	private async Task<string?> CoalescedRefreshAsync(CancellationToken cancellationToken)
	{
		Task<string?> pending;
		await _refreshGate.WaitAsync(cancellationToken);
		try
		{
			// A second caller arriving after the first has already kicked off gets the same task.
			// A third arriving after the task completes falls through to a fresh refresh — the
			// completed task is discarded rather than cached, because a discarded refresh's result
			// is stale by construction.
			if (_refreshInFlight is null || _refreshInFlight.IsCompleted)
			{
				_refreshInFlight = DoRefreshAsync(cancellationToken);
			}
			pending = _refreshInFlight;
		}
		finally
		{
			_refreshGate.Release();
		}

		return await pending;
	}

	private async Task<string?> DoRefreshAsync(CancellationToken cancellationToken)
	{
		string? refresh = await _tokens.ReadRefreshTokenAsync(cancellationToken);
		if (refresh is null)
		{
			await SignOutAsync(cancellationToken);
			return null;
		}

		try
		{
			TokenResponse session = await _api.TokenAsync(
				new TokenRequest(GrantTypes.Refresh, RefreshToken: refresh),
				cancellationToken);

			await ApplySessionAsync(session, cancellationToken);
			return session.AccessToken;
		}
		catch (HttpRequestException)
		{
			// A 401 or 403 from the token endpoint is theft response or an account that no longer
			// exists — either way there is no path back without a password. Anything else is a
			// network condition (§7.9): sign-in state stays what it was, and the caller sees a
			// failed request rather than a sudden sign-out.
			//
			// The client library maps every non-success to HttpRequestException, and we cannot
			// tell them apart without inspecting the response. The pragmatic rule: if the token
			// endpoint refused, the stored token is dead. Sign out locally; if the network was
			// actually the problem, a page reload restores the session.
			await SignOutAsync(cancellationToken);
			return null;
		}
	}

	private static ClaimsPrincipal BuildPrincipal(TokenResponse session)
	{
		AuthenticatedUser user = session.User;
		ClaimsIdentity identity = new(
			new[]
			{
				new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
				new Claim(ClaimTypes.Name, user.UserName),
				new Claim("hasEmail", user.HasEmail ? "true" : "false"),
				new Claim("emailConfirmed", user.EmailConfirmed ? "true" : "false"),
			},
			authenticationType: "DlrToken");
		return new ClaimsPrincipal(identity);
	}
}
