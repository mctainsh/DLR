using System.Net;
using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.State;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.State;

/// <summary>
/// §4.4 and §7.9 where they meet: a ride the server cannot be reached for opens from this device's
/// own copy, and one the server says is <em>gone</em> never does.
/// <para>
/// The distinction is the whole test class. Both look like "the load failed" from the outside, and
/// confusing them costs something real either way round: fall back on a 404 and a rider removed
/// from a ride goes on opening it from a cache; refuse to fall back on a transport failure and a
/// rider in a tunnel — the person this feature exists for — gets an error page.
/// </para>
/// </summary>
public sealed class RideSessionOfflineTests
{
	// ClockRules forbids an ambient clock read in test source (§10.4).
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

	private static readonly Guid RideId = Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly Guid MemberId = Guid.Parse("22222222-2222-2222-2222-222222222222");

	private static RideDetail SampleRide() => new(
		RideId,
		"Old Pacific Highway",
		null,
		FixedInstant,
		RideStateDto.Live,
		JoinPolicyDto.Approval,
		50,
		1,
		IsOrganiser: false,
		JoinCode: null,
		new RidePermissions(),
		[new RideMemberSummary(MemberId, "DaveSmith", "Member", FixedInstant, Sharing: true, HasPosition: true, MarkerColour: "#ff8800")]);

	private static MarkerDto SampleMarker() => new(
		Guid.Parse("33333333-3333-3333-3333-333333333333"),
		TrackId: null, GroupRideId: RideId,
		Lat: -3386800, Lon: 15120900,
		Icon: "fuel", Title: "Servo", Note: null,
		DirectionDeg: null, PhotoId: null,
		CreatedByUserId: MemberId, CreatedByUserName: "DaveSmith",
		CreatedUtc: FixedInstant, UpdatedUtc: FixedInstant);

	private static RideRoute SampleRoute() => new(
		Guid.Parse("44444444-4444-4444-4444-444444444444"),
		"The long way", DistanceM: 142_000, PointCount: 4200,
		EncodedPolyline: "_p~iF~ps|U_ulLnnqC_mqNvxq`@",
		Bounds: null, AddedUtc: FixedInstant,
		AddedByUserId: MemberId, AddedByUserName: "DaveSmith");

	private static RiderPositionDto SamplePosition() =>
		new(MemberId, "DaveSmith", -3386810, 15120950, SpeedMps: 22, HeadingDeg: 91, FixedInstant);

	/// <summary>
	/// One store shared across two sessions is how a test spells "the app was restarted": the first
	/// session fills it from a working server, the second opens against a broken one.
	/// </summary>
	private static RideSnapshotCache CacheOver(FakeOfflineStore store) =>
		new(store, new FakeTimeProvider(FixedInstant));

	private static FakeApiClient WorkingServer()
	{
		FakeApiClient api = new()
		{
			RideResult = SampleRide(),
			MarkersResult = [SampleMarker()],
			PositionsResult = [SamplePosition()],
		};
		api.RoutesResult.Add(SampleRoute());
		return api;
	}

	private static RideSession SessionOver(FakeApiClient api, RideSnapshotCache cache) =>
		new(api,
			new FakeRideHubClient(),
			new AuthState(api, new FakeTokenStore(), new FakeTimeProvider(FixedInstant)),
			broadcast: null,
			cache);

	[Fact]
	public async Task ARideThatLoaded_IsKeptOnTheDevice()
	{
		FakeOfflineStore store = new();

		RideSession session = SessionOver(WorkingServer(), CacheOver(store));
		await session.LoadAsync(RideId);

		session.LoadedFromCache.ShouldBeFalse("this one came off the wire.");
		session.CachedUtc.ShouldBeNull();
		store.Contains("ride-" + RideId.ToString("N")).ShouldBeTrue(
			"§4.4: a load that fully succeeded is the only chance to record what the adventure looked like.");
	}

