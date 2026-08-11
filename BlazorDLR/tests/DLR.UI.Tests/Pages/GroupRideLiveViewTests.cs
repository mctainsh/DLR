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
/// Where the live ride map is pointing, and what keeps it there.
/// <para>
/// Two behaviours, one state. The map re-opens on the ground it was left on — leaving for the
/// info page or the marker composer and coming straight back is the commonest thing a rider
/// does mid-ride, and re-panning at the side of a road is the cost of getting it wrong. And
/// "follow me" keeps this rider centred as their fixes arrive, which is the other half of not
/// having to touch the map while riding.
/// </para>
/// <para>
/// The map is the real <c>RideMap</c> behind <see cref="StubRideMap"/>: its viewport plumbing
/// and its camera are exactly what is under test, and only its pixels are impossible here.
/// </para>
/// </summary>
public sealed class GroupRideLiveViewTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
	private static readonly Guid MeId = Guid.Parse("11111111-1111-1111-1111-111111111111");

	/// <summary>Where a ride opens when the device has nothing stored — the page's own default.</summary>
	private static readonly MapCamera DefaultCamera = new(-33.868, 151.209, 11);

	private readonly FakeMapInterop _map = new();
	private readonly InMemoryDeviceSettings _settings = new();
	private FakeTimeProvider _clock = default!;

	private async Task<(FakeApiClient api, FakeRideHubClient hub, Guid rideId)> WireServicesAsync(
		IReadOnlyList<RiderPositionDto>? positions = null)
	{
		Guid rideId = Guid.NewGuid();
		FakeApiClient api = new()
		{
			PositionsResult = positions ?? Array.Empty<RiderPositionDto>(),
			RideResult = new RideDetail(
				Id: rideId,
				Name: "Test ride",
				Description: null,
				StartUtc: FixedInstant,
				State: RideStateDto.Live,
				JoinPolicy: JoinPolicyDto.Open,
				MemberCap: 50,
				MemberCount: 1,
				IsOrganiser: false,
				JoinCode: null,
				Permissions: new RidePermissions(),
				Members: [new RideMemberSummary(MeId, "Me", "Rider", FixedInstant, true, true)]),
		};

		FakeRideHubClient hub = new();
		FakeTokenStore tokens = new();
		_clock = new FakeTimeProvider(FixedInstant);
		AuthState auth = new(api, tokens, _clock);

		// Following centres on *this* rider's fix, which the page finds by matching
		// AuthState.UserId against the ride's positions. Without a session there is no user id
		// and the mode would quietly do nothing.
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

		ComponentFactories.Add<RideMap, StubRideMap>();

		return (api, hub, rideId);
	}

	private IRenderedComponent<GroupRideLive> RenderRide(Guid rideId) =>
		Render<GroupRideLive>(parameters => parameters.Add(p => p.RideId, rideId));

	/// <summary>
	/// Every camera the map has been pointed at, in order: the one it was opened on, then every
	/// move since. Which of the two carries a restored view depends on whether the device store
	/// or the ride's snapshot answered first, and a rider does not care which won.
	/// </summary>
	private IReadOnlyList<MapCamera> CamerasAskedFor() =>
		[.. (_map.LastOptions is { } options ? new[] { options.Camera } : []), .. _map.Cameras];

	private static async Task ChooseFollowMeAsync(IRenderedComponent<GroupRideLive> component)
	{
		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find("button.hamburger").Click());

		await component.InvokeAsync(() => component.FindAll(".menu button")
			.First(button => button.TextContent.Contains("Follow me", StringComparison.Ordinal))
			.Click());
	}

	private static RiderPositionDto At(Guid userId, double latitude, double longitude) =>
		new(userId, "Me", PositionScale.FromDegrees(latitude), PositionScale.FromDegrees(longitude),
			SpeedMps: 8, HeadingDeg: 90, FixedInstant);

	// ---------- Re-opening on the ground it was left on ----------

	[Fact]
	public async Task TheMapReopensOnTheViewItWasLastLeftOn()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		await _settings.SetAsync(
			LiveMapView.StorageKey,
			new LiveMapView(rideId, -37.8136, 144.9631, 15, FollowMe: false).Encode());

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(() =>
			CamerasAskedFor().ShouldContain(
				camera => Math.Abs(camera.Latitude - -37.8136) < 1e-4
					&& Math.Abs(camera.Longitude - 144.9631) < 1e-4
					&& Math.Abs(camera.ZoomLevel - 15) < 1e-6,
				"the rider left this map looking at Melbourne; re-opening it anywhere else means " +
				"panning back at the side of a road."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task AViewStoredForAnotherRide_DoesNotMoveThisRidesMap()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		await _settings.SetAsync(
			LiveMapView.StorageKey,
			new LiveMapView(Guid.NewGuid(), -37.8136, 144.9631, 15, FollowMe: false).Encode());

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		CamerasAskedFor().ShouldAllBe(
			camera => Math.Abs(camera.Latitude - DefaultCamera.Latitude) < 1e-6,
			"a stored view names one ride — applying another's would open this ride over the " +
			"wrong city entirely.");
	}

	[Fact]
	public async Task PanningTheMap_IsRememberedForTheNextTimeItOpens()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		// Past the throttle window that the base map's own opening frame has already used up —
		// this is a rider panning some seconds into the ride, not part of the same drag.
		_clock.Advance(TimeSpan.FromMinutes(1));

		// The base map reporting a view is the only thing that says where the rider panned to.
		await component.InvokeAsync(() => _map.RaiseViewport(new MapViewport(
			TopLeftLatitude: -37.80, TopLeftLongitude: 144.95,
			BottomRightLatitude: -37.83, BottomRightLongitude: 144.98,
			ZoomLevel: 15, HeadingDeg: 0,
			CanvasWidthPx: 800, CanvasHeightPx: 600, DevicePixelRatio: 1)));

		component.WaitForAssertion(() =>
		{
			LiveMapView? stored = LiveMapView.Decode(
				_settings.GetAsync(LiveMapView.StorageKey).GetAwaiter().GetResult());

			stored.ShouldNotBeNull();
			stored.RideId.ShouldBe(rideId);
			stored.Latitude.ShouldBe(-37.815, tolerance: 0.01);
			stored.Longitude.ShouldBe(144.965, tolerance: 0.01);
			stored.ZoomLevel.ShouldBe(15, tolerance: 1e-6);
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// A drag reports a viewport per frame and the device store is behind JS interop on the web,
	/// so the writes are throttled. What must not happen is the throttle eating the last one: the
	/// view the rider leaves on screen is precisely the view they expect back.
	/// </summary>
	[Fact]
	public async Task TheLastPanBeforeLeaving_IsStoredEvenThoughWritesAreThrottled()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => _map.RaiseViewport(new MapViewport(
			-37.80, 144.95, -37.83, 144.98, 15, 0, 800, 600, 1)));

		// A second pan a moment later — inside the throttle window, so it is held rather than
		// written. Nothing has advanced the clock, which is exactly the "still dragging" case.
		await component.InvokeAsync(() => _map.RaiseViewport(new MapViewport(
			-27.45, 153.01, -27.48, 153.04, 16, 0, 800, 600, 1)));

		await component.InvokeAsync(() => component.Instance.DisposeAsync().AsTask());

		LiveMapView? stored = LiveMapView.Decode(await _settings.GetAsync(LiveMapView.StorageKey));

		stored.ShouldNotBeNull();
		stored.Latitude.ShouldBe(-27.465, tolerance: 0.02,
			customMessage: "leaving the page has to flush whatever the throttle was holding.");
	}

	// ---------- Follow me (§5.3) ----------

	[Fact]
	public async Task TheMenu_OffersFollowMe()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find("button.hamburger").Click());

		component.Find(".menu").TextContent.ShouldContain("Follow me");
		component.FindAll("[role=menuitemcheckbox]").ShouldNotBeEmpty(
			"it is a mode that outlives the menu, not a one-shot action — the role has to say so.");
	}

	[Fact]
	public async Task ChoosingFollowMe_CentresTheMapOnThisRidersOwnPosition()
	{
		(_, _, Guid rideId) = await WireServicesAsync([At(MeId, -37.8136, 144.9631)]);

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		await ChooseFollowMeAsync(component);

		component.WaitForAssertion(() =>
		{
			MapCamera latest = _map.Cameras[^1];
			latest.Latitude.ShouldBe(-37.8136, tolerance: 1e-4,
				customMessage: "switching it on has to act now — a rider who has just panned away " +
				"is asking to be brought back, not to wait for the next fix.");
			latest.Longitude.ShouldBe(144.9631, tolerance: 1e-4);
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task WhileFollowing_EachNewFixMovesTheCamera()
	{
		(_, FakeRideHubClient hub, Guid rideId) = await WireServicesAsync([At(MeId, -37.8136, 144.9631)]);

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		await ChooseFollowMeAsync(component);

		await component.InvokeAsync(() => hub.RaisePositionsUpdated(new PositionBatch(rideId,
		[
			new PositionFix(MeId, PositionScale.FromDegrees(-37.82), PositionScale.FromDegrees(144.97),
				SpeedMps: 8, HeadingDeg: 90, FixedInstant),
		])));

		component.WaitForAssertion(() =>
		{
			MapCamera latest = _map.Cameras[^1];
			latest.Latitude.ShouldBe(-37.82, tolerance: 1e-4,
				customMessage: "§5.3: the whole of the mode is that the rider stays on screen as " +
				"their fixes arrive.");
			latest.Longitude.ShouldBe(144.97, tolerance: 1e-4);
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// The rider's zoom is theirs. Following is a promise about the centre of the screen, and a
	/// map that reset the zoom once a second would be unusable.
	/// </summary>
	[Fact]
	public async Task Following_KeepsTheZoomTheRiderChose()
	{
		(_, _, Guid rideId) = await WireServicesAsync([At(MeId, -37.8136, 144.9631)]);

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => _map.RaiseViewport(new MapViewport(
			-37.80, 144.95, -37.83, 144.98, ZoomLevel: 17, HeadingDeg: 0,
			CanvasWidthPx: 800, CanvasHeightPx: 600, DevicePixelRatio: 1)));

		await ChooseFollowMeAsync(component);

		component.WaitForAssertion(
			() => _map.Cameras[^1].ZoomLevel.ShouldBe(17),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task WithoutFollowing_ANewFixLeavesTheCameraWhereTheRiderPutIt()
	{
		(_, FakeRideHubClient hub, Guid rideId) = await WireServicesAsync([At(MeId, -37.8136, 144.9631)]);

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		int before = _map.Cameras.Count;

		await component.InvokeAsync(() => hub.RaisePositionsUpdated(new PositionBatch(rideId,
		[
			new PositionFix(MeId, PositionScale.FromDegrees(-37.82), PositionScale.FromDegrees(144.97),
				SpeedMps: 8, HeadingDeg: 90, FixedInstant),
		])));

		_map.Cameras.Count.ShouldBe(before,
			"a map nobody asked to follow must not move under somebody reading it.");
	}

	/// <summary>
	/// Positions arrive about once a second whether or not anybody has moved. Re-sending the same
	/// camera each time is a JS interop call a second for a map already where it should be.
	/// </summary>
	[Fact]
	public async Task AStationaryRider_DoesNotCostACameraMovePerFix()
	{
		(_, FakeRideHubClient hub, Guid rideId) = await WireServicesAsync([At(MeId, -37.8136, 144.9631)]);

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		await ChooseFollowMeAsync(component);

		component.WaitForAssertion(() => _map.Cameras.ShouldNotBeEmpty(), timeout: TimeSpan.FromSeconds(3));
		int afterSwitchingOn = _map.Cameras.Count;

		for (int tick = 0; tick < 3; tick++)
		{
			await component.InvokeAsync(() => hub.RaisePositionsUpdated(new PositionBatch(rideId,
			[
				new PositionFix(MeId, PositionScale.FromDegrees(-37.8136), PositionScale.FromDegrees(144.9631),
					SpeedMps: 0, HeadingDeg: null, FixedInstant),
			])));
		}

		_map.Cameras.Count.ShouldBe(afterSwitchingOn);
	}

	[Fact]
	public async Task FollowMe_IsRememberedAcrossTheTripToAnotherScreen()
	{
		(_, _, Guid rideId) = await WireServicesAsync([At(MeId, -37.8136, 144.9631)]);

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		await ChooseFollowMeAsync(component);

		component.WaitForAssertion(() =>
		{
			LiveMapView? stored = LiveMapView.Decode(
				_settings.GetAsync(LiveMapView.StorageKey).GetAwaiter().GetResult());

			stored.ShouldNotBeNull();
			stored.FollowMe.ShouldBeTrue(
				"the mode is set from a menu the rider then closes; it has to survive them going " +
				"to the info page and back.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task ADeviceThatLeftFollowingOn_OpensTheMapFollowing()
	{
		(_, _, Guid rideId) = await WireServicesAsync([At(MeId, -37.8136, 144.9631)]);

		// Stored against another ride on purpose: the camera belongs to one ride, but "keep me on
		// screen" is how this rider likes to be ridden with and carries into the next one.
		await _settings.SetAsync(
			LiveMapView.StorageKey,
			new LiveMapView(Guid.NewGuid(), -33.868, 151.209, 11, FollowMe: true).Encode());

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(() =>
		{
			_map.Cameras.ShouldNotBeEmpty();
			_map.Cameras[^1].Latitude.ShouldBe(-37.8136, tolerance: 1e-4);
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// Not sharing, or sharing with no fix yet, means the ride has no position for this rider.
	/// The mode is still on — it simply has nothing to point at, and says so rather than looking
	/// broken.
	/// </summary>
	[Fact]
	public async Task Following_WithNoFixOfOurOwn_SaysSoAndLeavesTheMapAlone()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		await ChooseFollowMeAsync(component);

		int moves = _map.Cameras.Count;

		await component.InvokeAsync(() => component.Find("button.hamburger").Click());

		component.Find("[role=menuitemcheckbox]").GetAttribute("aria-checked").ShouldBe("true");
		component.Find("[role=menuitemcheckbox]").TextContent.ShouldContain("waiting for a fix");
		_map.Cameras.Count.ShouldBe(moves, "there is nowhere to centre on yet.");
	}
}
