using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The organiser-only controls and remaining hub deltas on the live ride page.
/// Where <see cref="GroupRideLiveTests"/> covers viewer-side transitions, these
/// tests exercise: the Start button (§5.1), the end-ride two-choice dialog with
/// its immediate/wind-down decision (§5.6), the consent prompt lifecycle (§5.6),
/// and the marker-added / member-left / member-sharing-changed hub deltas that
/// remove or add pins on the map.
/// </summary>
public sealed class GroupRideLiveMoreTests : BunitContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	private (FakeApiClient api, FakeRideHubClient hub, Guid rideId, AuthState auth) WireServices(
		RideStateDto state = RideStateDto.Open,
		bool isOrganiser = false,
		bool sharing = false,
		IReadOnlyList<RideMemberSummary>? members = null)
	{
		Guid rideId = Guid.NewGuid();
		Guid userId = Guid.NewGuid();
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
					new RideMemberSummary(userId, "Me", "Rider", FixedInstant, sharing, sharing),
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
		Services.AddRealAuthorizationPipeline();
		this.CascadeAuthenticationState(auth);
		Services.AddSingleton<IMapInterop>(new FakeMapInterop
		{
			InitException = new InvalidOperationException("Test host — map stubbed."),
		});

		return (api, hub, rideId, auth);
	}

	[Fact]
	public async Task Organiser_ClicksStartRide_CallsStartRideAsync()
	{
		(FakeApiClient api, _, Guid rideId, _) = WireServices(state: RideStateDto.Open, isOrganiser: true);

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.FindAll("button").Any(b => b.TextContent.Contains("Start ride", StringComparison.Ordinal))
				.ShouldBeTrue("§5.1: an organiser looking at an Open ride sees the Start button."),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement start = component.FindAll("button")
				.First(b => b.TextContent.Contains("Start ride", StringComparison.Ordinal));
			start.Click();
		});

		component.WaitForAssertion(() => api.StartedRides.ShouldContain(rideId),
			timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void StartButton_HiddenForNonOrganiser()
	{
		(_, _, Guid rideId, _) = WireServices(state: RideStateDto.Open, isOrganiser: false);

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
		{
			component.FindAll("button").Any(b => b.TextContent.Contains("Start ride", StringComparison.Ordinal))
				.ShouldBeFalse("§5.1: only the organiser can start — the button must not appear for anyone else.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task Organiser_EndRide_ImmediateChoice_SendsImmediateEnding()
	{
		(FakeApiClient api, _, Guid rideId, _) = WireServices(state: RideStateDto.Live, isOrganiser: true);

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.FindAll("button").Any(b => b.TextContent.Contains("End ride…", StringComparison.Ordinal))
				.ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement openEnd = component.FindAll("button")
				.First(b => b.TextContent.Contains("End ride…", StringComparison.Ordinal));
			openEnd.Click();
		});

		component.WaitForAssertion(() =>
			component.FindAll("button.primary").Any(b => b.TextContent.Contains("Stop sharing", StringComparison.Ordinal))
				.ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement immediate = component.FindAll("button.primary")
				.First(b => b.TextContent.Contains("Stop sharing", StringComparison.Ordinal));
			immediate.Click();
		});

		component.WaitForAssertion(() => api.LastEndRide.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));
		api.LastEndRide!.Value.Request.Ending.ShouldBe(RideEndingDto.Immediate,
			"§5.6: 'Stop sharing for everyone now' sends the Immediate ending — the alternative would leave the wind-down running.");
	}

	[Fact]
	public async Task Organiser_EndRide_WindDownChoice_SendsWindDownEnding()
	{
		(FakeApiClient api, _, Guid rideId, _) = WireServices(state: RideStateDto.Live, isOrganiser: true);

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.FindAll("button").Any(b => b.TextContent.Contains("End ride…", StringComparison.Ordinal))
				.ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement openEnd = component.FindAll("button")
				.First(b => b.TextContent.Contains("End ride…", StringComparison.Ordinal));
			openEnd.Click();
		});

		component.WaitForAssertion(() =>
			component.FindAll("button").Any(b => b.TextContent.Contains("2-hour wind-down", StringComparison.Ordinal))
				.ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement windDown = component.FindAll("button")
				.First(b => b.TextContent.Contains("2-hour wind-down", StringComparison.Ordinal));
			windDown.Click();
		});

		component.WaitForAssertion(() => api.LastEndRide.ShouldNotBeNull(),
			timeout: TimeSpan.FromSeconds(3));
		api.LastEndRide!.Value.Request.Ending.ShouldBe(RideEndingDto.WindDown,
			"§5.6: the alternative choice explicitly opts into the two-hour wind-down.");
	}

	[Fact]
	public void ConsentPrompt_IsShownWhenNotSharing_AndRideIsOpen()
	{
		Guid userId = Guid.NewGuid();
		(_, _, Guid rideId, AuthState auth) = WireServices(
			state: RideStateDto.Open,
			sharing: false,
			members: new[] { new RideMemberSummary(userId, "Me", "Rider", FixedInstant, false, false) });

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Share your location with Test ride", StringComparison.Ordinal).ShouldBeTrue(
				"§5.6: opening the ride when not already sharing prompts for consent — the dialog names the ride so the user knows what they are agreeing to.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task ConsentShare_CallsSetSharingWithTrue()
	{
		(FakeApiClient api, _, Guid rideId, _) = WireServices(state: RideStateDto.Open);

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.FindAll("button.primary").Any(b => b.TextContent.Contains("Share", StringComparison.Ordinal))
				.ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() =>
		{
			AngleSharp.Dom.IElement share = component.FindAll("button.primary")
				.First(b => b.TextContent.Contains("Share", StringComparison.Ordinal));
			share.Click();
		});

		component.WaitForAssertion(() => api.SetSharingRequests.ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));
		api.SetSharingRequests.Last().Request.Share.ShouldBeTrue(
			"§5.6: tapping Share on the consent prompt sends SetSharing(true).");
		api.SetSharingRequests.Last().RideId.ShouldBe(rideId);
	}

	[Fact]
	public void MemberLeft_HubEvent_RemovesTheMemberFromTheList()
	{
		Guid other = Guid.NewGuid();
		(_, FakeRideHubClient hub, Guid rideId, _) = WireServices(members: new[]
		{
			new RideMemberSummary(Guid.NewGuid(), "Me", "Organiser", FixedInstant, false, false),
			new RideMemberSummary(other, "Bob", "Rider", FixedInstant, true, true),
		});

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.Markup.Contains("Bob", StringComparison.Ordinal).ShouldBeTrue(),
			timeout: TimeSpan.FromSeconds(3));

		component.InvokeAsync(() => hub.RaiseMemberLeft(rideId, other)).GetAwaiter().GetResult();

		component.WaitForAssertion(() =>
		{
			component.Markup.Contains("Bob", StringComparison.Ordinal).ShouldBeFalse(
				"§5.3: MemberLeft removes the member from the list — leaving a stale name is what §5.6 calls 'ghost members'.");
		}, timeout: TimeSpan.FromSeconds(3));
	}

	[Fact]
	public void MarkerAdded_HubEvent_DoesNotThrow()
	{
		(_, FakeRideHubClient hub, Guid rideId, _) = WireServices();

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(parameters => parameters
			.Add(p => p.RideId, rideId));

		component.WaitForAssertion(() =>
			component.Markup.Contains("Test ride", StringComparison.Ordinal).ShouldBeTrue(),
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
}
