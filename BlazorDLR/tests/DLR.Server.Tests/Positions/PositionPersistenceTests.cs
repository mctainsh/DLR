using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
using DLR.Server.Data.Positions;
using DLR.Server.Data.Rides;
using DLR.Server.Positions;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DLR.Server.Tests.Positions;

/// <summary>
/// The parts of §5.5 that only a real PostgreSQL can answer: the upsert's <c>WHERE</c> guard, and
/// what comes back into the cache after a restart.
/// </summary>
public sealed class PositionPersistenceTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";

	/// <summary>
	/// The guard that makes the flush idempotent (§5.5). Without it, a slow flush overlapping a
	/// fast one moves every rider backwards in time and nothing downstream ever reports it — the
	/// map is simply wrong for ten seconds.
	/// </summary>
	[Fact]
	public async Task Flush_DoesNotOverwriteNewerRowInDatabase()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(Guid rideId, Guid riderId) = await LiveRideAsync(app);

		DateTimeOffset now = DlrWebApplicationFactory.DefaultStart;

		// A newer row is already down — the state a retried or overlapping batch finds.
		await WriteAsync(app, rideId, riderId, lat: 500, at: now);

		// And an older one is flushed on top of it.
		await WriteAsync(app, rideId, riderId, lat: 999, at: now.AddSeconds(-30));

		RiderPosition stored = await app.WithDatabaseAsync(database =>
			database.Set<RiderPosition>().SingleAsync());

		stored.Lat.ShouldBe(500, "the older fix must not regress the newer row");
		stored.RecordedUtc.ShouldBe(now);

		// And a genuinely newer one still lands, or the guard would be a write-once bug.
		await WriteAsync(app, rideId, riderId, lat: 777, at: now.AddSeconds(30));

		(await app.WithDatabaseAsync(database =>
			database.Set<RiderPosition>().SingleAsync())).Lat.ShouldBe(777);
	}

	/// <summary>
	/// A restart mid-ride must not blank the map for the riders the feature exists to protect
	/// (§5.5).
	/// </summary>
	[Fact]
	public async Task Restart_RehydratesTheCacheFromTheDatabase()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateLiveRideAsync(app, organiser);

		await ShareAsync(organiser, ride.Id);
		await PublishAsync(organiser, -33.86, 151.20);

		await app.FlushPositionsAsync();

		await using DlrWebApplicationFactory restarted = app.Restart();

		using HttpClient after = await SignedInAsync(restarted, "SamJones");

		// A fresh process, and the position is on the map before anybody has published again.
		await JoinAsync(after, ride.JoinCode!);

		List<RiderPositionDto> visible =
			(await after.GetFromJsonAsync<List<RiderPositionDto>>($"{RidesUrl}/{ride.Id}/positions"))!;

		visible.ShouldHaveSingleItem().Lat.ShouldBe(PositionScale.FromDegrees(-33.86));
	}

	/// <summary>Rule 1: live rides only. A finished ride's pins must not come back (§5.5).</summary>
	[Fact]
	public async Task Rehydrate_LoadsLiveRidesOnly()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(Guid live, Guid liveRider) = await LiveRideAsync(app);
		(Guid completed, Guid completedRider) = await LiveRideAsync(app, state: GroupRideState.Completed);

		DateTimeOffset now = DlrWebApplicationFactory.DefaultStart;

		await WriteAsync(app, live, liveRider, lat: 1, at: now);
		await WriteAsync(app, completed, completedRider, lat: 2, at: now);

		RiderPositionCache cache = await RehydrateAsync(app, now);

		cache.ForRide(live).ShouldHaveSingleItem();

		cache.ForRide(completed).ShouldBeEmpty(
			"an adventure that has ended is not sharing, and its rows are on their way out anyway");
	}

	/// <summary>
	/// Rule 2: the freshness gate. A stale point must not reappear on the map as if it were
	/// current — which is exactly what a restart after a long outage would otherwise do (§5.5).
	/// </summary>
	[Fact]
	public async Task Rehydrate_SkipsPositionsOlderThanStalenessWindow()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(Guid rideId, Guid fresh) = await LiveRideAsync(app);

		Guid stale = await AddRiderAsync(app, rideId, "StaleSam");

		DateTimeOffset now = DlrWebApplicationFactory.DefaultStart;

		await WriteAsync(app, rideId, fresh, lat: 1, at: now.AddMinutes(-5));
		await WriteAsync(app, rideId, stale, lat: 2, at: now.AddMinutes(-45));

		RiderPositionCache cache = await RehydrateAsync(app, now);

		cache.ForRide(rideId).Keys.ShouldBe([fresh]);
	}

	/// <summary>
	/// Rule 3. Otherwise startup immediately schedules a pointless write of everything it has just
	/// read — every restart paying a full-table round trip for nothing (§5.5).
	/// </summary>
	[Fact]
	public async Task Rehydrate_LoadedEntriesAreNotDirty()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(Guid rideId, Guid riderId) = await LiveRideAsync(app);

		DateTimeOffset now = DlrWebApplicationFactory.DefaultStart;

		await WriteAsync(app, rideId, riderId, lat: 1, at: now);

		RiderPositionCache cache = await RehydrateAsync(app, now);

		cache.ForRide(rideId)[riderId].IsDirty.ShouldBeFalse();

		cache.Dirty().ShouldBeEmpty();
	}

	/// <summary>Rule 4, on the failure path — where it is easiest to forget and worst to omit.</summary>
	[Fact]
	public async Task Rehydrate_WhenTheDatabaseIsUnreachable_StillOpensTheGate()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		FakeTimeProvider clock = new(DlrWebApplicationFactory.DefaultStart);

		RiderPositionCache cache = new(clock);

		ServiceCollection empty = new();

		// A scope that cannot produce a DlrDbContext, which is what a dead database looks like
		// from in here.
		PositionCacheRehydrator rehydrator = new(
			cache,
			empty.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
			clock,
			Options.Create(new RideOptions()),
			NullLogger<PositionCacheRehydrator>.Instance);

		await rehydrator.RehydrateAsync();

		await cache.ReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

		cache.ReadyAsync().IsCompletedSuccessfully.ShouldBeTrue(
			"a server that cannot warm its cache still has to answer requests — if the gate " +
			"stayed shut, every read would hang forever");
	}

	private static async Task<RiderPositionCache> RehydrateAsync(
		DlrWebApplicationFactory app,
		DateTimeOffset now)
	{
		FakeTimeProvider clock = new(now);

		RiderPositionCache cache = new(clock);

		PositionCacheRehydrator rehydrator = new(
			cache,
			app.Services.GetRequiredService<IServiceScopeFactory>(),
			clock,
			Options.Create(new RideOptions()),
			NullLogger<PositionCacheRehydrator>.Instance);

		await rehydrator.RehydrateAsync();

		return cache;
	}

	/// <summary>Writes through the real writer, which is the point of these tests.</summary>
	private static async Task WriteAsync(
		DlrWebApplicationFactory app,
		Guid rideId,
		Guid userId,
		int lat,
		DateTimeOffset at)
	{
		using IServiceScope scope = app.Services.CreateScope();

		IPositionWriter writer = scope.ServiceProvider.GetRequiredService<IPositionWriter>();

		await writer.WriteAsync(
			[new DirtyPosition(rideId, userId, new PositionEntry(lat, 2, null, null, null, at, true))],
			CancellationToken.None);
	}

	private static async Task<(Guid RideId, Guid RiderId)> LiveRideAsync(
		DlrWebApplicationFactory app,
		GroupRideState state = GroupRideState.Live)
	{
		using HttpClient organiser = await SignedInAsync(app, $"Dave{Guid.NewGuid():N}"[..16]);

		RideDetail ride = await CreateLiveRideAsync(app, organiser, state);

		Guid ownerId = await app.WithDatabaseAsync(database =>
			database.Set<GroupRideMember>()
				.Where(member => member.GroupRideId == ride.Id)
				.Select(member => member.UserId)
				.SingleAsync());

		return (ride.Id, ownerId);
	}

	private static async Task<Guid> AddRiderAsync(
		DlrWebApplicationFactory app,
		Guid rideId,
		string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		await app.WithDatabaseAsync(async database =>
		{
			database.Add(new GroupRideMember
			{
				GroupRideId = rideId,
				UserId = session.User.Id,
				Role = GroupRideRole.Rider,
				JoinedUtc = DlrWebApplicationFactory.DefaultStart,
				ShareLocation = true,
			});

			await database.SaveChangesAsync();
		});

		return session.User.Id;
	}

	private static async Task<RideDetail> CreateLiveRideAsync(
		DlrWebApplicationFactory app,
		HttpClient organiser,
		GroupRideState state = GroupRideState.Live)
	{
		using HttpResponseMessage response = await organiser.PostAsJsonAsync(
			RidesUrl,
			new CreateRideRequest(
				"Saturday hills",
				DlrWebApplicationFactory.DefaultStart.AddDays(3),
				JoinPolicy: JoinPolicyDto.Open));

		RideDetail ride = (await response.Content.ReadFromJsonAsync<RideDetail>())!;

		await app.WithDatabaseAsync(async database =>
			await database.Set<GroupRide>()
				.Where(row => row.Id == ride.Id)
				.ExecuteUpdateAsync(row => row.SetProperty(x => x.State, state)));

		return ride;
	}

	private static async Task ShareAsync(HttpClient client, Guid rideId) =>
		await client.PutAsJsonAsync($"{RidesUrl}/{rideId}/sharing/me", new SetSharingRequest(true));

	private static async Task PublishAsync(HttpClient client, double lat, double lon) =>
		await client.PostAsJsonAsync(
			"/api/v1/positions",
			new PositionUpdate(
				PositionScale.FromDegrees(lat),
				PositionScale.FromDegrees(lon),
				DlrWebApplicationFactory.DefaultStart));

	private static async Task JoinAsync(HttpClient client, string code) =>
		await client.PostAsJsonAsync($"{RidesUrl}/join", new JoinByCodeRequest(code));

	private static async Task<HttpClient> SignedInAsync(DlrWebApplicationFactory app, string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}
}
