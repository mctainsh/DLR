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
		Services.AddSingleton<TrackRecordingState>();
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

	/// <summary>
	/// Turns following on or off the only way there is to: the button over the map. It briefly had
	/// a twin in the menu; that went once the button existed, because two controls for one mode is
	/// a mode a rider has to check twice.
	/// </summary>
	private static async Task PressFollowButtonAsync(IRenderedComponent<GroupRideLive> component)
	{
		component.WaitForAssertion(
			() => component.FindAll("button.follow").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find("button.follow").Click());
	}

	/// <summary>
	/// Flips the map between north-up and heading-up the only way there is to: the button over the
	/// map, beside the follow one. It lived under the rule in the menu until both camera modes were
	/// pulled out into the same stack — both keep moving the map after the tap that set them, so
	/// both are changed while riding and neither is reachable from two places.
	/// </summary>
	private static async Task ChooseMapOrientationAsync(IRenderedComponent<GroupRideLive> component)
	{
		component.WaitForAssertion(
			() => component.FindAll("button.heading-up").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find("button.heading-up").Click());
	}

	/// <summary>
	/// An element's attribute, or the empty string when it carries none — so a missing attribute
	/// fails the assertion it was written for rather than the overload resolution around it.
	/// </summary>
	private static string AttributeOn(IRenderedComponent<GroupRideLive> component, string selector, string name) =>
		component.Find(selector).GetAttribute(name) ?? string.Empty;

	/// <summary>
	/// What the map is currently telling the rider it has just started or stopped doing, or the
	/// empty string when it is saying nothing. Empty rather than null so "it said nothing" and "it
	/// said the wrong thing" are the same assertion shape.
	/// </summary>
	private static string ToastOn(IRenderedComponent<GroupRideLive> component) =>
		component.FindAll(".mode-toast").SingleOrDefault()?.TextContent ?? string.Empty;

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

	/// <summary>
	/// Following is set from the button on the map and from nowhere else. It had a twin under the
	/// rule in the menu while the button was new; keeping both would have left a rider checking two
	/// controls for one mode, and the one they would check is the one already on screen.
	/// </summary>
	[Fact]
	public async Task Following_IsSetFromTheMapAndNotFromTheMenu()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() => component.FindAll("button.follow").ShouldNotBeEmpty(
				"the one thing a rider does to this map while moving has a permanent target."),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find("button.hamburger").Click());

		component.Find(".menu").TextContent.ShouldNotContain("Follow me");
	}

	[Fact]
	public async Task ChoosingFollowMe_CentresTheMapOnThisRidersOwnPosition()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await PressFollowButtonAsync(component);

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

		await PressFollowButtonAsync(component);

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

		await PressFollowButtonAsync(component);

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

		await PressFollowButtonAsync(component);

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

		await PressFollowButtonAsync(component);

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

		await PressFollowButtonAsync(component);

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

		await PressFollowButtonAsync(component);

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

		await PressFollowButtonAsync(component);

		int moves = _map.Cameras.Count;

		component.Find("button.follow").GetAttribute("aria-pressed").ShouldBe("true");
		AttributeOn(component, "button.follow", "aria-label").ShouldContain("waiting for a fix",
			customMessage: "a lit button over a map that is not moving has to be able to say which " +
			"of the two it is.");
		_map.Cameras.Count.ShouldBe(moves, "there is nowhere to centre on yet.");
	}

	// ---------- The follow button over the map ----------

	/// <summary>
	/// Following is the one thing a rider does to this map while actually moving, so it has a
	/// permanent target on the map rather than a tap, a read and a second tap inside the menu.
	/// </summary>
	[Fact]
	public async Task TheFollowButton_CentresOnThisRiderAndSaysItIsFollowing()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		component.Find("button.follow").GetAttribute("aria-pressed").ShouldBe("false");

		await PressFollowButtonAsync(component);

		component.WaitForAssertion(() =>
		{
			_map.Cameras[^1].Latitude.ShouldBe(-37.8136, tolerance: 1e-4);
			component.Find("button.follow").GetAttribute("aria-pressed").ShouldBe("true");
			ToastOn(component).ShouldContain("Following you",
				customMessage: "a mode that keeps moving the map after the tap that set it has to " +
				"say out loud that it is on.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// Pressing it again turns following off, and the map stops moving under the rider — which is
	/// the whole of what "off" has to mean.
	/// </summary>
	[Fact]
	public async Task PressingTheFollowButtonAgain_TurnsFollowingOff()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await PressFollowButtonAsync(component);

		component.WaitForAssertion(
			() => component.Find("button.follow").GetAttribute("aria-pressed").ShouldBe("true"),
			timeout: TimeSpan.FromSeconds(3));

		await PressFollowButtonAsync(component);

		component.WaitForAssertion(() =>
		{
			component.Find("button.follow").GetAttribute("aria-pressed").ShouldBe("false");
			ToastOn(component).ShouldContain("Following off");
		}, timeout: TimeSpan.FromSeconds(3));

		// And the map has stopped moving under the rider, which is what "off" has to mean.
		int moves = _map.Cameras.Count;
		Gps.Emit(DeviceFix(-37.90, 145.05));

		component.WaitForAssertion(
			() => SelfMarkOn(component)!.Latitude.ShouldBe(-37.90, tolerance: 1e-6),
			timeout: TimeSpan.FromSeconds(3));

		_map.Cameras.Count.ShouldBe(moves);
	}

	// ---------- A hand on the map (§5.3) ----------

	/// <summary>
	/// The behaviour this whole gesture seam exists for. It used to be impossible: the viewport
	/// event says where the map is, never who put it there, so following could not tell a rider's
	/// drag from its own move and had to stay on — which meant a rider who panned away to look at
	/// a junction watched the map drag itself back a second later.
	/// </summary>
	[Fact]
	public async Task PanningTheMap_StopsFollowing_AndSaysWhy()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await PressFollowButtonAsync(component);

		component.WaitForAssertion(
			() => component.Find("button.follow").GetAttribute("aria-pressed").ShouldBe("true"),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => _map.RaiseGesture(MapGesture.Pan));

		component.WaitForAssertion(() =>
		{
			component.Find("button.follow").GetAttribute("aria-pressed").ShouldBe("false");
			ToastOn(component).ShouldContain("you moved the map",
				customMessage: "a mode that cancels itself silently is indistinguishable from an " +
				"app that has lost the setting.");
		}, timeout: TimeSpan.FromSeconds(3));

		int moves = _map.Cameras.Count;
		Gps.Emit(DeviceFix(-37.90, 145.05));

		component.WaitForAssertion(
			() => SelfMarkOn(component)!.Latitude.ShouldBe(-37.90, tolerance: 1e-6),
			timeout: TimeSpan.FromSeconds(3));

		_map.Cameras.Count.ShouldBe(moves, "the rider took the map; it stays where they left it.");
	}

	/// <summary>
	/// The cancellation is written to the device store at once rather than waiting on the throttled
	/// view save — a rider who pans and then closes the app inside the window would otherwise open
	/// the next ride with a mode they had already turned off.
	/// </summary>
	[Fact]
	public async Task APanThatStopsFollowing_IsRememberedImmediately()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await PressFollowButtonAsync(component);

		component.WaitForAssertion(() =>
			LiveMapView.Decode(_settings.GetAsync(LiveMapView.StorageKey).GetAwaiter().GetResult())
				?.FollowMe.ShouldBe(true),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => _map.RaiseGesture(MapGesture.Pan));

		component.WaitForAssertion(() =>
			LiveMapView.Decode(_settings.GetAsync(LiveMapView.StorageKey).GetAwaiter().GetResult())
				?.FollowMe.ShouldBe(false),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// A pan with nothing following is just a rider using the map. There is nothing to announce,
	/// and a toast on every drag would be the screen shouting at somebody reading it.
	/// </summary>
	[Fact]
	public async Task PanningWithNothingFollowing_SaysNothing()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await component.InvokeAsync(() => _map.RaiseGesture(MapGesture.Pan));

		ToastOn(component).ShouldBeEmpty();
	}

	/// <summary>
	/// A mode change is a sentence about what just happened, not a strip that stays. The standing
	/// statement of the mode is the button's own state.
	/// </summary>
	[Fact]
	public async Task TheToast_TakesItselfOffTheScreen()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await PressFollowButtonAsync(component);

		component.WaitForAssertion(
			() => ToastOn(component).ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		_clock.Advance(TimeSpan.FromSeconds(10));

		component.WaitForAssertion(() =>
		{
			ToastOn(component).ShouldBeEmpty();
			component.Find("button.follow").GetAttribute("aria-pressed").ShouldBe("true",
				"the message goes; the mode it described does not.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	// ---------- Which way up the map is drawn ----------

	/// <summary>
	/// Heading-up is a button over the map beside the follow one, and is not in the menu at all.
	/// Both camera modes keep moving the map after the tap that set them, so both are reached and
	/// read while riding; the menu is left holding the things that are done at a stop.
	/// </summary>
	[Fact]
	public async Task HeadingUp_IsAButtonOverTheMap_AndNotAMenuItem()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(() =>
		{
			component.FindAll("button.heading-up").Count.ShouldBe(1,
				"one control for one mode — a mode with two is a mode a rider has to check twice.");
			AttributeOn(component, "button.heading-up", "aria-pressed").ShouldBe("false",
				"a toggle button and not a menu item, so the state a screen reader reads out is " +
				"aria-pressed rather than aria-checked.");
		}, timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find("button.hamburger").Click());

		component.Find(".menu").TextContent.ShouldNotContain("North up");
		component.FindAll(".menu .heading-up").ShouldBeEmpty(
			"it went out to the stack; leaving a copy behind is the copy that goes stale.");
	}

	/// <summary>
	/// The hamburger carried a dot for whichever mode was still set from inside the menu. Nothing is
	/// any more — both modes are lit buttons beside it — and a second indicator for a state a button
	/// already shows is the one that goes stale.
	/// </summary>
	[Fact]
	public async Task TheHamburger_CarriesNoModeDot()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await PressFollowButtonAsync(component);

		component.WaitForAssertion(() =>
		{
			component.Find("button.follow").GetAttribute("aria-pressed").ShouldBe("true");
			component.FindAll("button.hamburger.turning").ShouldBeEmpty();
		}, timeout: TimeSpan.FromSeconds(3));

		await ChooseMapOrientationAsync(component);

		component.WaitForAssertion(() =>
		{
			component.Find("button.heading-up").GetAttribute("aria-pressed").ShouldBe("true",
				"the button's own lit state is what says the map is turning.");
			component.FindAll("button.hamburger.turning").ShouldBeEmpty(
				"the dot is gone; the hamburger says nothing about either mode now.");
			AttributeOn(component, "button.hamburger", "aria-label").ShouldBe("Ride actions",
				"one label, because it no longer has a second state to describe.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// Lit is the whole of the standing statement, so the button has to change with the mode. Colour
	/// is not the only cue — the glyph swaps too, because colour alone is the first thing to go
	/// through a visor in daylight (§18.6).
	/// </summary>
	[Fact]
	public async Task TheHeadingUpButton_LightsUpWithTheMode()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await ChooseMapOrientationAsync(component);

		component.WaitForAssertion(() =>
		{
			component.FindAll("button.heading-up.on").ShouldNotBeEmpty();
			component.Find("button.heading-up i").GetAttribute("class").ShouldContain("fa-compass");
		}, timeout: TimeSpan.FromSeconds(3));

		await ChooseMapOrientationAsync(component);

		component.WaitForAssertion(() =>
		{
			component.FindAll("button.heading-up.on").ShouldBeEmpty();
			component.Find("button.heading-up").GetAttribute("aria-pressed").ShouldBe("false");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task ChoosingHeadingUp_TurnsTheMapToThisRidersHeading()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		// The helper's fix carries a heading of 90° at 8 m/s — moving, so the heading is a
		// direction rather than the noise between two readings of a parked bike.
		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await ChooseMapOrientationAsync(component);

		component.WaitForAssertion(() =>
		{
			_map.Cameras[^1].HeadingDeg.ShouldBe(90, tolerance: 1e-6);
			ToastOn(component).ShouldContain("Heading up");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// A map rotating about a point the rider is not standing on swings the ground around nobody,
	/// which is worse than north-up rather than better — so choosing heading-up brings following
	/// with it rather than handing over half a mode and a second control to find.
	/// </summary>
	[Fact]
	public async Task ChoosingHeadingUp_SwitchesFollowingOnWithIt()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		component.Find("button.follow").GetAttribute("aria-pressed").ShouldBe("false");

		await ChooseMapOrientationAsync(component);

		component.WaitForAssertion(() =>
		{
			component.Find("button.follow").GetAttribute("aria-pressed").ShouldBe("true");

			MapCamera latest = _map.Cameras[^1];
			latest.HeadingDeg.ShouldBe(90, tolerance: 1e-6);
			latest.Latitude.ShouldBe(-37.8136, tolerance: 1e-4,
				"turned *and* centred, in the one camera — a rider who chose this from off in a " +
				"corner is asking to be brought back now, not at the next fix.");

			ToastOn(component).ShouldContain("centred on you",
				customMessage: "the rider asked for one of the two modes and got both; the toast " +
				"has to name the one they did not ask for.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// The other end of the pairing, and the one that makes it an invariant rather than a nicety:
	/// heading-up cannot outlive following by any route, so the button that stops one stops both.
	/// </summary>
	[Fact]
	public async Task TurningFollowingOff_TakesHeadingUpWithIt()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		// One tap sets both — see ChoosingHeadingUp_SwitchesFollowingOnWithIt.
		await ChooseMapOrientationAsync(component);

		component.WaitForAssertion(() =>
		{
			component.Find("button.follow").GetAttribute("aria-pressed").ShouldBe("true");
			component.FindAll("button.heading-up.on").ShouldNotBeEmpty();
		}, timeout: TimeSpan.FromSeconds(3));

		await PressFollowButtonAsync(component);

		component.WaitForAssertion(() =>
		{
			component.Find("button.follow").GetAttribute("aria-pressed").ShouldBe("false");
			component.FindAll("button.heading-up.on").ShouldBeEmpty(
				"the map rotates about the centre of the screen, and the rider has just stopped " +
				"being it.");
			ToastOn(component).ShouldBe("Following and heading up off.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// Stopping following is not a request for north. The rider pressed one button; straightening
	/// the map on top of it would be a second thing they did not ask for, and the base map's own
	/// compass is already a permanent control for exactly that.
	/// </summary>
	[Fact]
	public async Task TurningFollowingOff_LeavesTheMapAtTheBearingItWasOn()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await ChooseMapOrientationAsync(component);

		component.WaitForAssertion(
			() => _map.Cameras[^1].HeadingDeg.ShouldBe(90, tolerance: 1e-6),
			timeout: TimeSpan.FromSeconds(3));

		int moves = _map.Cameras.Count;

		await PressFollowButtonAsync(component);

		component.WaitForAssertion(
			() => ToastOn(component).ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		_map.Cameras.Count.ShouldBe(moves,
			"stopping two modes moves nothing — neither of them is driving the camera any more.");
	}

	/// <summary>
	/// Only on the way on, and only as a starting state. Turning heading-up back off is a statement
	/// about the bearing and nothing else — a rider who wanted to stop being followed has a button
	/// for it.
	/// </summary>
	[Fact]
	public async Task ChoosingNorthUpAgain_LeavesFollowingWhereItIs()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await ChooseMapOrientationAsync(component);

		component.WaitForAssertion(
			() => component.Find("button.follow").GetAttribute("aria-pressed").ShouldBe("true"),
			timeout: TimeSpan.FromSeconds(3));

		await ChooseMapOrientationAsync(component);

		component.WaitForAssertion(() =>
		{
			_map.Cameras[^1].HeadingDeg.ShouldBe(0, tolerance: 1e-6);
			component.Find("button.follow").GetAttribute("aria-pressed").ShouldBe("true");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task WhileHeadingUp_ANewHeadingTurnsTheMapWithIt()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await ChooseMapOrientationAsync(component);

		component.WaitForAssertion(
			() => _map.Cameras[^1].HeadingDeg.ShouldBe(90, tolerance: 1e-6),
			timeout: TimeSpan.FromSeconds(3));

		Gps.Emit(DeviceFix(-37.82, 144.97, headingDeg: 180));

		// Waited on the fix having been taken in before the camera is read, the same way the
		// follow-me tests above do: the mark is rebuilt and rendered on the way to the camera
		// being aimed, so this is what says the aim has happened rather than a race with it.
		component.WaitForAssertion(
			() => SelfMarkOn(component)!.DirectionDeg.ShouldBe(180),
			timeout: TimeSpan.FromSeconds(3));

		component.WaitForAssertion(
			() => _map.Cameras[^1].HeadingDeg.ShouldBe(180, tolerance: 1e-6,
				customMessage: "the whole of the mode is that the ground turns as the rider does."),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// A GPS heading is derived from successive fixes, so at a standstill it is the noise between
	/// two readings of a bike leaning against a fence — arriving once a second. Honouring it would
	/// spin the map under a rider stopped at a light, which is exactly when they are reading it.
	/// </summary>
	[Fact]
	public async Task AtAStandstill_TheMapHoldsItsBearingRatherThanFollowingTheNoise()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await ChooseMapOrientationAsync(component);

		component.WaitForAssertion(
			() => _map.Cameras[^1].HeadingDeg.ShouldBe(90, tolerance: 1e-6),
			timeout: TimeSpan.FromSeconds(3));

		int turns = _map.Cameras.Count;

		// Stopped, and the receiver still reporting a wildly different heading — which is the
		// steady state of a parked bike, not a rider who has turned around.
		Gps.Emit(DeviceFix(-37.8136, 144.9631, speedMps: 0, headingDeg: 275));

		component.WaitForAssertion(
			() => SelfMarkOn(component)!.DirectionDeg.ShouldBe(275),
			timeout: TimeSpan.FromSeconds(3));

		_map.Cameras.Count.ShouldBe(turns, "the mark may point wherever the fix says; the map holds.");
	}

	/// <summary>
	/// A turn is the rider stating which way up they want the map, and the base map's compass says
	/// the same thing — MapLibre's NavigationControl resets north through the same gesture path, so
	/// the two arrive here as one event and cancel the mode together.
	/// </summary>
	[Fact]
	public async Task TurningTheMapYourself_StopsHeadingUp_AndSaysWhy()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await ChooseMapOrientationAsync(component);

		component.WaitForAssertion(
			() => _map.Cameras[^1].HeadingDeg.ShouldBe(90, tolerance: 1e-6),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => _map.RaiseGesture(MapGesture.Rotate));

		component.WaitForAssertion(
			() => ToastOn(component).ShouldContain("you turned the map yourself"),
			timeout: TimeSpan.FromSeconds(3));

		// Following is still on — choosing heading-up switched it on, and a turn is not a statement
		// about it — so fixes go on moving the camera. What must stop is the *bearing* being driven.
		Gps.Emit(DeviceFix(-37.82, 144.97, headingDeg: 180));

		component.WaitForAssertion(
			() => SelfMarkOn(component)!.Latitude.ShouldBe(-37.82, tolerance: 1e-6),
			timeout: TimeSpan.FromSeconds(3));

		component.WaitForAssertion(() =>
		{
			MapCamera latest = _map.Cameras[^1];
			latest.Latitude.ShouldBe(-37.82, tolerance: 1e-4);
			latest.HeadingDeg.ShouldBe(0, tolerance: 1e-6,
				"a rider who has just set the bearing by hand must not have it taken back off " +
				"them — the camera carries the map's own bearing, it does not drive one.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// Cancelling by gesture does not straighten the map. The rider has just said where they want
	/// the bearing — by twisting it there, or by tapping the compass, which has already turned the
	/// map to north itself — and a camera move on top of that is the app arguing with them.
	/// </summary>
	[Fact]
	public async Task AGestureThatStopsHeadingUp_DoesNotMoveTheCameraItself()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await ChooseMapOrientationAsync(component);

		component.WaitForAssertion(
			() => _map.Cameras[^1].HeadingDeg.ShouldBe(90, tolerance: 1e-6),
			timeout: TimeSpan.FromSeconds(3));

		int moves = _map.Cameras.Count;

		await component.InvokeAsync(() => _map.RaiseGesture(MapGesture.Rotate));

		component.WaitForAssertion(
			() => ToastOn(component).ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		_map.Cameras.Count.ShouldBe(moves);
	}

	/// <summary>
	/// Pressing the button back to "north up" <em>is</em> a camera move, unlike the gesture path
	/// above: it is the rider asking for the other orientation, not stating a bearing of their own.
	/// </summary>
	[Fact]
	public async Task ChoosingNorthUp_TurnsTheMapBackToNorth()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await ChooseMapOrientationAsync(component);

		component.WaitForAssertion(
			() => _map.Cameras[^1].HeadingDeg.ShouldBe(90, tolerance: 1e-6),
			timeout: TimeSpan.FromSeconds(3));

		await ChooseMapOrientationAsync(component);

		component.WaitForAssertion(() =>
		{
			_map.Cameras[^1].HeadingDeg.ShouldBe(0, tolerance: 1e-6);
			ToastOn(component).ShouldContain("North up");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// The half of the asymmetry that leaves a mode standing. A twist says which way up the map
	/// should be and nothing about where its centre is, so the rider goes on being carried along
	/// with the group — a north-up map that keeps them on screen is an ordinary thing to want.
	/// </summary>
	[Fact]
	public async Task ATurnTakesTheBearingBackAndLeavesFollowingAlone()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await PressFollowButtonAsync(component);
		await ChooseMapOrientationAsync(component);

		component.WaitForAssertion(
			() => _map.Cameras[^1].HeadingDeg.ShouldBe(90, tolerance: 1e-6),
			timeout: TimeSpan.FromSeconds(3));

		// A turn takes the bearing back and leaves following alone.
		await component.InvokeAsync(() => _map.RaiseGesture(MapGesture.Rotate));

		component.WaitForAssertion(
			() => component.Find("button.follow").GetAttribute("aria-pressed").ShouldBe("true"),
			timeout: TimeSpan.FromSeconds(3));

		Gps.Emit(DeviceFix(-37.82, 144.97, headingDeg: 180));

		component.WaitForAssertion(
			() => SelfMarkOn(component)!.Latitude.ShouldBe(-37.82, tolerance: 1e-6),
			timeout: TimeSpan.FromSeconds(3));

		component.WaitForAssertion(() =>
		{
			_map.Cameras[^1].Latitude.ShouldBe(-37.82, tolerance: 1e-4,
				"following survived a gesture that was not about it.");
			_map.Cameras[^1].HeadingDeg.ShouldBe(0, tolerance: 1e-6,
				"and the bearing is the map's own now — carried from the viewport, not driven.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// The other half. A drag says where the centre of the screen is, and heading-up cannot survive
	/// losing that: the map rotates about the centre, so a turning map the rider is no longer at
	/// the middle of swings the ground around a point that is nobody. Both modes go, in one
	/// sentence — the same fact that makes choosing heading-up switch following on, backwards.
	/// </summary>
	[Fact]
	public async Task APanTakesBothModesOff_AndNamesThemBoth()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		// Choosing heading-up brings following with it, so this one tap sets both.
		await ChooseMapOrientationAsync(component);

		component.WaitForAssertion(() =>
		{
			component.Find("button.follow").GetAttribute("aria-pressed").ShouldBe("true");
			component.FindAll("button.heading-up.on").ShouldNotBeEmpty();
		}, timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => _map.RaiseGesture(MapGesture.Pan));

		component.WaitForAssertion(() =>
		{
			component.Find("button.follow").GetAttribute("aria-pressed").ShouldBe("false");
			component.FindAll("button.heading-up.on").ShouldBeEmpty(
				"a turning map the rider is no longer the centre of is worse than a north-up one.");
			ToastOn(component).ShouldContain("Following and heading up off",
				customMessage: "two modes stopped on one gesture, so one sentence names both — a " +
				"rider who reads only half of it goes looking for a fault in the other half.");
		}, timeout: TimeSpan.FromSeconds(3));

		// And the map is left exactly where the rider dragged it to, at the bearing they left it
		// on: cancelling is not a licence to move the camera.
		int moves = _map.Cameras.Count;

		Gps.Emit(DeviceFix(-37.90, 145.05, headingDeg: 200));

		component.WaitForAssertion(
			() => SelfMarkOn(component)!.Latitude.ShouldBe(-37.90, tolerance: 1e-6),
			timeout: TimeSpan.FromSeconds(3));

		_map.Cameras.Count.ShouldBe(moves, "neither mode is driving anything any more.");
	}

	/// <summary>
	/// A pan with heading-up on but following already off — reachable from a device that stored
	/// that pair, and from the follow button. The sentence has to name the one mode that stopped
	/// rather than a rider's own following, which was never on.
	/// </summary>
	[Fact]
	public async Task APanWithOnlyHeadingUpOn_NamesOnlyHeadingUp()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		await _settings.SetAsync(
			LiveMapView.StorageKey,
			new LiveMapView(Guid.NewGuid(), -33.868, 151.209, 11, FollowMe: false, HeadingUp: true).Encode());

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		component.WaitForAssertion(
			() => component.FindAll("button.heading-up.on").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		component.Find("button.follow").GetAttribute("aria-pressed").ShouldBe("false");

		await component.InvokeAsync(() => _map.RaiseGesture(MapGesture.Pan));

		component.WaitForAssertion(() =>
		{
			component.FindAll("button.heading-up.on").ShouldBeEmpty();
			ToastOn(component).ShouldBe("Heading up off — you moved the map.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task HeadingUp_IsRememberedAcrossTheTripToAnotherScreen()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		await ChooseMapOrientationAsync(component);

		component.WaitForAssertion(() =>
		{
			LiveMapView? stored = LiveMapView.Decode(
				_settings.GetAsync(LiveMapView.StorageKey).GetAwaiter().GetResult());

			stored.ShouldNotBeNull();
			stored.HeadingUp.ShouldBeTrue(
				"which way up somebody reads a map is a standing preference, not one ride's ground.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task ADeviceThatLeftTheMapTurning_OpensItTurning()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		await _settings.SetAsync(
			LiveMapView.StorageKey,
			new LiveMapView(Guid.NewGuid(), -33.868, 151.209, 11, FollowMe: false, HeadingUp: true).Encode());

		IRenderedComponent<GroupRideLive> component = RenderRideLocatedAt(rideId, -37.8136, 144.9631);

		component.WaitForAssertion(() =>
		{
			_map.Cameras.ShouldNotBeEmpty();
			_map.Cameras[^1].HeadingDeg.ShouldBe(90, tolerance: 1e-6);
		}, timeout: TimeSpan.FromSeconds(3));

		ToastOn(component).ShouldBeEmpty(
			"restoring a mode the rider set earlier is not a change of mind — a map that announced " +
			"its own settings on every open would be one more thing to dismiss per ride.");
	}
}
