using System.Net;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using DLR.Core.Contracts.Identity;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.State;

/// <summary>
/// §7.9's two rules about an offline-first riding app's auth, which are really one rule seen from
/// both ends: <strong>on-device sign-in state is "a refresh token exists", not "the access token
/// is valid"</strong>.
/// <list type="bullet">
/// <item><see cref="Restore_WithNoNetwork_KeepsTheRiderSignedIn"/> - a relaunch in a dead zone must
/// not land on the Welcome screen asking a rider to sign in to an account they never signed out
/// of.</item>
/// <item><see cref="Refresh_ThatCouldNotReachTheServer_DoesNotSignOut"/> - the outline's named
/// test <c>Offline_401WithNoConnectivity_DoesNotSignUserOut</c>. Getting this wrong signs riders
/// out mid-ride, in the middle of nowhere, which is where the app matters most.</item>
/// </list>
/// </summary>
public sealed class AuthStateOfflineTests
{
	// ClockRules forbids an ambient clock read in test source (§10.4).
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static readonly Guid UserId = Guid.Parse("55555555-5555-5555-5555-555555555555");

	private static TokenResponse Session(string accessToken = "access", string refreshToken = "refresh") =>
		new(accessToken, 900, refreshToken, new AuthenticatedUser(UserId, "DaveSmith", HasEmail: true, EmailConfirmed: true));

	/// <summary>
	/// The three stores a phone has: a token in the Keychain, an account in Preferences, and a
	/// server that may or may not answer. Shared across two <see cref="AuthState"/> instances is
	/// how a test spells "the app was restarted".
	/// </summary>
	private sealed record Device(FakeTokenStore Tokens, InMemoryDeviceSettings Settings);

	private static AuthState Build(FakeApiClient api, Device device) =>
		new(api, device.Tokens, new FakeTimeProvider(FixedInstant), device.Settings);

	private static async Task<Device> SignedInDeviceAsync()
	{
		Device device = new(new FakeTokenStore(), new InMemoryDeviceSettings());
		FakeApiClient api = new();

		await Build(api, device).ApplySessionAsync(Session());

		return device;
	}

	[Fact]
	public async Task SigningIn_RemembersWhoOnThisDevice()
	{
		Device device = await SignedInDeviceAsync();

		device.Tokens.StoredToken.ShouldBe("refresh");

		RememberedAccount? remembered = RememberedAccount.Decode(
			await device.Settings.GetAsync(RememberedAccount.StorageKey));

		remembered.ShouldNotBeNull();
		remembered.UserId.ShouldBe(UserId);
		remembered.UserName.ShouldBe("DaveSmith");
		remembered.EmailConfirmed.ShouldBeTrue();
	}

	[Fact]
	public async Task Restore_WithNoNetwork_KeepsTheRiderSignedIn()
	{
		Device device = await SignedInDeviceAsync();

		// Relaunch, in a dead zone. The token endpoint cannot be reached at all - no status, no
		// ProblemDetails, because there was no response to take either from.
		FakeApiClient offline = new()
		{
			TokenException = new HttpRequestException("No such host is known."),
		};

		AuthState auth = Build(offline, device);
		await auth.RestoreAsync();

		auth.UserId.ShouldBe(UserId, "the traveller is who this device says they are until the server says otherwise.");
		auth.UserName.ShouldBe("DaveSmith");
		auth.IsOffline.ShouldBeTrue();
		auth.AccessToken.ShouldBeNull("nothing minted a token - being signed in locally is not being authorised.");
		device.Tokens.StoredToken.ShouldBe("refresh", "§7.9: a tunnel is not a credential failure.");

		Microsoft.AspNetCore.Components.Authorization.AuthenticationState state =
			await auth.GetAuthenticationStateAsync();

		state.User.Identity!.IsAuthenticated.ShouldBeTrue(
			"[Authorize] has to pass, or GroupRideLive redirects to Welcome and the cached adventure is unreachable (§4.4).");
		state.User.Identity.Name.ShouldBe("DaveSmith");
	}

	[Fact]
	public async Task Restore_WhenTheServerAnswers_UpgradesToARealSession()
	{
		Device device = await SignedInDeviceAsync();

		FakeApiClient online = new() { TokenResult = Session("fresh-access", "rotated-refresh") };

		AuthState auth = Build(online, device);
		await auth.RestoreAsync();

		auth.IsOffline.ShouldBeFalse("the server has spoken, so the adopted session is confirmed and replaced.");
		auth.AccessToken.ShouldBe("fresh-access");
		device.Tokens.StoredToken.ShouldBe("rotated-refresh", "§7.4 rotates on every refresh.");
	}

