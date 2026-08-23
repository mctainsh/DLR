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
/// When a rider's pin comes off the live map (§5.3, §18.6).
/// <para>
/// The behaviour exists because nothing else takes one off. A position stays in the ride's cache
/// until its owner stops sharing (§5.6) and is rebroadcast on every tick whether or not it has
/// moved, so a phone that went flat, was left in a jacket or lost signal in a valley leaves a mark
/// that reads exactly like somebody standing there — and a group rides back for it. How long that
/// mark is worth drawing is the rider's own answer, chosen on Settings → Maps and held on this
/// device.
/// </para>
/// <para>
/// <strong>The map only.</strong> An expired pin is not a deleted position: the ride still holds
/// it, everybody else's phone still receives it, and its owner keeps their row on the members list
/// with the age of that fix beside it (see <c>MemberRoster</c>). The pin is the one part of this
/// screen that cannot say how old it is, which is why it is the part that goes.
/// </para>
/// </summary>
public sealed class GroupRideLivePinExpiryTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	private static readonly Guid MeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly Guid AliceId = Guid.Parse("22222222-2222-2222-2222-222222222222");

	private readonly FakeMapInterop _map = new()
	{
		// RideMap's stated-error branch (§4.5), which keeps SkiaMapOverlay unmounted — its
		// SKCanvasView cannot render outside a browser. What is under test is the layer the page
		// hands the map, not the pixels.
		InitException = new InvalidOperationException("Test host — map interop is stubbed."),
	};

	private readonly InMemoryDeviceSettings _settings = new();
	private FakeTimeProvider _clock = default!;

	/// <summary>
	/// Alice's last fix, <paramref name="minutesAgo"/> before now. Hers rather than this rider's:
	/// on a host with a receiver the page drops the ride's copy of its own rider anyway and draws
	/// the device's own reading instead, so this rider's pin could never test the rule.
	/// </summary>
	private static RiderPositionDto AliceLastSeen(double minutesAgo) => new(
		UserId: AliceId,
		UserName: "Alice",
		Lat: PositionScale.FromDegrees(-37.8136),
		Lon: PositionScale.FromDegrees(144.9631),
		SpeedMps: 0,
		HeadingDeg: null,
		RecordedUtc: FixedInstant.AddMinutes(-minutesAgo));

	private async Task<(FakeRideHubClient hub, Guid rideId)> WireServicesAsync(RiderPositionDto position)
	{
		Guid rideId = Guid.NewGuid();
		FakeApiClient api = new()
		{
			PositionsResult = [position],
			RideResult = new RideDetail(
				Id: rideId,
				Name: "Test adventure",
				Description: null,
				StartUtc: FixedInstant,
				State: RideStateDto.Live,
				JoinPolicy: JoinPolicyDto.Open,
				MemberCap: 50,
				MemberCount: 2,
				IsOrganiser: false,
				JoinCode: null,
				Permissions: new RidePermissions(),
				Members:
				[
					new RideMemberSummary(MeId, "Me", "Rider", FixedInstant, true, true),
					new RideMemberSummary(AliceId, "Alice", "Rider", FixedInstant, true, true),
				]),
		};

		FakeRideHubClient hub = new();
		FakeTokenStore tokens = new();
		_clock = new FakeTimeProvider(FixedInstant);
		AuthState auth = new(api, tokens, _clock);

		// The page tells its own rider apart from everybody else by AuthState.UserId; without a
		// session there is none, and the rule under test would be applied to the wrong pin.
		await auth.ApplySessionAsync(new TokenResponse(
			AccessToken: "access",
			ExpiresIn: 900,
			RefreshToken: "refresh",
			User: new AuthenticatedUser(MeId, "Me", HasEmail: true, EmailConfirmed: true)));

		Services.AddSingleton<IApiClient>(api);
		Services.AddSingleton<IRideHubClient>(hub);
		Services.AddSingleton<ITokenStore>(tokens);
		Services.AddSingleton<TimeProvider>(_clock);
		Services.AddSingleton(auth);
		Services.AddSingleton<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(auth);
		Services.AddSingleton<ConfirmService>();
		Services.AddRealAuthorizationPipeline();
		this.CascadeAuthenticationState(auth);

		Services.AddSingleton<IMapInterop>(_map);
		Services.AddSingleton<IDeviceSettings>(_settings);
		Services.AddSingleton<PrivateAreaState>();
		Services.AddSingleton<CurrentRideState>();

		// The GPS seam (§4.3). Nothing here emits a fix — what matters is only that the host has a
		// receiver at all, which is what makes this rider's own pin the device's business and
		// Alice's the ride's.
		Services.AddSingleton<ILocationProvider, FakeLocationProvider>();
		Services.AddSingleton<GpsProfileState>();
		Services.AddSingleton<TrackRecordingState>();
		Services.AddSingleton<LocationBroadcastState>();

		ComponentFactories.Add<RideMap, StubRideMap>();

		return (hub, rideId);
	}

	private IRenderedComponent<GroupRideLive> RenderRide(Guid rideId) =>
		Render<GroupRideLive>(parameters => parameters.Add(p => p.RideId, rideId));

	/// <summary>Whether Alice is currently drawn on the map.</summary>
	private static bool AliceIsOnTheMap(IRenderedComponent<GroupRideLive> component) =>
		component.FindComponent<StubRideMap>().Instance.Markers?.ContainsKey(AliceId) == true;

	/// <summary>
	/// Waits for the page to be up and for the device store to have been read — the read is on
	/// first render, so a test that asserts an absence without waiting would pass before the
	/// setting had arrived and for the wrong reason.
	/// </summary>
	private static void WaitForTheRideToLoad(IRenderedComponent<GroupRideLive> component) =>
		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

	[Fact]
	public async Task ARecentFix_IsDrawn()
	{
		(_, Guid rideId) = await WireServicesAsync(AliceLastSeen(minutesAgo: 3));

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() => AliceIsOnTheMap(component).ShouldBeTrue("three minutes is a set of lights, not a missing rider."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task AFixOlderThanTheDefault_IsNotDrawnAtAll()
	{
		// Nothing stored, so this is PinExpiry.Default — ten minutes.
		(_, Guid rideId) = await WireServicesAsync(AliceLastSeen(minutesAgo: 20));

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);
		WaitForTheRideToLoad(component);

		component.WaitForAssertion(
			() => AliceIsOnTheMap(component).ShouldBeFalse(
				"a twenty-minute-old fix is rebroadcast every tick and looks exactly like a rider " +
				"standing there — which is how a group ends up riding back for a flat phone."),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// The setting doing its job in the direction that keeps more on screen: a rider who has asked
	/// for six hours is somebody who would rather see where a missing rider was last heard from.
	/// </summary>
	[Fact]
	public async Task ADeviceThatAsksToKeepPinsLonger_StillDrawsTheOldFix()
	{
		await _settings.SetAsync(PinExpiry.StorageKey, PinExpiry.Encode(TimeSpan.FromHours(6)));

		(_, Guid rideId) = await WireServicesAsync(AliceLastSeen(minutesAgo: 20));

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);
		WaitForTheRideToLoad(component);

		component.WaitForAssertion(
			() => AliceIsOnTheMap(component).ShouldBeTrue(
				"the choice is the rider's, and this one asked to keep pins for six hours."),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// The case the whole thing is for: a pin that was fine when the map opened and is not fine
	/// a quarter of an hour later. The batch carrying the same unchanged fix is what the server
	/// really sends every tick — the position lives in its cache until sharing stops (§5.6) — so
	/// this is the ordinary path rather than a contrived one.
	/// </summary>
	[Fact]
	public async Task AFixThatAgesPastTheLimit_ComesOffTheMapOnTheNextBatch()
	{
		(FakeRideHubClient hub, Guid rideId) = await WireServicesAsync(AliceLastSeen(minutesAgo: 1));

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() => AliceIsOnTheMap(component).ShouldBeTrue("she was a minute old when the map opened."),
			timeout: TimeSpan.FromSeconds(3));

		_clock.Advance(TimeSpan.FromMinutes(15));

		await component.InvokeAsync(() => hub.RaisePositionsUpdated(new PositionBatch(
			rideId,
			[
				new PositionFix(
					AliceId,
					PositionScale.FromDegrees(-37.8136),
					PositionScale.FromDegrees(144.9631),
					SpeedMps: 0,
					HeadingDeg: null,
					RecordedUtc: FixedInstant.AddMinutes(-1)),
			])));

		component.WaitForAssertion(
			() => AliceIsOnTheMap(component).ShouldBeFalse(
				"the fix has not changed and the batch keeps arriving — the age is the only thing " +
				"that moved, and it is the whole of the rule."),
			timeout: TimeSpan.FromSeconds(3));
	}
}
