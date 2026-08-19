using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Rides;
using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// "Live members" — the rail's rider list (§5.3, §5.4, §5.6).
/// <para>
/// It reads the same <c>RideSession</c> the live map and the info page do, so what these tests
/// pin down is §5.3's rule applied to this screen: the snapshot is authoritative and the hub is
/// the delta on top. A member joining, leaving or flipping their sharing has to land here
/// without a refetch — the tests for the first two moved from <c>GroupRideInfoTests</c> along
/// with the list itself.
/// </para>
/// <para>
/// The row arithmetic belongs to <c>MemberRoster</c> and the row markup to
/// <c>LiveMemberList</c>; both are tested in their own suites.
/// </para>
/// </summary>
public sealed class RideMembersLiveTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	private const double BaseLat = -33.852;
	private const double BaseLon = 151.211;

	private (FakeApiClient api, FakeRideHubClient hub, Guid rideId) WireServices(
		RideStateDto state = RideStateDto.Live,
		IReadOnlyList<RideMemberSummary>? members = null)
	{
		Guid rideId = Guid.NewGuid();
		FakeApiClient api = new()
		{
			RideResult = new RideDetail(
				Id: rideId,
				Name: "Test adventure",
				Description: null,
				StartUtc: FixedInstant,
				State: state,
				JoinPolicy: JoinPolicyDto.Open,
				MemberCap: 50,
				MemberCount: members?.Count ?? 1,
				IsOrganiser: false,
				JoinCode: null,
				Permissions: new RidePermissions(),
				Members: members ?? [new RideMemberSummary(Guid.NewGuid(), "Me", "Rider", FixedInstant)]),
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

		// Reversing a route changes which end "along" is measured from (§5.4), so this page reads
		// the same device-local preference the info page writes.
		Services.AddSingleton<IDeviceSettings, InMemoryDeviceSettings>();
		Services.AddSingleton<RouteStyleState>();

		Services.AddRealAuthorizationPipeline();
		this.CascadeAuthenticationState(auth);

		return (api, hub, rideId);
	}

	private IRenderedComponent<RideMembersLive> RenderMembers(Guid rideId) =>
		Render<RideMembersLive>(parameters => parameters.Add(p => p.RideId, rideId));

	private static RiderPositionDto Fix(Guid id, string name, double lat, double lon, DateTimeOffset? recordedUtc = null) =>
		new(id, name, PositionScale.FromDegrees(lat), PositionScale.FromDegrees(lon), null, null,
			recordedUtc ?? FixedInstant);

	/// <summary>A route running due east from the anchor, as the ride's route endpoint describes it.</summary>
	private static RideRoute EastwardRoute() => new(
		TrackId: Guid.NewGuid(),
		Name: "The way out",
		DistanceM: 4_600,
		PointCount: 2,
		EncodedPolyline: PolylineCodec.EncodePoints(
			[new TrackPoint(BaseLat, BaseLon), new TrackPoint(BaseLat, BaseLon + 0.05)]),
		Bounds: null,
		AddedUtc: FixedInstant,
		AddedByUserId: Guid.NewGuid(),
		AddedByUserName: "DaveSmith");

	[Fact]
	public void TheRidesMembers_AreListed_WithTheirState()
	{
		Guid bob = Guid.NewGuid();
		(FakeApiClient api, _, Guid rideId) = WireServices(members:
		[
			new RideMemberSummary(Guid.NewGuid(), "Me", "Rider", FixedInstant),
			new RideMemberSummary(bob, "Bob", "Rider", FixedInstant, Sharing: true, HasPosition: true),
		]);

		api.PositionsResult = [Fix(bob, "Bob", BaseLat, BaseLon)];

		IRenderedComponent<RideMembersLive> component = RenderMembers(rideId);

		component.WaitForAssertion(() =>
		{
			component.FindAll(".live-members li").Count.ShouldBe(2);
			component.Markup.ShouldContain("Bob");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void TheWayBack_IsTheMap()
	{
		// The map is what this screen is read alongside — a rider opens the list, finds who they
		// were looking for, and goes back to the ground they are on.
		(_, _, Guid rideId) = WireServices();

		IRenderedComponent<RideMembersLive> component = RenderMembers(rideId);

		component.WaitForAssertion(
			() => component.Find(".page-nav-back").GetAttribute("href").ShouldBe($"/group-rides/live/{rideId}"),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task MemberJoined_AppearsInTheList()
	{
		(_, FakeRideHubClient hub, Guid rideId) = WireServices();

		IRenderedComponent<RideMembersLive> component = RenderMembers(rideId);

		component.WaitForAssertion(
			() => component.FindAll(".live-members li").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		RideMemberSummary alice = new(Guid.NewGuid(), "AliceNewJoiner", "Rider", FixedInstant);
		await component.InvokeAsync(() => hub.RaiseMemberJoined(rideId, alice));

		component.WaitForAssertion(
			() => component.Markup.ShouldContain("AliceNewJoiner"),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task MemberLeft_DropsThemFromTheList()
	{
		Guid other = Guid.NewGuid();
		(_, FakeRideHubClient hub, Guid rideId) = WireServices(members:
		[
			new RideMemberSummary(Guid.NewGuid(), "Me", "Organiser", FixedInstant),
			new RideMemberSummary(other, "Bob", "Rider", FixedInstant, Sharing: true, HasPosition: true),
		]);

		IRenderedComponent<RideMembersLive> component = RenderMembers(rideId);

		component.WaitForAssertion(
			() => component.Markup.ShouldContain("Bob"),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => hub.RaiseMemberLeft(rideId, other));

		// §5.3, §5.6: a name left behind after somebody has gone is a "ghost member", and on this
		// screen it would come with an age that never updates again.
		component.WaitForAssertion(
			() => component.Markup.ShouldNotContain("Bob"),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task AFixArrivingOnTheHub_MovesThatRidersFigures()
	{
		Guid bob = Guid.NewGuid();
		(FakeApiClient api, FakeRideHubClient hub, Guid rideId) = WireServices(members:
		[
			new RideMemberSummary(bob, "Bob", "Rider", FixedInstant, Sharing: true, HasPosition: true),
		]);

		api.RoutesResult.Add(EastwardRoute());
		api.PositionsResult = [Fix(bob, "Bob", BaseLat, BaseLon)];

		IRenderedComponent<RideMembersLive> component = RenderMembers(rideId);

		component.WaitForAssertion(
			() => component.Find(".live-members .along dd").TextContent.Trim().ShouldBe("0 m"),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => hub.RaisePositionsUpdated(new PositionBatch(rideId,
		[
			new PositionFix(bob, PositionScale.FromDegrees(BaseLat), PositionScale.FromDegrees(BaseLon + 0.02),
				null, null, FixedInstant),
		])));

		// §5.3: the snapshot got the ride onto the screen and the hub moved it, with no refetch.
		component.WaitForAssertion(
			() => component.Find(".live-members .along dd").TextContent.Trim().ShouldBe("1.8 km"),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task MemberSharingChanged_FlipsTheStateAndTakesTheirFigureAway()
	{
		Guid bob = Guid.NewGuid();
		(FakeApiClient api, FakeRideHubClient hub, Guid rideId) = WireServices(members:
		[
			new RideMemberSummary(bob, "Bob", "Rider", FixedInstant, Sharing: true, HasPosition: true),
		]);

		api.PositionsResult = [Fix(bob, "Bob", BaseLat, BaseLon)];

		IRenderedComponent<RideMembersLive> component = RenderMembers(rideId);

		component.WaitForAssertion(
			() => component.Find(".live-members .state").TextContent.Trim().ShouldBe("sharing"),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => hub.RaiseMemberSharingChanged(rideId, bob, sharing: false));

		component.WaitForAssertion(() =>
		{
			// §5.6: turning sharing off deletes the stored fix rather than merely ceasing to update
			// it, so the row has to stop reporting an age as well as changing its word.
			component.Find(".live-members .state").TextContent.Trim().ShouldBe("not sharing");
			component.Find(".live-members .age dd").TextContent.Trim().ShouldBe("—");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void ARideWithNoRoute_SaysWhyTheRouteColumnsAreEmpty()
	{
		(_, _, Guid rideId) = WireServices();

		IRenderedComponent<RideMembersLive> component = RenderMembers(rideId);

		component.WaitForAssertion(
			() => component.Markup.ShouldContain("no planned route"),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void OnAHostWithNoReceiver_TheEmptyRangeColumnIsExplained()
	{
		// A browser registers no LocationBroadcastState at all (§18.6), so this device does not
		// know where the reader is — and a column of em dashes with no sentence beside it reads as
		// everybody else having no position.
		(_, _, Guid rideId) = WireServices();

		IRenderedComponent<RideMembersLive> component = RenderMembers(rideId);

		component.WaitForAssertion(
			() => component.Markup.ShouldContain("Range is measured from where you are"),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void AgesTick_BetweenFixes()
	{
		// The fixes arrive every few seconds (§5.3); the ages have to keep counting in between,
		// because a row where nothing is arriving is exactly the row a rider opened this screen
		// to find.
		Guid bob = Guid.NewGuid();
		(FakeApiClient api, _, Guid rideId) = WireServices(members:
		[
			new RideMemberSummary(bob, "Bob", "Rider", FixedInstant, Sharing: true, HasPosition: true),
		]);

		api.PositionsResult = [Fix(bob, "Bob", BaseLat, BaseLon)];

		IRenderedComponent<RideMembersLive> component = RenderMembers(rideId);

		component.WaitForAssertion(
			() => component.Find(".live-members .age dd").TextContent.Trim().ShouldBe("now"),
			timeout: TimeSpan.FromSeconds(3));

		FakeTimeProvider clock = (FakeTimeProvider)Services.GetRequiredService<TimeProvider>();
		clock.Advance(TimeSpan.FromSeconds(30));

		component.WaitForAssertion(
			() => component.Find(".live-members .age dd").TextContent.Trim().ShouldBe("30 s"),
			timeout: TimeSpan.FromSeconds(3));
	}
}
