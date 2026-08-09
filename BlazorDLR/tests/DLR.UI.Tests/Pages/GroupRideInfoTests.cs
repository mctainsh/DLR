using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
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
/// The ride's info page — everything about a group ride that is not the map. It reads the
/// same <c>RideSession</c> the live map does, so §5.3's rule is what these tests exercise:
/// the snapshot is authoritative and the hub is the delta on top.
/// <list type="bullet">
///   <item><c>RideStateChanged</c> — an Open ride flipping to Live must show the new state,
///     unlock the gap list, and stop showing the organiser's "Start ride" button (that
///     button lives on the Open branch only).</item>
///   <item><c>MemberJoined</c> / <c>MemberLeft</c> — the member list follows.</item>
///   <item><c>SharingWindDownStarted</c> — the banner appears with the stated cutoff (§5.6).</item>
///   <item>The organiser's lifecycle controls: Start (§5.1) and the two-choice End (§5.6).</item>
/// </list>
/// </summary>
public sealed class GroupRideInfoTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	private (FakeApiClient api, FakeRideHubClient hub, Guid rideId) WireServices(
		RideStateDto state = RideStateDto.Open,
		bool isOrganiser = false,
		bool sharing = false,
		IReadOnlyList<RideMemberSummary>? members = null)
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
				MemberCount: members?.Count ?? 1,
				IsOrganiser: isOrganiser,
				JoinCode: isOrganiser ? "AB3K9Z" : null,
				Permissions: new RidePermissions(),
				Members: members ?? new[]
				{
					new RideMemberSummary(Guid.NewGuid(), "Me", "Rider", FixedInstant, sharing, sharing),
				}),
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

		return (api, hub, rideId);
	}

	private IRenderedComponent<GroupRideInfo> RenderInfo(Guid rideId) =>
		Render<GroupRideInfo>(parameters => parameters.Add(p => p.RideId, rideId));

	/// <summary>A route as the ride's route endpoint describes it (§5.4).</summary>
	private static RideRoute Route(string name, double distanceM = 42_000, Guid trackId = default) => new(
		TrackId: trackId == default ? Guid.NewGuid() : trackId,
		Name: name,
		DistanceM: distanceM,
		PointCount: 2,
		EncodedPolyline: PolylineCodec.EncodePoints([new TrackPoint(-33.86, 151.20), new TrackPoint(-33.87, 151.21)]),
		Bounds: null,
		AddedUtc: FixedInstant,
		AddedByUserId: Guid.NewGuid(),
		AddedByUserName: "DaveSmith");

	[Fact]
	public void Routes_AreListed_InTheColourTheMapDrawsThem()
	{
		(FakeApiClient api, _, Guid rideId) = WireServices();

		api.RoutesResult.Add(Route("The long way"));
		api.RoutesResult.Add(Route("The short way"));

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		component.WaitForAssertion(
			() => component.FindAll(".route-list li").Count.ShouldBe(2,
				"§5.4: a ride carries a set of planned routes, and the panel lists all of them."),
			timeout: TimeSpan.FromSeconds(3));

		component.Markup.Contains("The long way", StringComparison.Ordinal).ShouldBeTrue();
		component.Markup.Contains("The short way", StringComparison.Ordinal).ShouldBeTrue();

		// The swatch is the colour that route is drawn in on the map. Two routes in one colour is
		// a list that cannot be read against the map beside it.
		(component.FindAll(".route-list .swatch")[0].GetAttribute("style") ?? string.Empty)
			.ShouldContain(RoutePalette.At(0), Case.Insensitive);
		(component.FindAll(".route-list .swatch")[1].GetAttribute("style") ?? string.Empty)
			.ShouldContain(RoutePalette.At(1), Case.Insensitive);
	}

	[Fact]
	public void Routes_AddAndRemove_AreHiddenFromAnOrdinaryMember()
	{
		(FakeApiClient api, _, Guid rideId) = WireServices(isOrganiser: false);

		api.RoutesResult.Add(Route("The long way"));

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		component.WaitForAssertion(
			() => component.FindAll(".route-list li").ShouldNotBeEmpty(
				"a member still sees which routes the ride is on — reading is membership."),
			timeout: TimeSpan.FromSeconds(3));

		// Mirrors the server's owner-or-leader check, so the control is absent rather than there
		// and 403-ing.
		component.FindAll(".route-list .remove").ShouldBeEmpty(
			"§5.4: the organiser decides which routes a ride has.");
		component.FindAll("button").Any(button => button.TextContent.Contains("Add a route", StringComparison.Ordinal))
			.ShouldBeFalse();
	}

	[Fact]
	public async Task Organiser_AddsARoute_FromTheirOwnTracks()
	{
		(FakeApiClient api, _, Guid rideId) = WireServices(isOrganiser: true);

		Guid trackId = Guid.NewGuid();
		api.TracksResult =
		[
			new TrackSummary(trackId, "Saturday loop", FixedInstant, null, null, 42_000, null, null, null, 900, 1,
				TrackSourceDto.Imported, 1),
		];

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		await ClickAsync(component, "Add a route");

		component.WaitForAssertion(
			() => component.FindAll(".track-list button").ShouldNotBeEmpty(
				"the picker offers the caller's own tracks — the only ones §15.4 lets them attach."),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find(".track-list button").Click());

		component.WaitForAssertion(
			() => api.AddedRoutes.ShouldContain((rideId, trackId)),
			timeout: TimeSpan.FromSeconds(3));

		component.WaitForAssertion(
			() => component.Markup.Contains("Saturday loop", StringComparison.Ordinal).ShouldBeTrue(
				"the attached route appears in the panel without a reload."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Organiser_RemovesARoute_AndItLeavesTheRide()
	{
		(FakeApiClient api, _, Guid rideId) = WireServices(isOrganiser: true);

		RideRoute route = Route("The long way");
		api.RoutesResult.Add(route);

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		component.WaitForAssertion(
			() => component.FindAll(".route-list .remove").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find(".route-list .remove").Click());

		component.WaitForAssertion(
			() => api.RemovedRoutes.ShouldContain((rideId, route.TrackId)),
			timeout: TimeSpan.FromSeconds(3));

		component.WaitForAssertion(
			() => component.Markup.Contains("The long way", StringComparison.Ordinal).ShouldBeFalse(
				"detaching removes it from the ride — the owner's track itself is untouched."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task RoutesChanged_HubEvent_RefetchesTheSet()
	{
		(FakeApiClient api, FakeRideHubClient hub, Guid rideId) = WireServices();

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		component.WaitForAssertion(
			() => component.Markup.Contains("No route on this ride yet", StringComparison.Ordinal).ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		// Somebody else attached one. The event carries the ride, not the routes — the lines are
		// the largest thing a ride owns, so the client refetches rather than being pushed them.
		api.RoutesResult.Add(Route("Added by the organiser"));

		await component.InvokeAsync(() => hub.RaiseRoutesChanged(rideId));

		component.WaitForAssertion(
			() => component.Markup.Contains("Added by the organiser", StringComparison.Ordinal).ShouldBeTrue(
				"§5.3: the hub is the delta on top of the snapshot."),
			timeout: TimeSpan.FromSeconds(3));
	}

	private static async Task ClickAsync(IRenderedComponent<GroupRideInfo> component, string text)
	{
		component.WaitForAssertion(
			() => component.FindAll("button").Any(button => button.TextContent.Contains(text, StringComparison.Ordinal))
				.ShouldBeTrue($"expected a button reading '{text}'."),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.FindAll("button")
			.First(button => button.TextContent.Contains(text, StringComparison.Ordinal))
			.Click());
	}

	[Fact]
	public async Task RideStateChanged_UpdatesTheStateBadge()
	{
		(_, FakeRideHubClient hub, Guid rideId) = WireServices(state: RideStateDto.Open);

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		component.WaitForAssertion(() =>
			component.Find(".state").TextContent.ShouldBe("Open"),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => hub.RaiseRideStateChanged(rideId, RideStateDto.Live));

		component.WaitForAssertion(() =>
			component.Find(".state").TextContent.ShouldBe("Live",
				customMessage: "§5.3: the state badge reflects the hub-driven state transition."),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task MemberJoined_AppearsInTheMemberList()
	{
		(_, FakeRideHubClient hub, Guid rideId) = WireServices();

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		component.WaitForAssertion(() =>
			component.Markup.Contains("Members", StringComparison.Ordinal).ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		RideMemberSummary alice = new(
			UserId: Guid.NewGuid(), UserName: "AliceNewJoiner", Role: "Rider",
			JoinedUtc: FixedInstant, Sharing: false, HasPosition: false);
		await component.InvokeAsync(() => hub.RaiseMemberJoined(rideId, alice));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("AliceNewJoiner", StringComparison.Ordinal).ShouldBeTrue(
				"§5.3: MemberJoined delta adds the member to the visible list without a re-fetch.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task MemberLeft_HubEvent_RemovesTheMemberFromTheList()
	{
		Guid other = Guid.NewGuid();
		(_, FakeRideHubClient hub, Guid rideId) = WireServices(members: new[]
		{
			new RideMemberSummary(Guid.NewGuid(), "Me", "Organiser", FixedInstant, false, false),
			new RideMemberSummary(other, "Bob", "Rider", FixedInstant, true, true),
		});

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		component.WaitForAssertion(() =>
			component.Markup.Contains("Bob", StringComparison.Ordinal).ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => hub.RaiseMemberLeft(rideId, other));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Bob", StringComparison.Ordinal).ShouldBeFalse(
				"§5.3: MemberLeft removes the member from the list — leaving a stale name is what §5.6 calls 'ghost members'.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task SharingWindDownStarted_ShowsBannerWithCutoffTime()
	{
		(_, FakeRideHubClient hub, Guid rideId) = WireServices(state: RideStateDto.Live);

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		component.WaitForAssertion(() =>
			component.Markup.Contains("Test ride", StringComparison.Ordinal).ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		// The banner has always previously been absent — the state carried no wind-down.
		component.Markup.Contains("wind-down active", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
			"the banner is only present after the SharingWindDownStarted event fires.");

		DateTimeOffset endsUtc = FixedInstant.AddHours(2);
		await component.InvokeAsync(() => hub.RaiseSharingWindDownStarted(rideId, endsUtc));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Sharing wind-down active", StringComparison.Ordinal).ShouldBeTrue(
				"§5.6: the wind-down banner appears on the SharingWindDownStarted event.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void OrganiserJoinCode_IsShownToTheOrganiser_AndOnlyToTheOrganiser()
	{
		(_, _, Guid rideId) = WireServices(isOrganiser: true);

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("AB3K9Z", StringComparison.Ordinal).ShouldBeTrue(
				"§5.2: the organiser sees the join code on the ride page.");
			component.Markup.Contains("Only you see this", StringComparison.Ordinal).ShouldBeTrue(
				"the copy makes it clear this code is not on the shared view.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task EventForDifferentRide_IsIgnored()
	{
		(_, FakeRideHubClient hub, Guid rideId) = WireServices();

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		component.WaitForAssertion(() =>
			component.Markup.Contains("Test ride", StringComparison.Ordinal).ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		// Raise events for a different ride id — none of them must alter this page.
		Guid otherRide = Guid.NewGuid();
		await component.InvokeAsync(() => hub.RaiseRideStateChanged(otherRide, RideStateDto.Completed));
		await component.InvokeAsync(() => hub.RaiseMemberJoined(otherRide,
			new RideMemberSummary(Guid.NewGuid(), "InterloperFromOtherRide", "Rider", FixedInstant, false, false)));

		// The ride still says Open and the interloper is not in the member list.
		component.Markup.Contains("Completed", StringComparison.Ordinal).ShouldBeFalse(
			"§5.3: events for other rides must not alter this ride's state — a shared hub connection is not a shared page.");
		component.Markup.Contains("InterloperFromOtherRide", StringComparison.Ordinal).ShouldBeFalse();
	}

	[Fact]
	public async Task Organiser_ClicksStartRide_CallsStartRideAsync()
	{
		(FakeApiClient api, _, Guid rideId) = WireServices(state: RideStateDto.Open, isOrganiser: true);

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		component.WaitForAssertion(() =>
			component.FindAll("button").Any(b => b.TextContent.Contains("Start ride", StringComparison.Ordinal))
				.ShouldBeTrue("§5.1: an organiser looking at an Open ride sees the Start button."),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.FindAll("button")
			.First(b => b.TextContent.Contains("Start ride", StringComparison.Ordinal))
			.Click());

		component.WaitForAssertion(() => api.StartedRides.ShouldContain(rideId),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void StartButton_HiddenForNonOrganiser()
	{
		(_, _, Guid rideId) = WireServices(state: RideStateDto.Open, isOrganiser: false);

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		component.WaitForAssertion(() =>
		{
			component.FindAll("button").Any(b => b.TextContent.Contains("Start ride", StringComparison.Ordinal))
				.ShouldBeFalse("§5.1: only the organiser can start — the button must not appear for anyone else.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Organiser_EndRide_ImmediateChoice_SendsImmediateEnding()
	{
		(FakeApiClient api, _, Guid rideId) = WireServices(state: RideStateDto.Live, isOrganiser: true);

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		await OpenEndDialogAsync(component);

		await component.InvokeAsync(() => component.FindAll("button.primary")
			.First(b => b.TextContent.Contains("Stop sharing", StringComparison.Ordinal))
			.Click());

		component.WaitForAssertion(() => api.LastEndRide.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));
		api.LastEndRide!.Value.Request.Ending.ShouldBe(RideEndingDto.Immediate,
			"§5.6: 'Stop sharing for everyone now' sends the Immediate ending — the alternative would leave the wind-down running.");
	}

	[Fact]
	public async Task Organiser_EndRide_WindDownChoice_SendsWindDownEnding()
	{
		(FakeApiClient api, _, Guid rideId) = WireServices(state: RideStateDto.Live, isOrganiser: true);

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		await OpenEndDialogAsync(component);

		await component.InvokeAsync(() => component.FindAll("button")
			.First(b => b.TextContent.Contains("2-hour wind-down", StringComparison.Ordinal))
			.Click());

		component.WaitForAssertion(() => api.LastEndRide.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));
		api.LastEndRide!.Value.Request.Ending.ShouldBe(RideEndingDto.WindDown,
			"§5.6: the alternative choice explicitly opts into the two-hour wind-down.");
	}

	[Fact]
	public async Task SharingSwitch_SendsTheRidersChoice()
	{
		(FakeApiClient api, _, Guid rideId) = WireServices(state: RideStateDto.Live);

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		component.WaitForAssertion(() =>
			component.FindAll(".sharing input[type=checkbox]").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.Find(".sharing input[type=checkbox]").Change(true));

		component.WaitForAssertion(() => api.SetSharingRequests.ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));
		api.SetSharingRequests.Last().Request.Share.ShouldBeTrue(
			"§5.6: the switch is the rider's own control over their broadcast, and it reaches the server.");
	}

	private static async Task OpenEndDialogAsync(IRenderedComponent<GroupRideInfo> component)
	{
		component.WaitForAssertion(() =>
			component.FindAll("button").Any(b => b.TextContent.Contains("End ride…", StringComparison.Ordinal))
				.ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => component.FindAll("button")
			.First(b => b.TextContent.Contains("End ride…", StringComparison.Ordinal))
			.Click());

		component.WaitForAssertion(() =>
			component.FindAll(".end-dialog").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));
	}
}
