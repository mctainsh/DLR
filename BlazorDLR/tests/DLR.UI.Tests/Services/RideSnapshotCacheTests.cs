using BlazorDLR.Shared.Services;
using BlazorDLR.Shared.Services.Platform;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Rides;
using DLR.UI.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace DLR.UI.Tests.Services;

/// <summary>
/// §4.4's device-local copy of a ride. The property under test throughout is that what goes in
/// comes back out unchanged: the live map draws rider labels and colours off the member row and
/// its lines off an encoded polyline (§15.5, §16.3), so a snapshot that loses any of those opens
/// the ride on a map that is subtly wrong rather than obviously empty.
/// </summary>
public sealed class RideSnapshotCacheTests
{
	// ClockRules forbids an ambient clock read in test source (§10.4).
	private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

	private static readonly Guid RideId = Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly Guid MemberId = Guid.Parse("22222222-2222-2222-2222-222222222222");

	private static RideDetail SampleRide() => new(
		RideId,
		"Old Pacific Highway",
		"Meet at the servo",
		FixedInstant,
		JoinPolicyDto.Approval,
		50,
		1,
		IsOrganiser: true,
		JoinCode: "ABC123",
		new RidePermissions(),
		[new RideMemberSummary(MemberId, "DaveSmith", "Leader", FixedInstant, Sharing: true, HasPosition: true, MarkerColour: "#ff8800")]);

	private static (RideSnapshotCache Cache, FakeOfflineStore Store) Build()
	{
		FakeOfflineStore store = new();
		return (new RideSnapshotCache(store, new FakeTimeProvider(FixedInstant)), store);
	}

	[Fact]
	public async Task Write_ThenRead_ReturnsEverythingTheLiveMapDraws()
	{
		(RideSnapshotCache cache, _) = Build();

		MarkerDto marker = new(
			Guid.NewGuid(), TrackId: null, GroupRideId: RideId,
			Lat: -3386800, Lon: 15120900,
			Icon: "fuel", Title: "Servo", Note: "Last one before the hills",
			DirectionDeg: null, PhotoId: null,
			CreatedByUserId: MemberId, CreatedByUserName: "DaveSmith",
			CreatedUtc: FixedInstant, UpdatedUtc: FixedInstant);

		RideRoute route = new(
			Guid.NewGuid(), "The long way", DistanceM: 142_000, PointCount: 4200,
			EncodedPolyline: "_p~iF~ps|U_ulLnnqC_mqNvxq`@",
			Bounds: null, AddedUtc: FixedInstant,
			AddedByUserId: MemberId, AddedByUserName: "DaveSmith");

		RiderPositionDto position = new(MemberId, "DaveSmith", -3386810, 15120950, SpeedMps: 22, HeadingDeg: 91, FixedInstant);

		await cache.WriteAsync(SampleRide(), [marker], [route], [position]);

		RideSnapshot? read = await cache.ReadAsync(RideId);

		read.ShouldNotBeNull();
		read.CachedUtc.ShouldBe(FixedInstant, "the stamp is what the map reports the copy's age from.");

		// The roster - "officers" - with everything the map labels and colours a pin from.
		read.Ride.Name.ShouldBe("Old Pacific Highway");
		read.Ride.Members.Count.ShouldBe(1);
		read.Ride.Members[0].UserName.ShouldBe("DaveSmith");
		read.Ride.Members[0].Role.ShouldBe("Leader");
		read.Ride.Members[0].MarkerColour.ShouldBe("#ff8800",
			"a position batch carries no colour (§5.3) - losing it here would redraw the whole group in the default.");
		read.Ride.Members[0].Sharing.ShouldBeTrue();

		read.Markers.Count.ShouldBe(1);
		read.Markers[0].Icon.ShouldBe("fuel");
		read.Markers[0].Note.ShouldBe("Last one before the hills");

		read.Routes.Count.ShouldBe(1);
		read.Routes[0].EncodedPolyline.ShouldBe("_p~iF~ps|U_ulLnnqC_mqNvxq`@",
			"the line is stored encoded and decoded by PolylineCodec on the way out (§15.5) - a mangled string is a route drawn off the Gulf of Guinea.");

		read.Positions.Count.ShouldBe(1);
		read.Positions[0].Lat.ShouldBe(-3386810);
		read.Positions[0].HeadingDeg.ShouldBe((short)91);
	}

	[Fact]
	public async Task Read_ForARideNeverStored_IsNull()
	{
		(RideSnapshotCache cache, _) = Build();

		(await cache.ReadAsync(Guid.NewGuid())).ShouldBeNull();
	}

	[Fact]
	public async Task Read_ForADifferentRide_IsNull()
	{
		(RideSnapshotCache cache, _) = Build();

		await cache.WriteAsync(SampleRide(), [], [], []);

		(await cache.ReadAsync(Guid.Parse("99999999-9999-9999-9999-999999999999"))).ShouldBeNull(
			"one entry per adventure - a stored adventure must never answer for another one.");
	}

	[Fact]
	public async Task Forget_RemovesTheCopy()
	{
		(RideSnapshotCache cache, FakeOfflineStore store) = Build();

		await cache.WriteAsync(SampleRide(), [], [], []);
		store.Count.ShouldBe(1);

		await cache.ForgetAsync(RideId);

		store.Count.ShouldBe(0);
		(await cache.ReadAsync(RideId)).ShouldBeNull(
			"§5.2: a traveller removed from an adventure must not still be able to open it from a cache.");
	}

	[Fact]
	public async Task AHostThatStoresNothing_ReadsBackNothing()
	{
		// The browser hosts' binding (§18.6). The point is that shared code can call this
		// unconditionally and get the truthful "you have no copy" rather than an exception.
		RideSnapshotCache cache = new(new UnavailableOfflineStore(), new FakeTimeProvider(FixedInstant));

		cache.IsSupported.ShouldBeFalse();

		await cache.WriteAsync(SampleRide(), [], [], []);

		(await cache.ReadAsync(RideId)).ShouldBeNull();
	}

	[Fact]
	public async Task AStoredPayloadThisBuildCannotRead_IsTreatedAsNoCopy()
	{
		FakeOfflineStore store = new();
		RideSnapshotCache cache = new(store, new FakeTimeProvider(FixedInstant));

		// A file truncated by a phone killed mid-write, which is the ordinary way this happens.
		await store.WriteAsync("ride-" + RideId.ToString("N"), "{\"version\":1,\"ride\":{\"id\"");

		(await cache.ReadAsync(RideId)).ShouldBeNull(
			"a cache must never turn a bad payload into a failed screen - the caller falls back to the network.");
	}

	[Fact]
	public async Task ASnapshotFromAnOlderFormat_IsDiscarded()
	{
		FakeOfflineStore store = new();
		RideSnapshotCache cache = new(store, new FakeTimeProvider(FixedInstant));

		await cache.WriteAsync(SampleRide(), [], [], []);

		// Rewrite the stored entry with a version this build does not speak.
		string name = "ride-" + RideId.ToString("N");
		string stored = (await store.ReadAsync(name))!;
		await store.WriteAsync(name, stored.Replace("\"version\":1", "\"version\":0", StringComparison.Ordinal));

		(await cache.ReadAsync(RideId)).ShouldBeNull(
			"the migration for a cache is to throw it away - the data is one round trip from being refetched.");
	}
}
