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
		IReadOnlyList<RiderPositionDto>? positions = null,
		RideStateDto state = RideStateDto.Live)
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
				State: state,
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

		// This device has already been shown Play's background-location disclosure. Without it the
		// receiver stops at a modal nothing here answers, and every test below that expects a fix
		// would be waiting on a dialog rather than on the GPS. Its own path is
		// LocationBroadcastStateTests' subject.
		await _settings.SetAsync(LocationBroadcastState.DisclosureStorageKey, "1");

		// Opening a ride is what the nav rail's globe remembers (§18.6), so the page writes here.
		Services.AddSingleton<CurrentRideState>();

		// The GPS seam (§4.3). Unlike the other page suites this one does drive it: the rider's own
		// mark and the follow camera both come off the device's fix before the ride has one
		// (§4.3, §5.3), which is a behaviour of this page rather than of the broadcaster.
		Services.AddSingleton<ILocationProvider, FakeLocationProvider>();
		Services.AddSingleton<GpsProfileState>();
		Services.AddSingleton<LocationBroadcastState>();

		ComponentFactories.Add<RideMap, StubRideMap>();

		return (api, hub, rideId);
	}

	/// <summary>This test's device GPS — the same instance the page's broadcaster is watching.</summary>
	private FakeLocationProvider Gps => (FakeLocationProvider)Services.GetRequiredService<ILocationProvider>();

	/// <summary>Waits for the page to have brought the receiver up, which sharing on a Live ride does.</summary>
	private void WaitForReceiver(IRenderedComponent<GroupRideLive> component) =>
		component.WaitForAssertion(
			() => Gps.WatchCount.ShouldBe(1, "sharing is on and the ride is Live, so the GPS runs."),
			timeout: TimeSpan.FromSeconds(3));

	private static LocationFix DeviceFix(
		double latitude,
		double longitude,
		double? speedMps = 8,
		double? headingDeg = 90) =>
		new(latitude, longitude, AccuracyM: 5, SpeedMps: speedMps, HeadingDeg: headingDeg, RecordedUtc: FixedInstant);

	/// <summary>
	/// Renders the ride and gets the device's receiver as far as one fix — which is where every
	/// follow-me test starts, because following aims at this phone's own reading and at nothing
	/// else (§4.3). Returns once that fix is on the map, so a test can act on it without racing
	/// the pump.
	/// </summary>
	private IRenderedComponent<GroupRideLive> RenderRideLocatedAt(Guid rideId, double latitude, double longitude)
	{
		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		WaitForReceiver(component);

		Gps.Emit(DeviceFix(latitude, longitude));

		component.WaitForAssertion(
			() => SelfMarkOn(component).ShouldNotBeNull("the device's fix has to have landed first."),
			timeout: TimeSpan.FromSeconds(3));

		return component;
	}

	/// <summary>This rider's own mark as the page has handed it to the map, or null when there is none.</summary>
	private static MapMarker? SelfMarkOn(IRenderedComponent<GroupRideLive> component) =>
		component.FindComponent<StubRideMap>().Instance.Markers?.Values
			.SingleOrDefault(marker => marker.Kind == MarkerKind.Self);

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
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

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
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await ChooseFollowMeAsync(component);

		Gps.Emit(DeviceFix(-37.82, 144.97));

		component.WaitForAssertion(
			() => SelfMarkOn(component)!.Latitude.ShouldBe(-37.82, tolerance: 1e-6),
			timeout: TimeSpan.FromSeconds(3));

		component.WaitForAssertion(() =>
		{
			MapCamera latest = _map.Cameras[^1];
			latest.Latitude.ShouldBe(-37.82, tolerance: 1e-4,
				customMessage: "§4.3: the whole of the mode is that the rider stays on screen as " +
				"their receiver produces fixes.");
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
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await component.InvokeAsync(() => _map.RaiseViewport(new MapViewport(
			-37.80, 144.95, -37.83, 144.98, ZoomLevel: 17, HeadingDeg: 0,
			CanvasWidthPx: 800, CanvasHeightPx: 600, DevicePixelRatio: 1)));

		await ChooseFollowMeAsync(component);

		component.WaitForAssertion(
			() => _map.Cameras[^1].ZoomLevel.ShouldBe(17),
			timeout: TimeSpan.FromSeconds(3));
	}

	// ---------- The rider's own mark, off this device rather than off the server ----------

	/// <summary>
	/// The bug this pair exists for: a rider with a GPS lock, on a ride the server has nothing for
	/// them in yet, saw an empty map and a follow mode that silently did nothing. Their pin is the
	/// far end of a round trip — the ride only carries positions once it is Live (§5.1), and the
	/// fan-out is a 5 s tick (§5.3) — and none of that should stand between somebody and seeing
	/// where they are.
	/// </summary>
	[Fact]
	public async Task TheDevicesOwnFix_PutsThisRiderOnTheMap_WithNothingBackFromTheRide()
	{
		// No positions at all: the server has never sent this rider's pin, which is the state
		// every ride is in for its first seconds and every un-started ride is in throughout.
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		WaitForReceiver(component);

		Gps.Emit(DeviceFix(-37.8136, 144.9631));

		component.WaitForAssertion(() =>
		{
			IReadOnlyDictionary<Guid, MapMarker>? drawn = component.FindComponent<StubRideMap>().Instance.Markers;

			drawn.ShouldNotBeNull();
			MapMarker self = drawn!.Values.ShouldHaveSingleItem();

			self.Kind.ShouldBe(MarkerKind.Self,
				"§4.3: the device's own read is drawn as itself, not as one of the ride's rider pins.");
			self.Latitude.ShouldBe(-37.8136, tolerance: 1e-6);
			self.Longitude.ShouldBe(144.9631, tolerance: 1e-6);
			self.Title.ShouldBeEmpty("no label — a rider does not need to be told their own name.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Following_CentresOnTheDevicesOwnFix_WithNothingBackFromTheRide()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		WaitForReceiver(component);

		Gps.Emit(DeviceFix(-37.8136, 144.9631));

		await ChooseFollowMeAsync(component);

		component.WaitForAssertion(() =>
		{
			MapCamera latest = _map.Cameras[^1];
			latest.Latitude.ShouldBe(-37.8136, tolerance: 1e-4,
				customMessage: "following waited on the server's copy of this rider, which on a ride " +
				"that has not started yet never arrives.");
			latest.Longitude.ShouldBe(144.9631, tolerance: 1e-4);
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// The overlay picks the arrowhead over the green dot on <c>DirectionDeg</c> alone (see
	/// <c>SkiaMapOverlay.DrawSelf</c>), so what matters on this side of the seam is that the
	/// receiver's answer reaches it unchanged — including its <em>absence</em>. A heading quietly
	/// defaulted to zero here would point a rider due north for as long as they sat still, which is
	/// the §8 mistake the whole null-versus-zero rule exists to stop.
	/// </summary>
	[Theory]
	[InlineData(90.0)]
	[InlineData(null)]
	public async Task TheReceiversHeading_ReachesTheOverlayExactlyAsItCame(double? headingDeg)
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		WaitForReceiver(component);

		// Speed held at a standstill for both, so nothing but the heading can decide the shape.
		Gps.Emit(DeviceFix(-37.8136, 144.9631, speedMps: 0, headingDeg: headingDeg));

		component.WaitForAssertion(() =>
		{
			IReadOnlyDictionary<Guid, MapMarker>? drawn = component.FindComponent<StubRideMap>().Instance.Markers;

			drawn.ShouldNotBeNull();
			MapMarker self = drawn!.Values.ShouldHaveSingleItem();

			self.Kind.ShouldBe(MarkerKind.Self);
			self.DirectionDeg.ShouldBe(headingDeg,
				"null is 'the receiver did not say', which is what selects the dot — never zero, " +
				"which is due north (§8).");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// The server's copy of this rider is never drawn — not "unless the device has something
	/// fresher", never. It is this phone's own fix after a round trip (§4.2, §5.3), so at best it
	/// is a worse copy of the mark already on screen and at worst it is a second mark for one
	/// person: at a standstill they sit on top of each other, and at speed they read as two riders
	/// a few seconds apart.
	/// </summary>
	[Fact]
	public async Task TheRidesOwnCopyOfThisRider_IsNeverDrawn_EvenWithADeviceFixInHand()
	{
		(_, _, Guid rideId) = await WireServicesAsync([At(MeId, -37.80, 144.95)]);

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		IReadOnlyDictionary<Guid, MapMarker>? drawn = component.FindComponent<StubRideMap>().Instance.Markers;

		drawn.ShouldNotBeNull();
		MapMarker mine = drawn!.Values.ShouldHaveSingleItem();

		mine.Kind.ShouldBe(MarkerKind.Self);
		mine.Latitude.ShouldBe(-37.8136, tolerance: 1e-6,
			"the device's fix is the current one — the ride's copy is up to a fan-out tick behind.");
	}

	/// <summary>
	/// The exception, and the only one: a host with no receiver of its own (§18.6). In a browser
	/// the server's copy is the only answer there has ever been, and refusing it would take a rider
	/// who is sharing from their phone off the map they are watching on a laptop. It is drawn as an
	/// ordinary rider pill, because that is honestly what it is — the same view of them everybody
	/// else on the ride has.
	/// </summary>
	[Fact]
	public async Task InABrowser_TheRidesCopyOfThisRider_IsDrawnAndFollowed()
	{
		(_, _, Guid rideId) = await WireServicesAsync([At(MeId, -37.8136, 144.9631)]);

		// What separates the web hosts from the phones, and the same flag the page reads.
		Gps.IsSupported = false;

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(() =>
		{
			IReadOnlyDictionary<Guid, MapMarker>? drawn = component.FindComponent<StubRideMap>().Instance.Markers;

			drawn.ShouldNotBeNull();
			MapMarker mine = drawn!.Values.ShouldHaveSingleItem();

			mine.Kind.ShouldBe(MarkerKind.Rider,
				"with no receiver there is nothing to draw a Self mark from — this is the server's " +
				"view of this rider, and it is drawn as one.");
			mine.Latitude.ShouldBe(-37.8136, tolerance: 1e-4);
		}, timeout: TimeSpan.FromSeconds(3));

		await ChooseFollowMeAsync(component);

		component.WaitForAssertion(
			() => _map.Cameras[^1].Latitude.ShouldBe(-37.8136, tolerance: 1e-4,
				customMessage: "a host that cannot read a GPS still has somewhere to point."),
			timeout: TimeSpan.FromSeconds(3));

		_map.LastOptions!.ShowUserLocation.ShouldBeTrue(
			"and the base map's own blue dot is asked for on exactly the host where it works.");
	}

	/// <summary>
	/// On a phone, still never drawn when there is nothing to replace it with. A receiver that is
	/// off, still warming up, or suppressed by the private area (§10.1) does not currently know
	/// where it is — which is a different thing from where it was when the server last heard, and
	/// the map must not answer the first question with the second.
	/// </summary>
	[Fact]
	public async Task TheRidesOwnCopyOfThisRider_IsNotDrawn_WhenThereIsNoDeviceFixEither()
	{
		(_, _, Guid rideId) = await WireServicesAsync(
		[
			At(MeId, -37.80, 144.95),
			At(Guid.NewGuid(), -37.81, 144.96),
		]);

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		// The other rider is what proves the snapshot landed — without them, an empty layer would
		// pass this test for the wrong reason.
		component.WaitForAssertion(() =>
		{
			IReadOnlyDictionary<Guid, MapMarker>? landed = component.FindComponent<StubRideMap>().Instance.Markers;

			landed.ShouldNotBeNull();
			landed!.Count.ShouldBe(1);
		}, timeout: TimeSpan.FromSeconds(3));

		IReadOnlyDictionary<Guid, MapMarker> drawn = component.FindComponent<StubRideMap>().Instance.Markers!;

		drawn.ShouldNotContainKey(MeId,
			"no device fix is not a reason to fall back to the round-tripped one.");
		drawn.Values.ShouldAllBe(marker => marker.Kind == MarkerKind.Rider);

		_map.LastOptions!.ShowUserLocation.ShouldBeFalse(
			"and the base map's blue dot is not asked for here either — inside a WebView it is a " +
			"permission gate neither MAUI host grants, so it would answer nothing at all.");
	}

	// ---------- A ride that has not started ----------

	/// <summary>
	/// The other half of the same bug. Sharing can be turned on for an <c>Open</c> ride — the
	/// consent prompt is shown for exactly that state — but <c>PositionStore.PublishAsync</c> keeps
	/// a fix only for a ride that is <c>Live</c> or inside a wind-down (§5.1). Running the GPS into
	/// that is a foreground service and a battery bill for fixes the server drops, under a screen
	/// that says "Sharing your location".
	/// </summary>
	[Fact]
	public async Task OnARideThatHasNotStarted_TheReceiverDoesNotRun_AndTheMapSaysWhy()
	{
		(_, _, Guid rideId) = await WireServicesAsync(state: RideStateDto.Open);

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() => component.Markup.ShouldContain("when the organiser starts the ride",
				customMessage: "a rider who consented, read the disclosure and granted the phone's " +
				"permission is owed the reason they are still not on the map."),
			timeout: TimeSpan.FromSeconds(3));

		Gps.WatchCount.ShouldBe(0,
			"nothing this device sent would be kept, so nothing is sent — and no foreground service runs.");
	}

	[Fact]
	public async Task WhenTheOrganiserStartsTheRide_TheReceiverComesUpOnItsOwn()
	{
		(_, FakeRideHubClient hub, Guid rideId) = await WireServicesAsync(state: RideStateDto.Open);

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		Gps.WatchCount.ShouldBe(0);

		await component.InvokeAsync(() => hub.RaiseRideStateChanged(rideId, RideStateDto.Live));

		component.WaitForAssertion(
			() => Gps.WatchCount.ShouldBe(1,
				"§5.1: consent was already given — the rider must not have to find the switch and " +
				"toggle it twice to get onto a map they already said yes to."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task WithoutFollowing_ANewFixLeavesTheCameraWhereTheRiderPutIt()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		int before = _map.Cameras.Count;

		Gps.Emit(DeviceFix(-37.82, 144.97));

		component.WaitForAssertion(
			() => SelfMarkOn(component)!.Latitude.ShouldBe(-37.82, tolerance: 1e-6),
			timeout: TimeSpan.FromSeconds(3));

		_map.Cameras.Count.ShouldBe(before,
			"a map nobody asked to follow must not move under somebody reading it — the mark moves, " +
			"the camera does not.");
	}

	/// <summary>
	/// Fixes arrive about once a second whether or not anybody has moved. Re-sending the same
	/// camera each time is a JS interop call a second for a map already where it should be.
	/// </summary>
	[Fact]
	public async Task AStationaryRider_DoesNotCostACameraMovePerFix()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await ChooseFollowMeAsync(component);

		component.WaitForAssertion(() => _map.Cameras.ShouldNotBeEmpty(), timeout: TimeSpan.FromSeconds(3));
		int afterSwitchingOn = _map.Cameras.Count;

		for (int tick = 0; tick < 3; tick++)
		{
			Gps.Emit(DeviceFix(-37.8136, 144.9631, speedMps: 0, headingDeg: null));
		}

		// Waited on the last of the three having been taken in, so this is not asserting on a
		// camera count that simply has not moved yet.
		component.WaitForAssertion(
			() => SelfMarkOn(component)!.DirectionDeg.ShouldBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		_map.Cameras.Count.ShouldBe(afterSwitchingOn);
	}

	[Fact]
	public async Task FollowMe_IsRememberedAcrossTheTripToAnotherScreen()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

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
		(_, _, Guid rideId) = await WireServicesAsync();

		// Stored against another ride on purpose: the camera belongs to one ride, but "keep me on
		// screen" is how this rider likes to be ridden with and carries into the next one.
		await _settings.SetAsync(
			LiveMapView.StorageKey,
			new LiveMapView(Guid.NewGuid(), -33.868, 151.209, 11, FollowMe: true).Encode());

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

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
