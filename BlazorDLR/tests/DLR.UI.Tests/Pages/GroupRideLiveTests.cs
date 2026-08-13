using BlazorDLR.Shared.Components;
using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The live ride page, which is now a full-screen map and a hamburger.
/// <para>
/// Everything the rider might want while riding is behind that one button: the ride's
/// info page, the marker composer, the ride thread, and the marker list. What these tests
/// pin down is that the map is not competing with any of it for space — nothing but the
/// map and the hamburger is on screen until somebody asks — and that the menu still obeys
/// §5.8's content permissions.
/// </para>
/// <para>
/// The panels that used to sit beside the map (members, gaps, join code, sharing switch,
/// organiser controls) moved to <see cref="GroupRideInfo"/>; their tests moved with them
/// to <c>GroupRideInfoTests</c>.
/// </para>
/// </summary>
public sealed class GroupRideLiveTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	private (FakeApiClient api, FakeRideHubClient hub, Guid rideId) WireServices(
		RideStateDto state = RideStateDto.Open,
		bool isOrganiser = false,
		RidePermissions? permissions = null)
	{
		Guid rideId = Guid.NewGuid();
		FakeApiClient api = new()
		{
			RideResult = new RideDetail(
				Id: rideId,
				Name: "Test ride",
				Description: null,
				StartUtc: FixedInstant,
				State: state,
				JoinPolicy: JoinPolicyDto.Open,
				MemberCap: 50,
				MemberCount: 1,
				IsOrganiser: isOrganiser,
				JoinCode: isOrganiser ? "AB3K9Z" : null,
				Permissions: permissions ?? new RidePermissions(),
				Members: Array.Empty<RideMemberSummary>()),
		};

		FakeRideHubClient hub = new();
		FakeTokenStore tokens = new();
		FakeTimeProvider clock = new(FixedInstant);
		AuthState auth = new(api, tokens, clock);

		Services.AddSingleton<IApiClient>(api);
		Services.AddSingleton<IRideHubClient>(hub);
		Services.AddSingleton<ITokenStore>(tokens);
		Services.AddSingleton<TimeProvider>(clock);
		Services.AddSingleton(auth);
		Services.AddSingleton<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(auth);
		Services.AddSingleton<ConfirmService>();
		Services.AddRealAuthorizationPipeline();
		this.CascadeAuthenticationState(auth);

		// GroupRideLive hosts RideMap → SkiaMapOverlay, which needs a JS-bound canvas.
		// Force the map into its stated-error branch so Skia never mounts.
		Services.AddSingleton<IMapInterop>(new FakeMapInterop
		{
			InitException = new InvalidOperationException("Test host — map interop is stubbed."),
		});

		// The rider's own private area is drawn on this map (§10.1), so the page injects the
		// state that holds it. In-memory stand-in for the device store, as everywhere else.
		Services.AddSingleton<IDeviceSettings, InMemoryDeviceSettings>();
		Services.AddSingleton<PrivateAreaState>();

		// Opening a ride is what the nav rail's globe remembers (§18.6), so the page writes here.
		Services.AddSingleton<CurrentRideState>();

		// The GPS seam (§4.3). The fake provider stands in for the phone's receiver; a page test
		// never emits a fix, so this only has to resolve — turning sharing on is what would start
		// it, and LocationBroadcastStateTests is where that path is exercised.
		Services.AddSingleton<ILocationProvider, FakeLocationProvider>();
		Services.AddSingleton<GpsProfileState>();
		Services.AddSingleton<LocationBroadcastState>();

		return (api, hub, rideId);
	}

	/// <summary>Waits for the snapshot to land, then opens the hamburger.</summary>
	private static async Task OpenMenuAsync(IRenderedComponent<GroupRideLive> component)
	{
		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find("button.hamburger").Click());
	}

	[Fact]
	public void BeforeTheMenuIsOpened_NothingButTheMapAndTheHamburgerIsOnScreen()
	{
		(_, _, Guid rideId) = WireServices();

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		component.FindAll(".menu").ShouldBeEmpty("the menu is closed until it is asked for.");
		component.FindAll(".markers-dialog").ShouldBeEmpty(
			"the marker list is a popup now — a permanent panel is exactly the screen space " +
			"a full-screen map was meant to win back.");
	}

	[Fact]
	public void OpeningARide_IsWhereTheRailsGlobeLeadsBackTo()
	{
		// Every way into a ride — a code redeemed, a row in the list, a shared link — ends on this
		// page, so this is the one place that has to remember (§18.6). The state persists through
		// the device store, which is what carries it over a restart.
		(_, _, Guid rideId) = WireServices();

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		CurrentRideState current = Services.GetRequiredService<CurrentRideState>();

		component.WaitForAssertion(
			() => current.Href.ShouldBe($"group-rides/{rideId}"),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void ARideThatIsGone_IsForgotten()
	{
		// A ride that was deleted, or one this rider has been removed from — §5.2 makes both a 404,
		// because a non-member's answer must not say whether the ride exists. Either way the globe
		// must stop leading to a page that can only answer "no such ride".
		(FakeApiClient api, _, Guid rideId) = WireServices();
		api.RideException = new ApiException(new ApiError(
			StatusCode: System.Net.HttpStatusCode.NotFound,
			Title: "That ride no longer exists.",
			Messages: Array.Empty<string>()));

		CurrentRideState current = Services.GetRequiredService<CurrentRideState>();

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(
			() => component.Find(".error").TextContent.ShouldContain("no longer exists"),
			timeout: TimeSpan.FromSeconds(3));

		component.WaitForAssertion(() =>
		{
			current.RideId.ShouldBeNull();
			current.Href.ShouldBe("group-rides", "with nothing to go back to, the globe means \"pick one\".");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task BeingRemovedWhileTheMapIsOpen_ForgetsTheRide_AndSaysSo()
	{
		// §5.2: an organiser can remove a member. It can land mid-ride, not only between loads —
		// the map on screen is then a picture of a ride whose every next request will 404.
		(_, FakeRideHubClient hub, Guid rideId) = WireServices();

		// The event names a user id, so "it was me" needs a session — without one the page would
		// read the removal as somebody else's and the test would pass for the wrong reason.
		Guid me = Guid.NewGuid();
		await Services.GetRequiredService<AuthState>().ApplySessionAsync(new TokenResponse(
			AccessToken: "access",
			ExpiresIn: 900,
			RefreshToken: "refresh",
			User: new AuthenticatedUser(me, "Me", HasEmail: true, EmailConfirmed: true)));

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		CurrentRideState current = Services.GetRequiredService<CurrentRideState>();
		current.RideId.ShouldBe(rideId, "opening the ride is what remembers it.");

		await component.InvokeAsync(() => hub.RaiseMemberLeft(rideId, me));

		component.WaitForAssertion(() =>
		{
			component.Find(".error").TextContent.ShouldContain("no longer on this ride",
				customMessage: "a map that carried on drawing would be a ride they are not on.");
			current.RideId.ShouldBeNull();
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task ARideThatOnlyFailedToLoad_IsStillRemembered()
	{
		// A tunnel, a captive portal, a server restart. The rider who most needs the globe is the
		// one whose phone has just lost signal in the middle of a ride, so a load that failed with
		// no statement from the server must not cost them the ride they are on.
		(FakeApiClient api, _, Guid rideId) = WireServices();
		api.RideException = new ApiException(new ApiError(
			StatusCode: System.Net.HttpStatusCode.ServiceUnavailable,
			Title: "The server is not answering.",
			Messages: Array.Empty<string>()));

		CurrentRideState current = Services.GetRequiredService<CurrentRideState>();
		await current.SetAsync(rideId);

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(
			() => component.Find(".error").TextContent.ShouldContain("not answering"),
			timeout: TimeSpan.FromSeconds(3));

		current.RideId.ShouldBe(rideId);
	}

	[Fact]
	public async Task TheMenu_ListsTheRidesActions()
	{
		(_, _, Guid rideId) = WireServices();

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		await OpenMenuAsync(component);

		string menu = component.Find(".menu").TextContent;
		menu.ShouldContain("Info");
		menu.ShouldContain("Add marker");
		menu.ShouldContain("Ride thread");
		menu.ShouldContain("Markers");
	}

	[Fact]
	public async Task ChoosingInfo_NavigatesToTheRidesInfoPage()
	{
		(_, _, Guid rideId) = WireServices();

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		await OpenMenuAsync(component);

		await component.InvokeAsync(() => component.FindAll(".menu button")
			.First(b => b.TextContent.Contains("Info", StringComparison.Ordinal))
			.Click());

		Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri
			.ShouldEndWith($"/group-rides/{rideId}/info",
				customMessage: "Info is a page about the ride, not a panel over the map.");
	}

	[Fact]
	public async Task ChoosingAddMarker_ArmsTheMap_RatherThanOpeningTheComposer()
	{
		(_, _, Guid rideId) = WireServices();

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		string before = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri;

		await OpenMenuAsync(component);

		await component.InvokeAsync(() => component.FindAll(".menu button")
			.First(b => b.TextContent.Contains("Add marker", StringComparison.Ordinal))
			.Click());

		component.WaitForAssertion(() =>
		{
			component.Find(".placing").TextContent.ShouldContain("Tap the map",
				customMessage: "§16.1: the point is the rider's to choose, so the item arms the map and " +
				"says so — an armed map that looks exactly like an unarmed one is a screen that has " +
				"quietly stopped answering taps the way it did a second ago.");
			component.FindAll(".menu").ShouldBeEmpty("choosing an item closes the menu behind it.");
		}, timeout: TimeSpan.FromSeconds(3));

		Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri.ShouldBe(before,
			"Nothing navigates until the point exists — the composer used to open on the centre of " +
			"the view, which is a guess, and the middle of the screen is exactly where the rider is.");
	}

	[Fact]
	public async Task CancellingPlacement_TakesTheStripAwayAgain()
	{
		(_, _, Guid rideId) = WireServices();

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		await OpenMenuAsync(component);

		await component.InvokeAsync(() => component.FindAll(".menu button")
			.First(b => b.TextContent.Contains("Add marker", StringComparison.Ordinal))
			.Click());

		component.WaitForAssertion(() => component.FindAll(".placing").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find(".placing button").Click());

		component.WaitForAssertion(() =>
			component.FindAll(".placing").ShouldBeEmpty(
				"A mode has to have a way out that is not 'place a marker you no longer want'."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task ChoosingMarkers_OpensTheMarkerPanelAsAPopup()
	{
		(_, _, Guid rideId) = WireServices();

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		await OpenMenuAsync(component);

		await component.InvokeAsync(() => component.FindAll(".menu button")
			.First(b => b.TextContent.Contains("Markers", StringComparison.Ordinal))
			.Click());

		component.WaitForAssertion(() =>
		{
			component.Find(".markers-dialog").TextContent.ShouldContain("Nothing marked on this ride yet");
			component.FindAll(".menu").ShouldBeEmpty("choosing an item closes the menu behind it.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task PermissionsChanged_HidesTheAddMarkerItem_ForOrdinaryMember()
	{
		RidePermissions initial = new(AllowMemberMarkers: true, AllowMemberComments: true, AllowMemberPhotos: true);
		(_, FakeRideHubClient hub, Guid rideId) = WireServices(isOrganiser: false, permissions: initial);

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		await OpenMenuAsync(component);

		component.Find(".menu").TextContent.ShouldContain("Add marker",
			customMessage: "with markers allowed, an ordinary member can place one.");

		RidePermissions revoked = initial with { AllowMemberMarkers = false };
		await component.InvokeAsync(() => hub.RaisePermissionsChanged(rideId, revoked));

		component.WaitForAssertion(() =>
		{
			component.Find(".menu").TextContent.ShouldNotContain("Add marker",
				customMessage: "§5.8: PermissionsChanged with markers off must remove the item — " +
				"leaving it there means a compose screen that ends in a 403.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void ConsentPrompt_IsShownWhenNotSharing_AndRideIsOpen()
	{
		(_, _, Guid rideId) = WireServices(state: RideStateDto.Open);

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Share your location with Test ride", StringComparison.Ordinal).ShouldBeTrue(
				"§5.6: opening the ride when not already sharing prompts for consent — the prompt " +
				"stays on the map page because that is where the rider lands.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task ConsentShare_CallsSetSharingWithTrue()
	{
		(FakeApiClient api, _, Guid rideId) = WireServices(state: RideStateDto.Open);

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.FindAll("button.primary").Any(b => b.TextContent.Contains("Share", StringComparison.Ordinal))
				.ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.FindAll("button.primary")
			.First(b => b.TextContent.Contains("Share", StringComparison.Ordinal))
			.Click());

		component.WaitForAssertion(() => api.SetSharingRequests.ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));
		api.SetSharingRequests.Last().Request.Share.ShouldBeTrue(
			"§5.6: tapping Share on the consent prompt sends SetSharing(true).");
		api.SetSharingRequests.Last().RideId.ShouldBe(rideId);
	}

	[Fact]
	public void MarkerAdded_HubEvent_DoesNotThrow()
	{
		(_, FakeRideHubClient hub, Guid rideId) = WireServices();

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		// The MapMarker is drawn on the Skia overlay (which we've stubbed) — the property
		// under test is that the hub event is handled without throwing. Missing this handler
		// would break the live-marker path silently.
		MarkerDto marker = new(
			Id: Guid.NewGuid(),
			TrackId: null,
			GroupRideId: rideId,
			Lat: -33_868_000,
			Lon: 151_209_000,
			Icon: "gravel",
			Title: "Loose gravel",
			Note: "Whole corner",
			DirectionDeg: null,
			PhotoId: null,
			CreatedByUserId: Guid.NewGuid(),
			CreatedByUserName: "Alice",
			CreatedUtc: FixedInstant,
			UpdatedUtc: FixedInstant);

		Should.NotThrow(() =>
			component.InvokeAsync(() => hub.RaiseMarkerAdded(rideId, marker)).GetAwaiter().GetResult());
	}

	// ---------- How riders reach the overlay (§16.3) ----------

	/// <summary>
	/// The overlay draws a rider's own colour, and needs their speed to decide between the heading
	/// arrow and the stopped dot. Neither travels with a position fix — the colour comes off the
	/// member row and the speed off the fix — so this is the join that has to be right.
	/// </summary>
	[Fact]
	public void ARidersMarker_CarriesTheirColourFromTheMemberList_AndTheirSpeedFromTheFix()
	{
		Guid riderId = Guid.NewGuid();
		(FakeApiClient api, _, Guid rideId) = WireServices();

		api.RideResult = api.RideResult! with
		{
			Members = [new RideMemberSummary(riderId, "DaveSmith", "Rider", FixedInstant, true, true, "#16a34a")],
		};

		api.PositionsResult =
		[
			new RiderPositionDto(riderId, "DaveSmith", -33_868_000, 151_209_000, SpeedMps: 8, HeadingDeg: 90, FixedInstant),
		];

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
		{
			MapMarker drawn = component.FindComponent<RideMap>().Instance.Markers!.Values
				.Single(marker => marker.Kind == MarkerKind.Rider);

			drawn.Colour.ShouldBe("#16a34a",
				"the colour is on the member row, not the fix — the map has to look it up per rider.");
			drawn.SpeedMps.ShouldBe(8);
			drawn.DirectionDeg.ShouldBe(90);
			drawn.Title.ShouldBe("DaveSmith", "§7.2: the pin carries the username.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// A hub batch carries no username (§5.3) — the client is expected to already have it. Before
	/// this, a rider whose first sighting was a batch rather than the snapshot was drawn as "?".
	/// </summary>
	[Fact]
	public async Task ARiderFirstSeenOnTheHub_IsStillNamedFromTheMemberList()
	{
		Guid riderId = Guid.NewGuid();
		(FakeApiClient api, FakeRideHubClient hub, Guid rideId) = WireServices();

		api.RideResult = api.RideResult! with
		{
			Members = [new RideMemberSummary(riderId, "DaveSmith", "Rider", FixedInstant, true, false)],
		};

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => hub.RaisePositionsUpdated(new PositionBatch(rideId,
		[
			new PositionFix(riderId, -33_868_000, 151_209_000, SpeedMps: 0, HeadingDeg: null, FixedInstant),
		])));

		component.WaitForAssertion(() =>
		{
			MapMarker drawn = component.FindComponent<RideMap>().Instance.Markers!.Values
				.Single(marker => marker.Kind == MarkerKind.Rider);

			drawn.Title.ShouldBe("DaveSmith");
			drawn.SpeedMps.ShouldBe(0, "a stopped rider is drawn as a dot, which needs the speed to have arrived.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	// ---------- The rider's own private area on the live map (§10.1) ----------

	[Fact]
	public void WithNoPrivateAreaSet_TheMapCarriesNoCircle()
	{
		(_, _, Guid rideId) = WireServices();

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.FindComponent<RideMap>().Instance.Circles.ShouldBeNull(
				"a device that never set an area draws nothing extra — the shipped state."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task APrivateAreaSetOnThisDevice_IsDrawnOnTheRideMap()
	{
		(_, _, Guid rideId) = WireServices();

		PrivateAreaState areas = Services.GetRequiredService<PrivateAreaState>();
		await areas.SetAsync(new PrivateArea(-33.868, 151.209, 1_500));

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
		{
			IReadOnlyList<MapCircle> circles = component.FindComponent<RideMap>().Instance.Circles!;
			circles.ShouldNotBeNull();
			circles.Count.ShouldBe(1);
			circles[0].Latitude.ShouldBe(-33.868, tolerance: 1e-6);
			circles[0].RadiusM.ShouldBe(1_500,
				"the circle on the ride map is the area that is actually in force, radius and all.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task MovingThePrivateArea_RedrawsTheRideMapWithoutAReload()
	{
		(_, _, Guid rideId) = WireServices();

		PrivateAreaState areas = Services.GetRequiredService<PrivateAreaState>();
		await areas.SetAsync(new PrivateArea(-33.868, 151.209, 1_000));

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(
			() => component.FindComponent<RideMap>().Instance.Circles.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));

		// The settings screen and this page share one scoped state, so a change made in another
		// tab of the same app has to land here without the rider reopening the ride.
		await component.InvokeAsync(() => areas.ClearAsync());

		component.WaitForAssertion(
			() => component.FindComponent<RideMap>().Instance.Circles.ShouldBeNull(
				"removing the area must take the circle off the map, not leave a stale one."),
			timeout: TimeSpan.FromSeconds(3));
	}

	// -- The screen lock (§4.3) -------------------------------------------------------------

	/// <summary>
	/// Registers a countable screen lock over <c>PageTestContext</c>'s stub, and hands it back so
	/// the test can read it.
	/// </summary>
	private FakeScreenWakeLock WireScreenWakeLock()
	{
		FakeScreenWakeLock wakeLock = new();
		Services.AddSingleton<IScreenWakeLock>(wakeLock);
		return wakeLock;
	}

	[Fact]
	public void TheLiveMap_HoldsTheScreenOn_WhileItIsThePage()
	{
		// The one thing a rider on a bar mount cannot do is tap the screen to wake it: gloves on,
		// at speed, both hands where they should be. The platform's idle timer measures input, and
		// reading a map produces none — so without this the map goes black mid-ride.
		(_, _, Guid rideId) = WireServices(state: RideStateDto.Live);
		FakeScreenWakeLock wakeLock = WireScreenWakeLock();

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(
			() => wakeLock.IsHeld.ShouldBeTrue("the live map holds the screen for as long as it is on screen."),
			timeout: TimeSpan.FromSeconds(3));

		wakeLock.RequestCount.ShouldBe(1,
			"taken once on first render — the map re-renders about once a second on positions alone, "
			+ "and a request per frame would be a platform call per frame.");
	}

	[Fact]
	public async Task LeavingTheLiveMap_GivesTheScreenBack()
	{
		(_, _, Guid rideId) = WireServices(state: RideStateDto.Live);
		FakeScreenWakeLock wakeLock = WireScreenWakeLock();

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(
			() => wakeLock.IsHeld.ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		// How this suite spells "the rider navigated away" — the router disposes the page.
		await component.InvokeAsync(() => component.Instance.DisposeAsync().AsTask());

		wakeLock.IsHeld.ShouldBeFalse(
			"a page that is no longer on screen must not go on holding the device awake — "
			+ "that is somebody's battery for a map nobody is reading.");
		wakeLock.ReleaseCount.ShouldBe(1, "released exactly once, matching the one request.");
	}

	[Fact]
	public async Task ARideThatNeverLoaded_StillGivesTheScreenBack()
	{
		// The lock is taken on first render, before the snapshot has landed and regardless of
		// whether it ever does. The pairing has to hold on that path too, or a ride opened in a
		// dead zone and abandoned leaves the screen pinned on until the app is killed.
		(FakeApiClient api, _, Guid rideId) = WireServices();
		api.RideException = new HttpRequestException("No such host is known.");

		FakeScreenWakeLock wakeLock = WireScreenWakeLock();

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(
			() => wakeLock.IsHeld.ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Instance.DisposeAsync().AsTask());

		wakeLock.IsHeld.ShouldBeFalse();
	}
}