	[Fact]
	public async Task WithNoNetwork_TheRideOpensFromTheDevicesOwnCopy()
	{
		FakeOfflineStore store = new();

		// The ride was opened once, with signal.
		await SessionOver(WorkingServer(), CacheOver(store)).LoadAsync(RideId);

		// And now the phone is in a dead zone: no status, no ProblemDetails, no server.
		FakeApiClient offline = new()
		{
			RideException = new HttpRequestException("No such host is known."),
		};

		RideSession session = SessionOver(offline, CacheOver(store));
		await session.LoadAsync(RideId);

		session.LoadedFromCache.ShouldBeTrue();
		session.CachedUtc.ShouldBe(FixedInstant, "the map has to be able to say how old the copy is.");
		session.RideUnavailable.ShouldBeFalse(
			"§5.2: an adventure must never be forgotten because a phone went through a tunnel.");
		session.Error.ShouldBeNull(
			"a screen showing an adventure does not also need a transport exception's wording over the top of it.");

		// Everything GroupRideLive draws, back off the device.
		session.Ride.ShouldNotBeNull();
		session.Ride.Name.ShouldBe("Old Pacific Highway");
		session.Ride.Members.Single().MarkerColour.ShouldBe("#ff8800", "the roster carries the map's labels and colours.");
		session.Markers.Count.ShouldBe(1);
		session.Markers.Values.Single().Icon.ShouldBe("fuel");
		session.Routes.Count.ShouldBe(1);
		session.Positions.Count.ShouldBe(1);
		session.RoutePolyline.ShouldNotBeNull("the cached line still has to decode into points for the gap list (§5.4).");
		session.RoutePolyline.Count.ShouldBeGreaterThan(0);
	}

	[Fact]
	public async Task WithNoNetworkAndNoStoredCopy_TheErrorStands()
	{
		FakeApiClient offline = new()
		{
			RideException = new HttpRequestException("No such host is known."),
		};

		RideSession session = SessionOver(offline, CacheOver(new FakeOfflineStore()));
		await session.LoadAsync(RideId);

		session.LoadedFromCache.ShouldBeFalse();
		session.Ride.ShouldBeNull();
		session.Error.ShouldNotBeNull(
			"with nothing to draw, 'the request failed' is still the whole truth and must not be swallowed.");
	}

	[Fact]
	public async Task ARideTheServerSaysIsGone_IsNotOpenedFromTheCache_AndTheCopyGoes()
	{
		FakeOfflineStore store = new();

		await SessionOver(WorkingServer(), CacheOver(store)).LoadAsync(RideId);
		store.Count.ShouldBe(1);

		// The server answered. This rider is not on this ride any more (§5.2).
		FakeApiClient removed = new()
		{
			RideException = new ApiException(new ApiError(HttpStatusCode.NotFound, "No such adventure.", [])),
		};

		RideSession session = SessionOver(removed, CacheOver(store));
		await session.LoadAsync(RideId);

		session.RideUnavailable.ShouldBeTrue();
		session.LoadedFromCache.ShouldBeFalse(
			"a removal has to be able to stop a traveller opening the adventure, and a cache that outlived it would not let it.");
		session.Ride.ShouldBeNull();
		store.Count.ShouldBe(0, "the copy goes with the membership.");
	}

	[Fact]
	public async Task AServerHavingABadMinute_StillOpensFromTheCache()
	{
		FakeOfflineStore store = new();

		await SessionOver(WorkingServer(), CacheOver(store)).LoadAsync(RideId);

		// A 500 is the server answering, but not with a ride — and it says nothing about whether
		// this rider is still on it. Same situation as a tunnel, so the same answer.
		FakeApiClient broken = new()
		{
			RideException = new ApiException(new ApiError(HttpStatusCode.InternalServerError, "Something went wrong.", [])),
		};

		RideSession session = SessionOver(broken, CacheOver(store));
		await session.LoadAsync(RideId);

		session.LoadedFromCache.ShouldBeTrue();
		session.RideUnavailable.ShouldBeFalse();
		store.Count.ShouldBe(1, "a 500 is not a reason to throw away the only copy of the adventure.");
	}

	[Fact]
	public async Task LeavingARide_DropsItsCopy()
	{
		FakeOfflineStore store = new();
		FakeApiClient api = WorkingServer();

		RideSession session = SessionOver(api, CacheOver(store));
		await session.LoadAsync(RideId);
		store.Count.ShouldBe(1);

		(await session.LeaveRideAsync()).ShouldBeTrue();

		store.Count.ShouldBe(0,
			"§5.2: getting back on takes a join code or a request, so the device must not keep a copy in the meantime.");
	}
}
