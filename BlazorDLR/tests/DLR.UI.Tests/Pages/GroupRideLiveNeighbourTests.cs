using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
using DLR.Core.Tracks;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The panel over the live map naming who is just up the road and just behind (§5.4).
/// <para>
/// Which four riders and in what order is <c>NeighbourList</c>'s, and how a row is drawn is
/// <c>NeighbourPanel</c>'s; both are tested where they live. What is left for the page is the
/// wiring only the page has: that the panel is measured from <em>this device's</em> fix rather
/// than from the ride's round-tripped copy of it, that the hamburger can take it off and put it
/// back, and that the choice survives leaving the map - which is the commonest thing a rider does
/// mid-ride.
/// </para>
/// </summary>
public sealed class GroupRideLiveNeighbourTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
	private static readonly Guid MeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly Guid BobId = Guid.Parse("22222222-2222-2222-2222-222222222222");

	private const double BaseLat = -33.852;
	private const double BaseLon = 151.211;

	private readonly FakeMapInterop _map = new();
	private readonly InMemoryDeviceSettings _settings = new();

	/// <summary>
	/// A ride with a route running due east from the anchor, this rider and one other. A straight
	/// line so "distance along" is a number the assertions can state rather than whatever a curve
	/// happened to integrate to.
	/// </summary>
	private async Task<(FakeApiClient api, FakeRideHubClient hub, Guid rideId)> WireServicesAsync(bool withRoute = true)
	{
		Guid rideId = Guid.NewGuid();
		FakeApiClient api = new()
		{
			RideResult = new RideDetail(
				Id: rideId,
				Name: "Test adventure",
				Description: null,
				StartUtc: FixedInstant,
				JoinPolicy: JoinPolicyDto.Open,
				MemberCap: 50,
				MemberCount: 2,
				IsOrganiser: false,
				JoinCode: null,
				Permissions: new RidePermissions(),
				Members:
				[
					new RideMemberSummary(MeId, "Me", "Rider", FixedInstant, true, true),
					new RideMemberSummary(BobId, "Bob", "Rider", FixedInstant, true, true),
				]),
		};

		if (withRoute)
		{
			api.RoutesResult.Add(new RideRoute(
				TrackId: Guid.NewGuid(),
				Name: "The way out",
				DistanceM: 4_600,
				PointCount: 2,
				EncodedPolyline: PolylineCodec.EncodePoints(
					[new TrackPoint(BaseLat, BaseLon), new TrackPoint(BaseLat, BaseLon + 0.05)]),
				Bounds: null,
				AddedUtc: FixedInstant,
				AddedByUserId: MeId,
				AddedByUserName: "Me"));
		}

		FakeRideHubClient hub = new();
		FakeTokenStore tokens = new();
		FakeTimeProvider clock = new(FixedInstant);
		AuthState auth = new(api, tokens, clock);

		// The panel is measured from this rider, which the page finds by matching AuthState.UserId
		// against the ride's members. Without a session there is no reader and no "ahead".
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

		Services.AddSingleton<IMapInterop>(_map);
		Services.AddSingleton<IDeviceSettings>(_settings);
		Services.AddSingleton<RouteStyleState>();
		Services.AddSingleton<PrivateAreaState>();
		Services.AddSingleton<CurrentRideState>();

		// Play's background-location disclosure, already answered - without it the receiver stops at
		// a modal nothing here presses, and this rider never gets a fix of their own.
		await _settings.SetAsync(LocationDisclosure.StorageKey, "1");

		// The GPS seam (§4.3). This page measures the panel from the device's own reading, so unlike
		// most page suites this one has to drive a real receiver.
		Services.AddSingleton<ILocationProvider, FakeLocationProvider>();
		Services.AddSingleton<LocationUpdateRateState>();
		Services.AddSingleton<TrackRecordingState>();
		Services.AddSingleton<LocationDisclosure>();
		Services.AddSingleton<LocationBroadcastState>();

		ComponentFactories.Add<BlazorDLR.Shared.Components.RideMap, StubRideMap>();

		return (api, hub, rideId);
	}

	private FakeLocationProvider Gps => (FakeLocationProvider)Services.GetRequiredService<ILocationProvider>();

	private IRenderedComponent<GroupRideLive> RenderRide(Guid rideId) =>
		Render<GroupRideLive>(parameters => parameters.Add(p => p.RideId, rideId));

	/// <summary>
	/// A point that many metres east of the anchor. The route runs due east, so this is also that
	/// rider's distance along it.
	/// </summary>
	private static double LonAt(double metresEast) =>
		BaseLon + (metresEast / (111_320.0 * Math.Cos(BaseLat * Math.PI / 180.0)));

	private static RiderPositionDto Fix(Guid id, string name, double metresEast) =>
		new(id, name, PositionScale.FromDegrees(BaseLat), PositionScale.FromDegrees(LonAt(metresEast)),
			null, null, FixedInstant);

	/// <summary>
	/// Renders the ride and gets the device's receiver as far as one fix, which is where the panel
	/// starts: it is measured from this phone's own reading and from nothing else.
	/// </summary>
	private async Task<IRenderedComponent<GroupRideLive>> RenderRideLocatedAtAsync(Guid rideId, double metresEast)
	{
		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		// Polled rather than waited on through the renderer - the watch starts after the last
		// render the page has any reason to do. See BackgroundWait.
		await BackgroundWait.UntilAsync(
			() => Gps.WatchCount == 1,
			"the receiver to start - sharing is on and the adventure is Live, so the GPS runs");

		Gps.Emit(new LocationFix(
			BaseLat, LonAt(metresEast), AccuracyM: 5, SpeedMps: 8, HeadingDeg: 90, RecordedUtc: FixedInstant));

		return component;
	}

	private static async Task OpenMenuAsync(IRenderedComponent<GroupRideLive> component)
	{
		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find("button.hamburger").Click());
	}

	private static async Task ToggleNeighboursAsync(IRenderedComponent<GroupRideLive> component)
	{
		await OpenMenuAsync(component);
		await component.InvokeAsync(() => component.Find("[role=menuitemcheckbox].neighbours").Click());
	}

	// -- What it draws ------------------------------------------------------------------------

	[Fact]
	public async Task ARiderUpTheRoad_IsNamedWithTheirGapAlongIt()
	{
		(FakeApiClient api, _, Guid rideId) = await WireServicesAsync();
		api.PositionsResult = [Fix(BobId, "Bob", 1_400)];

		IRenderedComponent<GroupRideLive> component = await RenderRideLocatedAtAsync(rideId, metresEast: 1_000);

		component.WaitForAssertion(
			() =>
			{
				string panel = component.Find(".live-neighbours").TextContent;
				panel.ShouldContain("Bob");
				panel.ShouldContain("+", Case.Insensitive);
			},
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// The panel is on by default, which is the opposite of how the two camera modes arrive and
	/// deliberately so: those move the map under a rider who did not ask, and this covers a corner of
	/// it and takes no tap. A read-out nobody knows exists is a read-out nobody turns on.
	/// </summary>
	[Fact]
	public async Task OnADeviceThatHasNeverChosen_ThePanelIsOn()
	{
		(FakeApiClient api, _, Guid rideId) = await WireServicesAsync();
		api.PositionsResult = [Fix(BobId, "Bob", 1_400)];

		IRenderedComponent<GroupRideLive> component = await RenderRideLocatedAtAsync(rideId, metresEast: 1_000);

		component.WaitForAssertion(
			() => component.FindAll(".live-neighbours").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// "Along the route" needs a route. A ride with none has nothing to put in the panel, and an
	/// empty box over the map is a strip of ground covered for no reason.
	/// </summary>
	[Fact]
	public async Task OnARideWithNoRoute_NothingIsDrawn()
	{
		(FakeApiClient api, _, Guid rideId) = await WireServicesAsync(withRoute: false);
		api.PositionsResult = [Fix(BobId, "Bob", 1_400)];

		IRenderedComponent<GroupRideLive> component = await RenderRideLocatedAtAsync(rideId, metresEast: 1_000);

		component.WaitForAssertion(
			() => component.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		component.FindAll(".live-neighbours").ShouldBeEmpty();
	}

	/// <summary>
	/// A rider's own place on the route comes off the device's GPS, never off the ride's copy of it.
	/// The copy is this phone's own fix after a filter, a fan-out tick and a round trip (§4.2, §5.3),
	/// and measuring everybody else against it would show the group drifting forward past somebody
	/// holding a steady wheel.
	/// </summary>
	[Fact]
	public async Task TheGapsAreMeasuredFromThisDevicesFix_NotTheRidesCopyOfIt()
	{
		(FakeApiClient api, _, Guid rideId) = await WireServicesAsync();
		api.PositionsResult =
		[
			Fix(BobId, "Bob", 2_500),
			// The ride still has this rider back at the start line.
			Fix(MeId, "Me", 0),
		];

		// The device knows better: they are 1 000 m up the road, so Bob is 1.5 km ahead of them and
		// not the 2.5 km the ride's copy of this rider would make it.
		IRenderedComponent<GroupRideLive> component = await RenderRideLocatedAtAsync(rideId, metresEast: 1_000);

		component.WaitForAssertion(
			() => component.Find(".live-neighbours").TextContent.ShouldContain("+ 1.5 km"),
			timeout: TimeSpan.FromSeconds(3));
	}

	// -- The switch ---------------------------------------------------------------------------

	[Fact]
	public async Task TheMenu_TakesThePanelOff_AndPutsItBack()
	{
		(FakeApiClient api, _, Guid rideId) = await WireServicesAsync();
		api.PositionsResult = [Fix(BobId, "Bob", 1_400)];

		IRenderedComponent<GroupRideLive> component = await RenderRideLocatedAtAsync(rideId, metresEast: 1_000);

		component.WaitForAssertion(
			() => component.FindAll(".live-neighbours").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await ToggleNeighboursAsync(component);

		component.WaitForAssertion(
			() =>
			{
				component.FindAll(".live-neighbours").ShouldBeEmpty();
				component.FindAll(".menu").ShouldBeEmpty("choosing an item closes the menu behind it.");
			},
			timeout: TimeSpan.FromSeconds(3));

		await ToggleNeighboursAsync(component);

		component.WaitForAssertion(
			() => component.FindAll(".live-neighbours").ShouldNotBeEmpty(
				"switching it back on fills it now, rather than waiting on the next position batch - "
				+ "on a group standing at the meeting point that batch may be a while."),
			timeout: TimeSpan.FromSeconds(3));
	}

	/// <summary>
	/// The switch says which way it is set while the menu is open, in the attribute a screen reader
	/// reads out - the panel behind the menu is what says it once the menu has closed.
	/// </summary>
	[Fact]
	public async Task TheSwitch_CarriesItsStateAsAChecked()
	{
		(_, _, Guid rideId) = await WireServicesAsync();

		IRenderedComponent<GroupRideLive> component = RenderRide(rideId);

		await OpenMenuAsync(component);

		component.Find("[role=menuitemcheckbox].neighbours").GetAttribute("aria-checked").ShouldBe("true");

		await component.InvokeAsync(() => component.Find("[role=menuitemcheckbox].neighbours").Click());
		await OpenMenuAsync(component);

		component.Find("[role=menuitemcheckbox].neighbours").GetAttribute("aria-checked").ShouldBe("false");
	}

	/// <summary>
	/// Leaving the map for the info page, the thread or the marker composer and coming straight back
	/// is the commonest thing a rider does mid-ride. A preference that has to be set again each time
	/// is one that gets set once and then endured.
	/// </summary>
	[Fact]
	public async Task TurningItOff_SurvivesLeavingTheMap()
	{
		(FakeApiClient api, _, Guid rideId) = await WireServicesAsync();
		api.PositionsResult = [Fix(BobId, "Bob", 1_400)];

		IRenderedComponent<GroupRideLive> component = await RenderRideLocatedAtAsync(rideId, metresEast: 1_000);

		component.WaitForAssertion(
			() => component.FindAll(".live-neighbours").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await ToggleNeighboursAsync(component);

		component.WaitForAssertion(
			() => component.FindAll(".live-neighbours").ShouldBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(component.Instance.DisposeAsync().AsTask);

		// The same device, opening the same ride again.
		IRenderedComponent<GroupRideLive> reopened = await RenderRideLocatedAtAsync(rideId, metresEast: 1_000);

		reopened.WaitForAssertion(
			() => reopened.FindAll("button.hamburger").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		reopened.FindAll(".live-neighbours").ShouldBeEmpty(
			"a traveller who turned it off must not find it back the next time they open the map.");
	}
}
