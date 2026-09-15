using BlazorDLR.Shared.Pages;
using BlazorDLR.Shared.Pages.GroupRides;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using Bunit;
using DLR.Core.Contracts.Rides;
using DLR.Core.Tracks;
using DLR.UI.Tests.Fakes;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static DLR.UI.Tests.Fakes.MapPackFixtures;

namespace DLR.UI.Tests.Pages;

/// <summary>
/// The two offers of an offline pack that are not on a map (§4.2): the deferred one on Home, which
/// picks up a dead zone from last ride, and the row on an adventure's info page.
/// <para>
/// They answer the same question from opposite ends. Home is remedial - the map already went blank
/// and this is the first screen since with a connection to fix it. The ride's row is preventive,
/// and sits where somebody is planning rather than riding, which is the one moment a
/// several-hundred-megabyte download over wifi is a reasonable thing to ask anybody for.
/// </para>
/// </summary>
public sealed class OfflineMapOfferPageTests : PageTestContext
{
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

	private readonly FakeMapPackStore _packs = new();

	/// <summary>
	/// Puts a phone under the page. Registered after <c>PageTestContext</c>'s own wiring, which
	/// binds the browser's pack store (§18.6) - the last registration is the one resolved.
	/// </summary>
	private void WireAPhone(bool phone = true)
	{
		_packs.IsSupported = phone;
		Services.AddOfferedPacks(_packs);
	}

	// -- Home ------------------------------------------------------------------------------------

	private void WireHome()
	{
		WireAPhone();
		Services.AddSingleton<IFormFactor>(new FakeFormFactor { FormFactor = "Phone", Platform = "Android" });

		// Home offers the administration section on the server's roster (§14.6). The fake answers a
		// profile with IsAdmin false, so the section stays off - which is the state every account
		// but a handful is in, and the one these tests are about.
		Services.AddSingleton<IApiClient>(new FakeApiClient());
		Services.AddSingleton<AdminAccess>();
	}

	/// <summary>
	/// A rider whose map went blank last ride is offered the pack that covers where it happened -
	/// here, on the first screen since with a connection to act on it.
	/// </summary>
	[Fact]
	public async Task Home_AfterAMapWentBlank_OffersThePackThatCoversIt()
	{
		WireHome();

		OfflineMapOfferState offers = Services.GetRequiredService<OfflineMapOfferState>();
		await offers.RecordMissAsync(SydneyLatitude, SydneyLongitude);

		// MainLayout's launch ladder does this in the app - Home only renders what it found.
		await offers.PrimeMissedAsync();

		IRenderedComponent<Home> component = Render<Home>();

		string strip = component.Find(".home-offline-offer").TextContent;
		strip.ShouldContain("Your map went blank last ride");
		strip.ShouldContain("New South Wales");
	}

	/// <summary>
	/// And a rider whose map has never failed is told nothing. Home is the landing screen, so this
	/// is the silence that keeps the feature from being a permanent fixture of it.
	/// </summary>
	[Fact]
	public void Home_WithNoDeadZoneRecorded_SaysNothing()
	{
		WireHome();

		IRenderedComponent<Home> component = Render<Home>();

		component.FindAll(".home-offline-offer").Count.ShouldBe(0,
			"nothing has gone wrong on this device, so there is nothing to offer.");
	}

	/// <summary>Taking it here downloads the pack and points every map in the app at it.</summary>
	[Fact]
	public async Task Home_TakingTheOffer_DownloadsAndSelectsThePack()
	{
		WireHome();

		OfflineMapOfferState offers = Services.GetRequiredService<OfflineMapOfferState>();
		await offers.RecordMissAsync(SydneyLatitude, SydneyLongitude);
		await offers.PrimeMissedAsync();

		IRenderedComponent<Home> component = Render<Home>();

		await component.InvokeAsync(() => component.Find(".dlr-offline-offer-take").Click());

		MapSourceState sources = Services.GetRequiredService<MapSourceState>();

		component.WaitForAssertion(
			() =>
			{
				sources.Chosen.PackId.ShouldBe("au-nsw");
				component.FindAll(".home-offline-offer").Count.ShouldBe(0);
			},
			timeout: TimeSpan.FromSeconds(3));

		_packs.Versions.ShouldContainKey("au-nsw");
		offers.MissedPoint.ShouldBeNull("the dead zone this answered is spent.");
	}

	// -- The adventure's info page ---------------------------------------------------------------

