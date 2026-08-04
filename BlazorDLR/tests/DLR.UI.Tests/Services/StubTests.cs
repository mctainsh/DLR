using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Stubs;

namespace DLR.UI.Tests.Services;

/// <summary>
/// The Phase-0 stubs. Every seam has one so a screen that reaches for it before its
/// real binding is wired fails with a phase-named message rather than a null-reference.
/// These tests pin the two properties that matter:
/// <list type="bullet">
///   <item>The message names the phase — "phase 0 stub, arrives in phase 1" — so a
///     bug report has the reason without a stack read.</item>
///   <item>The "noop" stubs (CookieBackedTokenStore, NoopMediaPicker,
///     NoopLocationProvider, NoopNotificationService, UnavailableExternalSignInProvider)
///     report their unavailability cleanly and do not throw. The Welcome page and every
///     other consumer branches on the <c>IsAvailable</c>/<c>IsSupported</c> flag.</item>
/// </list>
/// </summary>
public sealed class StubTests
{
	// ---------- ThrowingApiClient ----------

	[Fact]
	public async Task ThrowingApiClient_EveryCall_ThrowsWithPhaseMessage()
	{
		ThrowingApiClient api = new();

		NotImplementedException nx = await Should.ThrowAsync<NotImplementedException>(
			() => api.GetAboutAsync());
		nx.Message.Contains("Phase 0 stub", StringComparison.Ordinal).ShouldBeTrue(
			"the message names the phase so a bug reader has the reason without a stack read.");
		nx.Message.Contains("SharedFrontend.md", StringComparison.Ordinal).ShouldBeTrue();
	}

	[Fact]
	public async Task ThrowingApiClient_VoidCalls_AlsoThrow()
	{
		ThrowingApiClient api = new();

		// A void-returning stub must still throw — the caller cannot silently proceed
		// on a no-op when the real endpoint would have altered state.
		await Should.ThrowAsync<NotImplementedException>(
			() => api.RevokeSessionAsync(Guid.NewGuid()));
	}

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
		NoopLocationProvider provider = new();

		provider.IsSupported.ShouldBeFalse("§18.6: no continuous GPS in a browser.");
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

		map.Provider.ShouldBe(MapProvider.OpenLayersOsm,
			"the stub reports a provider so callers reading only the flag do not blow up on a null property.");

		// Init on an uninitialised interop must fail loudly with the phase message.
		NotImplementedException nx = Should.Throw<NotImplementedException>(() =>
			map.InitAsync(default, new MapOptions(new MapCamera(0, 0, 0))).GetAwaiter().GetResult());
		nx.Message.Contains("Phase 0 stub", StringComparison.Ordinal).ShouldBeTrue();
	}
}
