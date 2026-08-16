using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Pages.Settings;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The shared screens on a host that registers no GPS at all — which is both browsers (§18.6).
/// <para>
/// This is the arrangement rather than a corner of it: the web hosts register no
/// <c>ILocationProvider</c>, no <c>GpsProfileState</c>, no <c>TrackRecordingState</c>, no
/// <c>PrivateAreaState</c> and no <c>LocationBroadcastState</c>. Every screen below resolves the
/// broadcaster with <c>GetService</c> and renders its no-receiver branch when it comes back null,
/// and that is what these tests hold in place — a screen that went back to <c>@inject</c> would
/// throw here rather than fail quietly on the one host nobody tests by hand.
/// </para>
/// <para>
/// <strong>Receiving is not GPS.</strong> The live map still takes the hub's position batches,
/// because those are data (§5.3) and drawing them never needed a receiver. That is asserted here
/// too, so "we removed the GPS" cannot quietly become "we removed the map".
/// </para>
/// </summary>
public sealed class HostWithoutGpsTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private static readonly Guid OtherRiderId = Guid.Parse("22222222-2222-2222-2222-222222222222");

	/// <summary>
	/// Everything a browser host registers, and nothing it does not. Deliberately spelled out
	/// rather than shared with the other page suites: the point of this file is the absence, and
	/// a helper that quietly added a service would take the test with it.
	/// </summary>
	private (FakeApiClient Api, FakeRideHubClient Hub, Guid RideId) WireBrowser()
	{
		Guid rideId = Guid.NewGuid();

		FakeApiClient api = new()
		{
			RideResult = new RideDetail(
				Id: rideId,
				Name: "Saturday Coast Run",
				Description: "Up the old road.",
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
					new RideMemberSummary(Guid.NewGuid(), "dave", "Rider", FixedInstant),
					new RideMemberSummary(OtherRiderId, "sam", "Rider", FixedInstant),
				]),
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
		Services.AddSingleton<IDeviceSettings, InMemoryDeviceSettings>();
		Services.AddSingleton<RouteStyleState>();
		Services.AddSingleton<CurrentRideState>();
		Services.AddSingleton<IMapInterop>(new FakeMapInterop
		{
			InitException = new InvalidOperationException("No base map in bUnit."),
		});

		// And that is the whole list. No ILocationProvider, no GpsProfileState, no
		// TrackRecordingState, no PrivateAreaState, no LocationBroadcastState.

		Services.AddRealAuthorizationPipeline();
		this.CascadeAuthenticationState(auth);

		return (api, hub, rideId);
	}

	private void AssertNoGpsRegistered()
	{
		Services.GetService<ILocationProvider>().ShouldBeNull();
		Services.GetService<LocationBroadcastState>().ShouldBeNull();
		Services.GetService<GpsProfileState>().ShouldBeNull();
		Services.GetService<TrackRecordingState>().ShouldBeNull();
		Services.GetService<PrivateAreaState>().ShouldBeNull();
	}

	// ---------- Settings ----------

	[Fact]
	public void Settings_DoesNotOfferLocation()
	{
		WireBrowser();

		IRenderedComponent<Settings> component = Render<Settings>();

		AssertNoGpsRegistered();

		component.FindAll("a.card[href='/settings/location']").ShouldBeEmpty(
			"§18.6: every control on that screen configures a receiver this host does not have.");

		// The rest of the hub is untouched — this is a removal, not a collapse.
		component.FindAll("a.card[href='/settings/profile']").Count.ShouldBe(1);
		component.FindAll("a.card[href='/settings/maps']").Count.ShouldBe(1);
	}

	[Fact]
	public void Location_DeepLinked_SaysItIsAPhoneSetting_RatherThanThrowing()
	{
		// The route lives in the shared assembly, so it resolves on every host. A bookmark or a
		// typed URL has to land somewhere honest rather than on a DI exception.
		WireBrowser();

		IRenderedComponent<Location> component = Render<Location>();

		component.Markup.ShouldContain("set up on your phone");

		// None of the controls are there — not disabled, not empty: absent.
		component.FindAll("input[name=gps-profile]").ShouldBeEmpty();
		component.FindAll(".intervals").ShouldBeEmpty();
		component.FindAll(".area-picker").ShouldBeEmpty();
		component.FindAll(".track-actions").ShouldBeEmpty();
	}

	// ---------- The ride's info page ----------

	[Fact]
	public void RideInfo_HasNoSharingSwitch()
	{
		// The headline of this change. The switch set a server-side consent flag for fixes only a
		// phone can produce, under copy promising "your phone keeps sending" — on a laptop.
		(_, _, Guid rideId) = WireBrowser();

		IRenderedComponent<GroupRideInfo> component = Render<GroupRideInfo>(
			parameters => parameters.Add(page => page.RideId, rideId));

		component.WaitForAssertion(() =>
			component.Markup.ShouldContain("Saturday Coast Run"), timeout: TimeSpan.FromSeconds(3));

		component.FindAll(".sharing").ShouldBeEmpty(
			"a browser cannot broadcast, so it does not offer to.");
		component.Markup.ShouldNotContain("Broadcast my location");
	}

	[Fact]
	public void RideInfo_StillShowsEverythingThatIsNotSharing()
	{
		(_, _, Guid rideId) = WireBrowser();

		IRenderedComponent<GroupRideInfo> component = Render<GroupRideInfo>(
			parameters => parameters.Add(page => page.RideId, rideId));

		component.WaitForAssertion(() =>
		{
			// Members and routes are ride data, not device data — untouched by any of this. The
			// names themselves moved to "Ride members live"; what is left here is the count and
			// the way through to them.
			component.Find(".members h3").TextContent.ShouldBe("Members (2)");
			component.FindAll(".members .to-members").Count.ShouldBe(1);
			component.FindAll(".routes").Count.ShouldBe(1);
		}, timeout: TimeSpan.FromSeconds(3));
	}

	// ---------- The live map ----------

	[Fact]
	public async Task LiveMap_Renders_AndSaysNothingAboutAReceiverItDoesNotHave()
	{
		(_, _, Guid rideId) = WireBrowser();

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(
			parameters => parameters.Add(page => page.RideId, rideId));

		// The hamburger appearing is the snapshot having landed — the map is the whole page, so
		// there is no ride name in the markup to wait on.
		component.WaitForAssertion(() =>
			component.FindAll("button.hamburger").ShouldNotBeEmpty(), timeout: TimeSpan.FromSeconds(3));

		// The GPS strip over the map is absent — a rider watching other people's pins was never
		// asking about their own.
		component.FindAll(".gps-alert").ShouldBeEmpty();
		component.Markup.ShouldNotContain("cannot share its location");

		// And so is the receiver's line inside the menu, which is where it lives.
		await component.InvokeAsync(() => component.Find("button.hamburger").Click());

		component.FindAll(".gps").ShouldBeEmpty(
			"§18.6: a line reading \"this device cannot share its location\" answers a question a "
			+ "rider on a laptop was never asking.");
	}

	[Fact]
	public async Task LiveMap_StillTakesOtherRidersPositions()
	{
		// The distinction the whole change rests on: sharing is a GPS concern and receiving is
		// not. A batch arriving over the hub is data, and taking it never needed a receiver.
		(_, FakeRideHubClient hub, Guid rideId) = WireBrowser();

		IRenderedComponent<GroupRideLive> component = Render<GroupRideLive>(
			parameters => parameters.Add(page => page.RideId, rideId));

		component.WaitForAssertion(() =>
			component.FindAll("button.hamburger").ShouldNotBeEmpty(), timeout: TimeSpan.FromSeconds(3));

		await component.InvokeAsync(() => hub.RaisePositionsUpdated(new PositionBatch(
			rideId,
			[
				new PositionFix(
					OtherRiderId,
					PositionScale.FromDegrees(-33.868),
					PositionScale.FromDegrees(151.209),
					12,
					90,
					FixedInstant),
			])));

		// The page took the batch with no receiver anywhere in its graph. bUnit surfaces an
		// unhandled render exception on the next render, so a null-deref in the map rebuild —
		// SelfMarker and MyPoint both reach for the broadcaster — would fail here.
		component.WaitForAssertion(() =>
			component.FindAll("button.hamburger").ShouldNotBeEmpty(), timeout: TimeSpan.FromSeconds(3));

		AssertNoGpsRegistered();
	}
}
