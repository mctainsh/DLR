using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The permanent warnings over the live map: no network, GPS off, no fix (§4.3, §5.3).
/// <para>
/// All three are states rather than events — nothing takes them away but the state ending — and
/// all three describe the same failure from a rider's side: the group cannot see where they are,
/// on a screen that looks exactly the same whether it can or not. A map whose hub died ten minutes
/// ago is pixel-identical to a map where nobody has moved, which is why every one of these has to
/// be said rather than left to be inferred.
/// </para>
/// <para>
/// Phones only. Each is gated on the host having a receiver, so a browser — which has none
/// (§18.6) — gets none of them; <see cref="HostWithoutGpsTests"/> holds that end down.
/// </para>
/// </summary>
public sealed class GroupRideLiveWarningTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
	private static readonly Guid MeId = Guid.Parse("11111111-1111-1111-1111-111111111111");

	private readonly InMemoryDeviceSettings _settings = new();

	/// <summary>
	/// A phone, on a Live adventure, with sharing already on — which is the only arrangement in
	/// which any of these warnings is a fair thing to show. A receiver that is off because nobody
	/// asked it for anything is the correct resting state, and warning about that would be the app
	/// complaining that the rider has not agreed to be tracked.
	/// </summary>
	private async Task<(FakeApiClient Api, FakeRideHubClient Hub, Guid RideId)> WirePhoneAsync(
		bool sharing = true)
	{
		Guid rideId = Guid.NewGuid();

		FakeApiClient api = new()
		{
			PositionsResult = Array.Empty<RiderPositionDto>(),
			RideResult = new RideDetail(
				Id: rideId,
				Name: "Test adventure",
				Description: null,
				StartUtc: FixedInstant,
				JoinPolicy: JoinPolicyDto.Open,
				MemberCap: 50,
				MemberCount: 1,
				IsOrganiser: false,
				JoinCode: null,
				Permissions: new RidePermissions(),
				Members: [new RideMemberSummary(MeId, "Me", "Rider", FixedInstant, Sharing: sharing)]),
		};

		FakeRideHubClient hub = new();
		FakeTokenStore tokens = new();
		FakeTimeProvider clock = new(FixedInstant);
		AuthState auth = new(api, tokens, clock);

		// The sharing flag is read off the member row matching this session's user id, so without
		// a session the page would think nobody is sharing and every test here would pass for the
		// wrong reason.
		await auth.ApplySessionAsync(new TokenResponse(
			AccessToken: "access",
			ExpiresIn: 900,
			RefreshToken: "refresh",
			User: new AuthenticatedUser(MeId, "Me", HasEmail: true, EmailConfirmed: true)));

		Services.AddSingleton<IApiClient>(api);
		Services.AddSingleton<IRideHubClient>(hub);
		Services.AddSingleton<ITokenStore>(tokens);
		Services.AddSingleton<TimeProvider>(clock);
		Services.AddSingleton(auth);
		Services.AddSingleton<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(auth);
		Services.AddSingleton<ConfirmService>();
		Services.AddRealAuthorizationPipeline();
		this.CascadeAuthenticationState(auth);

		Services.AddSingleton<IMapInterop>(new FakeMapInterop());
		Services.AddSingleton<IDeviceSettings>(_settings);
		Services.AddSingleton<PrivateAreaState>();
		Services.AddSingleton<CurrentRideState>();
		Services.AddSingleton<RouteStyleState>();

		// Play's background-location disclosure, already accepted on this device. Without it the
		// receiver stops at a modal nothing here answers, and "no GPS signal" would be a test
		// waiting on a dialog.
		await _settings.SetAsync(LocationBroadcastState.DisclosureStorageKey, "1");

		Services.AddSingleton<ILocationProvider, FakeLocationProvider>();
		Services.AddSingleton<LocationUpdateRateState>();
		Services.AddSingleton<TrackRecordingState>();
		Services.AddSingleton<LocationBroadcastState>();

		ComponentFactories.Add<RideMap, StubRideMap>();

		return (api, hub, rideId);
	}

	/// <summary>This test's device GPS — the same instance the page's broadcaster watches.</summary>
	private FakeLocationProvider Gps => (FakeLocationProvider)Services.GetRequiredService<ILocationProvider>();

	private IRenderedComponent<GroupRideLive> RenderRide(Guid rideId)
	{
		IRenderedComponent<GroupRideLive> component =
			Render<GroupRideLive>(parameters => parameters.Add(page => page.RideId, rideId));

		// The hamburger is the snapshot having landed: the map is the whole page, so there is no
		// ride name in the markup to wait on.
		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		return component;
	}

	// ---------- No network ----------

	[Fact]
	public async Task WhenTheHubDrops_TheMapSaysThereIsNoNetwork()
	{
		// The failure this screen is worst at showing on its own: nothing arrives, so nothing
		// re-renders, and every pin keeps making a claim about where somebody is *now* that
		// quietly stopped being true.
		(_, FakeRideHubClient hub, Guid rideId) = await WirePhoneAsync();

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() => component.FindAll(".no-network").ShouldBeEmpty("the session connected on load."),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => hub.SetConnected(false));

		component.WaitForAssertion(
			() =>
			{
				AngleSharp.Dom.IElement strip = component.FindAll(".map-error.no-network").ShouldHaveSingleItem();
				strip.ClassList.ShouldContain("error", "it is a warning, and it is red.");
				strip.GetAttribute("role").ShouldBe("alert");
				strip.TextContent.ShouldContain("No network");
			},
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task WhenTheHubComesBack_TheWarningGoes()
	{
		// A warning that outlives its state is the fastest way to teach a rider to ignore it —
		// and SignalR reconnects on its own, so this transition happens without anybody tapping.
		(_, FakeRideHubClient hub, Guid rideId) = await WirePhoneAsync();

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		await component.InvokeAsync(() => hub.SetConnected(false));
		component.WaitForAssertion(
			() => component.FindAll(".no-network").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => hub.SetConnected(true));

		component.WaitForAssertion(
			() => component.FindAll(".no-network").ShouldBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));
	}

	// ---------- GPS disabled ----------

	[Fact]
	public async Task WhenLocationPermissionIsRefused_TheMapSaysTheGpsIsOff()
	{
		// Permission, not signal: no amount of riding into open sky fixes this one, so the strip
		// names the place it is fixed instead.
		(_, _, Guid rideId) = await WirePhoneAsync();
		Gps.Permission = LocationPermissionState.DeniedPermanently;

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() =>
			{
				AngleSharp.Dom.IElement strip = component.FindAll(".map-error.gps-disabled").ShouldHaveSingleItem();
				strip.ClassList.ShouldContain("error");
				strip.GetAttribute("role").ShouldBe("alert");
				strip.TextContent.ShouldContain("settings",
					customMessage: "a permission only the phone's settings can grant has to say so.");
			},
			timeout: TimeSpan.FromSeconds(3));

		// And not the other one: two red bars about one GPS is how a rider learns to read neither.
		component.FindAll(".no-gps").ShouldBeEmpty();
	}

	// ---------- No GPS signal ----------

	[Fact]
	public async Task WithTheReceiverUpAndNoFixYet_TheMapSaysThereIsNoSignal()
	{
		// A cold start, a garage, a gorge. Nothing to tap and nothing broken, which is exactly why
		// it is said: an untold rider reads their own missing pin as the app having failed.
		(_, _, Guid rideId) = await WirePhoneAsync();

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() =>
			{
				AngleSharp.Dom.IElement strip = component.FindAll(".map-error.no-gps").ShouldHaveSingleItem();
				strip.ClassList.ShouldContain("error");
				strip.TextContent.ShouldContain("No GPS signal");
			},
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task OnceAFixArrives_TheNoSignalWarningGoes()
	{
		(_, _, Guid rideId) = await WirePhoneAsync();

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		// Polled rather than waited on through the renderer — the watch starts after the last render
		// the page has any reason to do. See BackgroundWait.
		await BackgroundWait.UntilAsync(
			() => Gps.WatchCount == 1,
			"the receiver to start — sharing is on and the adventure is Live, so the GPS runs");

		Gps.Emit(new LocationFix(
			Latitude: -33.868,
			Longitude: 151.209,
			AccuracyM: 5,
			SpeedMps: 8,
			HeadingDeg: 90,
			RecordedUtc: FixedInstant));

		component.WaitForAssertion(
			() => component.FindAll(".no-gps").ShouldBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task ARiderWhoIsNotSharing_IsToldThat_AndNotAboutTheGps()
	{
		// The receiver is idle because they asked for it to be, so "no GPS signal" would be the
		// app reporting a fault it invented. The one true thing here is that nobody can see them.
		(_, _, Guid rideId) = await WirePhoneAsync(sharing: false);

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() => component.FindAll(".sharing-off").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		component.FindAll(".no-gps").ShouldBeEmpty();
		component.FindAll(".gps-disabled").ShouldBeEmpty();
	}

}