	[Fact]
	public async Task Restore_WhenTheServerRefuses_SignsOut()
	{
		Device device = await SignedInDeviceAsync();

		// 401 is the only status the refresh grant refuses with (TokenEndpoints): a revoked family,
		// a deleted account, a token that is not valid. There is no way back without a password.
		FakeApiClient refused = new()
		{
			TokenException = new ApiException(new ApiError(HttpStatusCode.Unauthorized, "Session ended", [])),
		};

		AuthState auth = Build(refused, device);
		await auth.RestoreAsync();

		auth.UserId.ShouldBeNull();
		auth.IsOffline.ShouldBeFalse();
		device.Tokens.StoredToken.ShouldBeNull();
		(await device.Settings.GetAsync(RememberedAccount.StorageKey)).ShouldBeNull(
			"an account left behind would be adopted by the next launch and show a signed-out traveller their own name.");
	}

	[Fact]
	public async Task Refresh_ThatCouldNotReachTheServer_DoesNotSignOut()
	{
		// The outline's Offline_401WithNoConnectivity_DoesNotSignUserOut, at the seam that decides
		// it. Not routed through RestoreAsync: this is the ordinary mid-ride refresh that
		// BearerAuthHandler drives, and it is the one that used to sign riders out in a tunnel.
		Device device = await SignedInDeviceAsync();

		FakeApiClient offline = new() { TokenException = new HttpRequestException("The network is unreachable.") };
		AuthState auth = Build(offline, device);

		(await auth.RefreshNowAsync()).ShouldBeNull("the caller's request fails, which is honest.");

		device.Tokens.StoredToken.ShouldBe("refresh", "and the session survives it.");
	}

	[Fact]
	public async Task Refresh_ThatWasRateLimited_DoesNotSignOut()
	{
		// A 429 is this device refreshing too often (§7.4's per-device limit), not a bad token.
		Device device = await SignedInDeviceAsync();

		FakeApiClient throttled = new()
		{
			TokenException = new ApiException(new ApiError(HttpStatusCode.TooManyRequests, "Slow down.", [])),
		};

		AuthState auth = Build(throttled, device);

		(await auth.RefreshNowAsync()).ShouldBeNull();

		device.Tokens.StoredToken.ShouldBe("refresh",
			"backing off is the answer to a 429 - signing the traveller out is not.");
	}

	[Fact]
	public async Task Restore_OnAHostWithNoReadableToken_DoesNothing()
	{
		// The web (§18.5): the refresh token is an HttpOnly cookie the script cannot read, so
		// CookieBackedTokenStore answers null and there is nothing to adopt an account beside.
		FakeApiClient api = new();
		AuthState auth = new(api, new CookieBackedTokenStore(), new FakeTimeProvider(FixedInstant), new InMemoryDeviceSettings());

		await auth.RestoreAsync();

		auth.UserId.ShouldBeNull();
		auth.IsOffline.ShouldBeFalse();
		api.Calls.ShouldNotContain(nameof(IApiClient.TokenAsync),
			"a browser with no stored token must not spend a round trip discovering that.");
	}

	[Fact]
	public async Task Restore_AfterASignIn_LeavesTheLiveSessionAlone()
	{
		// MainLayout calls this on first render, and nothing about a layout guarantees it renders
		// once - nor that it renders before the rider has signed in on the Welcome screen.
		Device device = new(new FakeTokenStore(), new InMemoryDeviceSettings());
		FakeApiClient api = new();

		AuthState auth = Build(api, device);
		await auth.ApplySessionAsync(Session("live-access"));

		await auth.RestoreAsync();
		await auth.RestoreAsync();

		auth.AccessToken.ShouldBe("live-access");
		auth.IsOffline.ShouldBeFalse();
		api.Calls.ShouldNotContain(nameof(IApiClient.TokenAsync), "a live session needs no refreshing.");
	}

	[Fact]
	public void RememberedAccount_RoundTripsAHandleWithTheSeparatorInIt()
	{
		// §7.2 does not allow a '|' today, and this encoding should not depend on that staying true
		// in a different assembly.
		RememberedAccount account = new(UserId, "Dave|Smith", HasEmail: false, EmailConfirmed: false);

		RememberedAccount? decoded = RememberedAccount.Decode(account.Encode());

		decoded.ShouldBe(account);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("garbage")]
	[InlineData("2|55555555555555555555555555555555|Dave|1|1")]      // a version this build cannot read
	[InlineData("1|not-a-guid|Dave|1|1")]
	[InlineData("1|00000000000000000000000000000000|Dave|1|1")]      // the empty id is not an account
	public void RememberedAccount_ThatCannotBeRead_IsNoAccount(string? stored)
	{
		RememberedAccount.Decode(stored).ShouldBeNull(
			"half an identity is worse than none - the half that survives is the half the app renders as somebody's name.");
	}
}
