using System.Net;
using System.Security.Claims;
using BlazorDLR.Shared.Diagnostics;
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
/// <para>
/// <strong>Signed in is "a refresh token exists", not "the access token is valid" (§7.9).</strong>
/// This app is used at trailheads. A request that failed because there was nothing to fail
/// against is a network condition, and treating it as a credential failure would sign riders out
/// in the middle of nowhere — which is where the app matters most and where the Welcome screen
/// helps least. <see cref="RestoreAsync"/> is the other half of that rule: a relaunch adopts the
/// account this device remembers and confirms it with the server afterwards, rather than the
/// other way round.
/// </para>
/// </summary>
public sealed class AuthState : AuthenticationStateProvider
{
	private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

	private readonly IApiClient _api;
	private readonly ITokenStore _tokens;
	private readonly TimeProvider _clock;
	private readonly IDeviceSettings? _settings;
	private readonly SemaphoreSlim _refreshGate = new(1, 1);

	private ClaimsPrincipal _current = Anonymous;
	private string? _accessToken;
	private DateTimeOffset _accessExpiresUtc;
	private Task<string?>? _refreshInFlight;
	private bool _restored;

	/// <param name="api">The token endpoint, among everything else.</param>
	/// <param name="tokens">Where the refresh token lives — the Keychain on a phone, a cookie the script cannot read on the web (§7.4, §18.5).</param>
	/// <param name="clock">Decides when the cached access token is close enough to expiry to refresh (§10.4).</param>
	/// <param name="settings">
	/// Where this device remembers <em>who</em> signed in, so a relaunch with no signal can open on
	/// their rides (see <see cref="RememberedAccount"/>). <c>null</c> for a caller that remembers
	/// nobody — which is also what a host without one behaves like, since the account is only ever
	/// adopted alongside a refresh token and the browser hosts hold none (§18.5).
	/// </param>
	public AuthState(IApiClient api, ITokenStore tokens, TimeProvider clock, IDeviceSettings? settings = null)
	{
		_api = api;
		_tokens = tokens;
		_clock = clock;
		_settings = settings;
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
	/// Whether the current sign-in is one this device adopted on its own and the server has not
	/// confirmed since (§7.9) — the state a relaunch in a dead zone lands in.
	/// <para>
	/// The rider is signed in for every purpose the app decides locally: <c>[Authorize]</c> passes,
	/// their own ride opens from the cache (§4.4), and their name is on screen. What they do not
	/// have is an access token, so anything that needs the server fails until one arrives — which
	/// is not a different outcome from being online with no signal, only an honest name for it.
	/// </para>
	/// <para>
	/// Cleared by the next successful refresh, and by signing out. Never set on the web, where
	/// there is no readable refresh token to adopt an account beside (§18.5).
	/// </para>
	/// </summary>
	public bool IsOffline { get; private set; }

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

		// The username, never a token or any part of one. Who is signed in explains most of the
		// authorisation failures further down a log; the credential explains none of them and
		// would be sitting in a file a rider is about to paste into a message.
		DiagnosticLog.Write($"Signed in as {UserName}.");

		// The server has just spoken, so whatever this device had adopted on its own is confirmed
		// or replaced. Cleared before the awaits below so a screen reading it off the broadcast
		// cannot see a stale "offline" beside a live session.
		IsOffline = false;
		_restored = true;

		if (!string.IsNullOrEmpty(session.RefreshToken))
		{
			await _tokens.WriteRefreshTokenAsync(session.RefreshToken, cancellationToken);

			// Only alongside a token, and that is the whole rule: a host that cannot hold a refresh
			// token is one where a remembered account could never be adopted (see RestoreAsync), so
			// storing one there would be a name on a device with nothing to back it. The web is
			// exactly that host — its token endpoint strips the refresh token out of the body and
			// puts it in a cookie the script cannot read (§18.5).
			await RememberAccountAsync(RememberedAccount.From(session.User), cancellationToken);
		}

		_current = BuildPrincipal(session.User);
		NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_current)));
	}

	/// <summary>
	/// Brings back the session this device was last left in, so a relaunch does not ask a rider to
	/// sign in to an account they never signed out of (§7.9).
	/// <para>
	/// <strong>The remembered account is adopted first and confirmed second.</strong> The obvious
	/// order — refresh, then sign in if it worked — makes every launch wait on a round trip, and
	/// makes a launch with no network end at the Welcome screen. So this adopts
	/// <see cref="RememberedAccount"/> immediately when there is a refresh token beside it, marks
	/// the session <see cref="IsOffline"/>, and then asks the token endpoint. Three things can come
	/// back:
	/// </para>
	/// <list type="bullet">
	/// <item>a session, which replaces the adopted one and clears the flag;</item>
	/// <item>a refusal — 401, the only status the refresh grant refuses with — which signs out;</item>
	/// <item>nothing at all, which is a rider in a tunnel and changes nothing.</item>
	/// </list>
	/// <para>
	/// <strong>Idempotent, and safe to call from a layout's first render.</strong> A session that
	/// is already live is left alone, and so is a second call — the app has exactly one launch, but
	/// nothing about the shell that calls this guarantees it renders once.
	/// </para>
	/// <para>
	/// On the web this does nothing: <c>CookieBackedTokenStore</c> answers <c>null</c>, so there is
	/// no token to adopt an account beside, and the cookie handoff is what signs a browser in
	/// (§18.5).
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancels the reads and the refresh.</param>
	public async Task RestoreAsync(CancellationToken cancellationToken = default)
	{
		if (_restored || _accessToken is not null)
		{
			return;
		}

		// Set before the first await: this is called from a render callback, and a layout that
		// renders twice must not start two restores racing each other into ApplySessionAsync.
		_restored = true;

		string? refresh = await _tokens.ReadRefreshTokenAsync(cancellationToken);

		if (refresh is null)
		{
			// Nothing signed in here — a fresh install, a vault that will not decrypt (§7.4), or a
			// browser. The Welcome screen is the honest destination and it is already where an
			// anonymous principal sends the rider.
			return;
		}

		if (await ReadAccountAsync(cancellationToken) is { } account)
		{
			// Signed in, on this device's word. Everything the app decides locally now works —
			// including opening the remembered ride off the cache (§4.4), which is the entire point
			// of doing this before the network rather than after it.
			UserId = account.UserId;
			UserName = account.UserName;
			DiagnosticLog.Write($"Session restored for {UserName}.");
			IsOffline = true;

			_current = BuildPrincipal(account);
			NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_current)));
		}

		// And now the server's answer, which either upgrades what we just did or ends it. Awaited
		// rather than abandoned so a caller can order its own startup against it; the screens do
		// not wait, because the broadcast above has already let them render.
		await CoalescedRefreshAsync(cancellationToken);
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
		DiagnosticLog.Write("Signed out.");
		DeviceId = null;
		IsOffline = false;

		// Nothing left to restore, and there must not be: an account left behind here would be
		// adopted by the next launch (see RestoreAsync) and show a signed-out rider their own name.
		// Cleared alongside the token rather than instead of it — both, or the pair disagrees.
		_restored = true;

		await _tokens.ClearAsync(cancellationToken);
		await ForgetAccountAsync(cancellationToken);

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
		catch (HttpRequestException exception)
		{
			// §7.9's rule, and the one place it is decided: **never sign out on a failure that was
			// not the server refusing.**
			//
			// The two are tellable apart, and the distinction is load-bearing enough to be worth
			// stating. HttpApiClient throws ApiException — which *is* an HttpRequestException, so it
			// arrives here — carrying the status the server answered. A transport failure throws the
			// base type with StatusCode null, because there was no response to take one from.
			//
			// 401 is the only status the refresh grant refuses with (TokenEndpoints): a revoked
			// family, an account that has been deleted, a token that is not valid. 403 is included
			// because a token endpoint that ever starts answering it means the same thing. Both are
			// unrecoverable without a password, so the stored token is dead and the session ends.
			//
			// Everything else stays signed in. A 429 is the device refreshing too often, a 5xx is a
			// server having a bad minute, and a null status is a rider in a tunnel — and a rider
			// mid-ride in a dead zone being returned to the Welcome screen is the exact failure this
			// rule exists to prevent. The caller gets null and its request fails; the next refresh
			// tries again.
			if (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
			{
				await SignOutAsync(cancellationToken);
			}

			return null;
		}
	}

	/// <summary>
	/// The claims <c>AuthorizeView</c> and <c>@attribute [Authorize]</c> read, built from the four
	/// things the app knows about an account.
	/// <para>
	/// One builder for both callers on purpose: a session off the wire and an account this device
	/// remembered must produce an identical principal, or a screen would be able to tell which of
	/// the two signed the rider in — and the answer to that question is <see cref="IsOffline"/>,
	/// deliberately, rather than a claim that is quietly missing.
	/// </para>
	/// </summary>
	private static ClaimsPrincipal BuildPrincipal(Guid userId, string userName, bool hasEmail, bool emailConfirmed)
	{
		ClaimsIdentity identity = new(
			new[]
			{
				new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
				new Claim(ClaimTypes.Name, userName),
				new Claim("hasEmail", hasEmail ? "true" : "false"),
				new Claim("emailConfirmed", emailConfirmed ? "true" : "false"),
			},
			authenticationType: "DlrToken");
		return new ClaimsPrincipal(identity);
	}

	private static ClaimsPrincipal BuildPrincipal(AuthenticatedUser user) =>
		BuildPrincipal(user.Id, user.UserName, user.HasEmail, user.EmailConfirmed);

	private static ClaimsPrincipal BuildPrincipal(RememberedAccount account) =>
		BuildPrincipal(account.UserId, account.UserName, account.HasEmail, account.EmailConfirmed);

	/// <summary>
	/// Reads the account this device last signed in as, or <c>null</c> on a host that remembers
	/// nobody or a device that has never stored one.
	/// </summary>
	private async Task<RememberedAccount?> ReadAccountAsync(CancellationToken cancellationToken) =>
		_settings is null
			? null
			: RememberedAccount.Decode(await _settings.GetAsync(RememberedAccount.StorageKey, cancellationToken));

	private async Task RememberAccountAsync(RememberedAccount account, CancellationToken cancellationToken)
	{
		if (_settings is not null)
		{
			await _settings.SetAsync(RememberedAccount.StorageKey, account.Encode(), cancellationToken);
		}
	}

	/// <summary>
	/// Forgets the remembered account. Removes the key rather than storing a blank one — see
	/// <see cref="IDeviceSettings.RemoveAsync"/>.
	/// </summary>
	private async Task ForgetAccountAsync(CancellationToken cancellationToken)
	{
		if (_settings is not null)
		{
			await _settings.RemoveAsync(RememberedAccount.StorageKey, cancellationToken);
		}
	}
}
