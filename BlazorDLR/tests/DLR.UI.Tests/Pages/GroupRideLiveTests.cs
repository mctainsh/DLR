using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// §5.3 and §5.6 through the live-ride page. The hub is the delta layer over the
/// snapshot (§5.3), so each of the events that alters visible state has to reach the
/// UI as a re-render:
/// <list type="bullet">
///   <item><c>RideStateChanged</c> — an Open ride flipping to Live must show the new
///     state, unlock the gap list, and stop showing the organiser's "Start ride"
///     button (that button lives on the Open branch only).</item>
///   <item><c>MemberJoined</c> — a new member appears in the list.</item>
///   <item><c>PermissionsChanged</c> — the "Add marker" link vanishes when markers are
///     revoked (§5.8), for a non-organiser member.</item>
///   <item><c>SharingWindDownStarted</c> — the wind-down banner appears with the
///     stated cutoff time (§5.6).</item>
/// </list>
/// </summary>
public sealed class GroupRideLiveTests : BunitContext
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
		Services.AddRealAuthorizationPipeline();
		this.CascadeAuthenticationState(auth);

		// GroupRideLive hosts RideMap → SkiaMapOverlay, which needs a JS-bound canvas.
		// Force the map into its stated-error branch so Skia never mounts.
		Services.AddSingleton<IMapInterop>(new FakeMapInterop
		{
			InitException = new InvalidOperationException("Test host — map interop is stubbed."),
		});

		return (api, hub, rideId);
	}

	[Fact]
	public void RideStateChanged_UpdatesTheStateBadge()
	{
		(FakeApiClient api, FakeRideHubClient hub, Guid rideId) = WireServices(state: RideStateDto.Open);

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.Markup.Contains("Open", StringComparison.Ordinal).ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		component.InvokeAsync(() => hub.RaiseRideStateChanged(rideId, RideStateDto.Live)).GetAwaiter().GetResult();

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Live", StringComparison.Ordinal).ShouldBeTrue(
				"§5.3: the state badge reflects the hub-driven state transition.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void MemberJoined_AppearsInTheMemberList()
	{
		(FakeApiClient api, FakeRideHubClient hub, Guid rideId) = WireServices();

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.Markup.Contains("Members", StringComparison.Ordinal).ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		RideMemberSummary alice = new(
			UserId: Guid.NewGuid(), UserName: "AliceNewJoiner", Role: "Rider",
			JoinedUtc: FixedInstant, Sharing: false, HasPosition: false);
		component.InvokeAsync(() => hub.RaiseMemberJoined(rideId, alice)).GetAwaiter().GetResult();

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("AliceNewJoiner", StringComparison.Ordinal).ShouldBeTrue(
				"§5.3: MemberJoined delta adds the member to the visible list without a re-fetch.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void PermissionsChanged_HidesTheAddMarkerLink_ForOrdinaryMember()
	{
		RidePermissions initial = new(AllowMemberMarkers: true, AllowMemberComments: true, AllowMemberPhotos: true);
		(FakeApiClient api, FakeRideHubClient hub, Guid rideId) = WireServices(isOrganiser: false, permissions: initial);

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
		{
			component.FindAll("a").Any(a => a.TextContent.Contains("Add marker", StringComparison.Ordinal))
				.ShouldBeTrue("with markers allowed, an ordinary member sees the Add-marker link.");
		}, timeout: TimeSpan.FromSeconds(3));

		RidePermissions revoked = initial with { AllowMemberMarkers = false };
		component.InvokeAsync(() => hub.RaisePermissionsChanged(rideId, revoked)).GetAwaiter().GetResult();

		component.WaitForAssertion(() =>
		{
			component.FindAll("a").Any(a => a.TextContent.Contains("Add marker", StringComparison.Ordinal))
				.ShouldBeFalse("§5.8: PermissionsChanged with markers off must remove the Add-marker link.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void SharingWindDownStarted_ShowsBannerWithCutoffTime()
	{
		(FakeApiClient api, FakeRideHubClient hub, Guid rideId) = WireServices(state: RideStateDto.Live);

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.Markup.Contains("Test ride", StringComparison.Ordinal).ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		// The banner has always previously been absent — the state carried no wind-down.
		component.Markup.Contains("wind-down active", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
			"the banner is only present after the SharingWindDownStarted event fires.");

		DateTimeOffset endsUtc = FixedInstant.AddHours(2);
		component.InvokeAsync(() => hub.RaiseSharingWindDownStarted(rideId, endsUtc)).GetAwaiter().GetResult();

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Sharing wind-down active", StringComparison.Ordinal).ShouldBeTrue(
				"§5.6: the wind-down banner appears on the SharingWindDownStarted event.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void OrganiserJoinCode_IsShownToTheOrganiser_AndOnlyToTheOrganiser()
	{
		(FakeApiClient api, FakeRideHubClient hub, Guid rideId) = WireServices(isOrganiser: true);

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("AB3K9Z", StringComparison.Ordinal).ShouldBeTrue(
				"§5.2: the organiser sees the join code on the ride page.");
			component.Markup.Contains("Only you see this", StringComparison.Ordinal).ShouldBeTrue(
				"the copy makes it clear this code is not on the shared view.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void EventForDifferentRide_IsIgnored()
	{
		(FakeApiClient api, FakeRideHubClient hub, Guid rideId) = WireServices();

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.Markup.Contains("Test ride", StringComparison.Ordinal).ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		// Raise events for a different ride id — none of them must alter this page.
		Guid otherRide = Guid.NewGuid();
		component.InvokeAsync(() => hub.RaiseRideStateChanged(otherRide, RideStateDto.Completed)).GetAwaiter().GetResult();
		component.InvokeAsync(() => hub.RaiseMemberJoined(otherRide,
			new RideMemberSummary(Guid.NewGuid(), "InterloperFromOtherRide", "Rider", FixedInstant, false, false))).GetAwaiter().GetResult();

		// The ride still says Open and the interloper is not in the member list.
		component.Markup.Contains("Completed", StringComparison.Ordinal).ShouldBeFalse(
			"§5.3: events for other rides must not alter this ride's state — a shared hub connection is not a shared page.");
		component.Markup.Contains("InterloperFromOtherRide", StringComparison.Ordinal).ShouldBeFalse();
	}
}