	/// <summary>
	/// Wires the info page over an adventure carrying one route. <paramref name="bounds"/> is the
	/// ground it covers - the thing the offer is chosen from (§5.4).
	/// </summary>
	private Guid WireRide(TrackBounds? bounds)
	{
		WireAPhone();

		Guid rideId = Guid.NewGuid();

		FakeApiClient api = new()
		{
			RideResult = new DLR.Core.Contracts.Rides.RideDetail(
				Id: rideId,
				Name: "Test adventure",
				Description: null,
				StartUtc: FixedInstant,
				JoinPolicy: JoinPolicyDto.Open,
				MemberCap: 50,
				MemberCount: 1,
				IsOrganiser: false,
				JoinCode: "AB3K9Z",
				Permissions: new RidePermissions(),
				Members: [new RideMemberSummary(Guid.NewGuid(), "Me", "Rider", FixedInstant, false, false)]),
		};

		if (bounds is { } box)
		{
			api.RoutesResult.Add(new RideRoute(
				TrackId: Guid.NewGuid(),
				Name: "The long way",
				DistanceM: 42_000,
				PointCount: 500,
				EncodedPolyline: "",
				Bounds: box,
				AddedUtc: FixedInstant,
				AddedByUserId: Guid.NewGuid(),
				AddedByUserName: "Me"));
		}

		FakeTokenStore tokens = new();
		FakeTimeProvider clock = new(FixedInstant);
		AuthState auth = new(api, tokens, clock);

		Services.AddSingleton<IApiClient>(api);
		Services.AddSingleton<IRideHubClient>(new FakeRideHubClient());
		Services.AddSingleton<ITokenStore>(tokens);
		Services.AddSingleton<TimeProvider>(clock);
		Services.AddSingleton(auth);
		Services.AddSingleton<AuthenticationStateProvider>(auth);

		// The rail's globe points at whichever ride this device is on (§18.6), and the page
		// injects it whether or not a test touches it.
		Services.AddScoped<CurrentRideState>();

		Services.AddRealAuthorizationPipeline();
		this.CascadeAuthenticationState(auth);

		return rideId;
	}

	private IRenderedComponent<GroupRideInfo> RenderInfo(Guid rideId) =>
		Render<GroupRideInfo>(parameters => parameters.Add(p => p.RideId, rideId));

	/// <summary>
	/// The row names the pack covering the adventure's routes. It carries no "Not now": this is a
	/// fact about the ride sitting beside the ride's other facts, not an interruption.
	/// </summary>
	[Fact]
	public void RideInfo_ForGroundNotOnThePhone_OffersThePackCoveringIt()
	{
		Guid rideId = WireRide(new TrackBounds(-33.90, 150.20, -33.60, 150.60));

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		component.WaitForAssertion(
			() =>
			{
				string row = component.Find(".offline-map").TextContent;
				row.ShouldContain("There is an offline map for this ground");
				row.ShouldContain("New South Wales");
			},
			timeout: TimeSpan.FromSeconds(3));

		component.FindAll(".offline-map .dlr-offline-offer-quiet").Count.ShouldBe(0,
			"there is nothing to dismiss - the row goes away when the pack is here.");
	}

	/// <summary>An adventure with no route yet has no ground to cover, and says nothing.</summary>
	[Fact]
	public void RideInfo_WithNoRouteYet_SaysNothing()
	{
		Guid rideId = WireRide(bounds: null);

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		component.WaitForAssertion(
			() => component.FindAll(".routes").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		component.FindAll(".offline-map").Count.ShouldBe(0,
			"a ride with no route is a ride with no ground to offer a map for.");
	}

	/// <summary>
	/// A ride on ground no published extract covers gets no row rather than the nearest guess -
	/// the same silence the map keeps.
	/// </summary>
	[Fact]
	public void RideInfo_ForGroundNoPackCovers_SaysNothing()
	{
		Guid rideId = WireRide(new TrackBounds(51.4, -0.20, 51.6, 0.10));

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		component.WaitForAssertion(
			() => component.FindAll(".routes").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		component.FindAll(".offline-map").Count.ShouldBe(0, "London is not in this catalogue.");
	}

	/// <summary>
	/// The browser gets no row either, for the reason it gets no banner: a tab cannot keep several
	/// hundred megabytes (§18.6), so the offer would be one it could never honour.
	/// </summary>
	[Fact]
	public void RideInfo_OnABrowser_SaysNothing()
	{
		Guid rideId = WireRide(new TrackBounds(-33.90, 150.20, -33.60, 150.60));
		_packs.IsSupported = false;

		IRenderedComponent<GroupRideInfo> component = RenderInfo(rideId);

		component.WaitForAssertion(
			() => component.FindAll(".routes").ShouldNotBeEmpty(),
			timeout: TimeSpan.FromSeconds(3));

		component.FindAll(".offline-map").Count.ShouldBe(0);
	}
}
