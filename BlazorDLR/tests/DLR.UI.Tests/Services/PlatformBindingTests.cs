using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;

namespace DLR.UI.Tests.Services;

/// <summary>
/// The platform bindings in <c>BlazorDLR.Shared/Services/Platform/</c> — the implementations
/// a host registers when the capability behind a seam does not exist there (no GPS in a
/// browser, no JS runtime during a prerender, no device storage on the server). These are
/// shipping behaviour, not scaffolding, so the tests pin the two properties that matter:
/// <list type="bullet">
///   <item>The "unavailable" bindings report their unavailability cleanly and do not throw.
///     The Welcome page and every other consumer branches on the
///     <c>IsAvailable</c>/<c>IsSupported</c> flag, so a throw there would be a crash on a
///     path the caller has already handled.</item>
///   <item>Where a binding does throw, the message names the reason — which host, and what
///     re-resolves the seam instead — so a bug report has the answer without a stack read.</item>
/// </list>
/// </summary>
public sealed class PlatformBindingTests
{
	// ---------- CookieBackedTokenStore ----------

	[Fact]
	public async Task CookieBackedTokenStore_ReadReturnsNull_WriteIsNoOp_ClearIsNoOp()
	{
		CookieBackedTokenStore store = new();

		// The refresh token lives in an HttpOnly cookie the JS heap cannot touch (§18.5).
		string? read = await store.ReadRefreshTokenAsync();
		read.ShouldBeNull("§18.5: the JS heap cannot read the HttpOnly cookie — Read must return null, not throw.");

		// Write is silent — the cookie is authoritative.
		await Should.NotThrowAsync(() => store.WriteRefreshTokenAsync("would-be-refresh").AsTask());
		await Should.NotThrowAsync(() => store.ClearAsync().AsTask());
	}

	// ---------- NoopLocationProvider ----------

	[Fact]
	public async Task NoopLocationProvider_IsUnsupported_AndYieldsNoFixes()
	{
		// The MAUI Windows and macOS heads bind this — the browsers no longer bind anything at
		// all, so "no receiver" is a missing service there rather than a stub (see
		// HostWithoutGpsTests). It still has to answer cleanly on the target that does bind it.
		NoopLocationProvider provider = new();

		provider.IsSupported.ShouldBeFalse("§18.6: no receiver behind this MAUI target.");
		provider.IsRecording.ShouldBeFalse();

		LocationPermissionState permission = await provider.EnsurePermissionsAsync();
		permission.ShouldBe(LocationPermissionState.NotSupported,
			"the permission call must report not-supported rather than pretend to grant.");

		// WatchAsync yields nothing rather than throw — a UI awaiting the first fix
		// simply never gets one, which is the accurate answer on a host without GPS.
		int fixes = 0;
		await foreach (LocationFix _ in provider.WatchAsync(AccuracyProfile.Balanced))
		{
			fixes++;
			break;
		}
		fixes.ShouldBe(0);
	}

	// ---------- NoopMediaPicker ----------

	[Fact]
	public async Task NoopMediaPicker_ReturnsNull_AndCannotCapture()
	{
		NoopMediaPicker picker = new();

		picker.CanCapture.ShouldBeFalse();
		(await picker.PickPhotoAsync()).ShouldBeNull(
			"cancelled/unsupported picker returns null — the caller reads null as 'no photo attached', not as an error.");
		(await picker.CapturePhotoAsync()).ShouldBeNull();
	}

	// ---------- NoopNotificationService ----------

	[Fact]
	public async Task NoopNotificationService_IsUnsupported_AndRegistrationIsNoOp()
	{
		NoopNotificationService service = new();

		service.IsSupported.ShouldBeFalse("§18.2: no push in the browser in v1.");
		await Should.NotThrowAsync(() => service.RegisterAsync("token"));
		await Should.NotThrowAsync(() => service.UnregisterAsync());
	}

	// ---------- UnavailableMapPackStore / UnavailableMapPackServer ----------

	[Fact]
	public async Task UnavailableMapPack_HoldsNothing_AndServesNothing()
	{
		UnavailableMapPackStore store = new();
		UnavailableMapPackServer server = new();

		store.IsSupported.ShouldBeFalse("§18.6: a browser has nowhere to keep a few hundred megabytes.");
		server.IsSupported.ShouldBeFalse();

		(await store.ListAsync()).ShouldBeEmpty("empty rather than null — the settings screen enumerates it.");
		(await store.OpenReadAsync("au-nsw")).ShouldBeNull();
		await Should.NotThrowAsync(() => store.DeleteAsync("au-nsw").AsTask());

		// Null rather than a throw: it is what sends MapSourceState.Effective back to an online
		// source, which is a working map under the routes and pins the screen is actually for.
		(await server.ResolveAsync("au-nsw")).ShouldBeNull();
	}

	// ---------- UnavailableScreenWakeLock ----------

	[Fact]
	public async Task UnavailableScreenWakeLock_IsUnsupported_AndBothCallsAreNoOps()
	{
		UnavailableScreenWakeLock wakeLock = new();

		wakeLock.IsSupported.ShouldBeFalse(
			"§18.6: holding the screen on is for a phone on a bar mount, not a laptop with a tab open.");

		// The live map calls these unconditionally — a throw here would crash the one page the
		// whole app is for, on the host where nothing was going to happen anyway.
		await Should.NotThrowAsync(() => wakeLock.RequestAsync().AsTask());
		await Should.NotThrowAsync(() => wakeLock.ReleaseAsync().AsTask());

		// And an unbalanced release, which is what a page torn down before its first render does.
		await Should.NotThrowAsync(() => wakeLock.ReleaseAsync().AsTask());
	}

	// ---------- UnavailableExternalSignInProvider ----------

	[Fact]
	public async Task UnavailableExternalSignInProvider_ReportsProvider_ButIsUnavailable()
	{
		UnavailableExternalSignInProvider apple = new(ExternalProvider.Apple);

		apple.Provider.ShouldBe(ExternalProvider.Apple,
			"the provider identifier round-trips so the Welcome page can label the button 'Sign in with Apple'.");
		apple.IsAvailable.ShouldBeFalse(
			"§7.16: real Apple/Google bindings arrive with store submission — Welcome must dim the button until then.");
		(await apple.StartAsync()).ShouldBeNull(
			"§7.16: StartAsync must return null rather than throw — the composer treats null as 'user cancelled', which is the correct posture until a real binding is wired.");
	}

	[Fact]
	public async Task UnavailableExternalSignInProvider_Google_HasCorrectIdentifier()
	{
		UnavailableExternalSignInProvider google = new(ExternalProvider.Google);

		google.Provider.ShouldBe(ExternalProvider.Google);
		google.IsAvailable.ShouldBeFalse();
		(await google.StartAsync()).ShouldBeNull();
	}

	// ---------- UninitialisedMapInterop ----------

	[Fact]
	public void UninitialisedMapInterop_ReportsProvider_ButInitThrows()
	{
		UninitialisedMapInterop map = new();

		map.Provider.ShouldBe(MapProvider.MapLibreOsm,
			"the binding reports a provider so callers reading only the flag do not blow up on a null property.");

		// Init during a prerender must fail loudly, and name why.
		NotImplementedException nx = Should.Throw<NotImplementedException>(() =>
			map.InitAsync(default, new MapOptions(new MapCamera(0, 0, 0))).GetAwaiter().GetResult());
		nx.Message.Contains("SSR", StringComparison.Ordinal).ShouldBeTrue(
			"the message names the host that cannot answer, so a bug reader has the reason without a stack read.");
		nx.Message.Contains("WASM client", StringComparison.Ordinal).ShouldBeTrue(
			"and names what does initialise the map, so the reader knows this is by design rather than a missing registration.");
	}

	// ---------- ThrowingRideHubClient ----------

	[Fact]
	public async Task ThrowingRideHubClient_IsNotConnected_AndConnectThrowsWithReason()
	{
		await using ThrowingRideHubClient hub = new();

		hub.IsConnected.ShouldBeFalse(
			"a static render has no connection — callers reading the flag get the honest answer rather than a throw.");

		NotImplementedException nx = await Should.ThrowAsync<NotImplementedException>(
			() => hub.ConnectAsync());
		nx.Message.Contains("SSR", StringComparison.Ordinal).ShouldBeTrue(
			"the message names the host that cannot connect, so a bug reader has the reason without a stack read.");
	}
}
