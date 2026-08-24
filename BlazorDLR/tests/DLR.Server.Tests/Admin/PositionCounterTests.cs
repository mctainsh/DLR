using System.Net.Http.Json;
using DLR.Core.Contracts.Admin;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
using DLR.Server.Data.Identity;
using DLR.Server.Data.Rides;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Admin;

/// <summary>
/// The lifetime GPS counter (§14.6).
/// <para>
/// The number exists because the rows do not: positions are swept as soon as the ride carrying
/// them stops being live, so a count of <c>rider_position</c> answers a different question. These
/// tests are about the two properties that gives the counter — that it counts a <em>fix</em>
/// rather than a flushed row, and that it survives the sweep that deletes what it counted.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PositionCounterTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";

	/// <summary>
	/// Three fixes in one flush period is three, not one.
	/// <para>
	/// The flush upserts one row per rider per ride and coalesces a whole period into it, so a
	/// counter that incremented per written row would report a rider publishing at 1 Hz as one fix
	/// every ten seconds — an order of magnitude out, and silently.
	/// </para>
	/// </summary>
	[Fact]
	public async Task EveryFixIsCounted_NotEveryFlushedRow()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(_, HttpClient rider, Guid riderId) = await LiveRiderAsync(app);

		using (rider)
		{
			// Three fixes inside one flush period, which the upsert collapses into a single row.
			await PublishAsync(rider, -33.85, 151.21);
			await PublishAsync(rider, -33.86, 151.22);
			await PublishAsync(rider, -33.87, 151.23);

			await app.FlushPositionsAsync();

			long counted = await app.WithDatabaseAsync(database =>
				database.Set<AppUser>()
					.Where(user => user.Id == riderId)
					.Select(user => user.PositionsRecorded)
					.SingleAsync());

			counted.ShouldBe(3);

			// And one row, which is the thing that makes the counter necessary rather than
			// redundant — the two numbers are deliberately different.
			int stored = await app.WithDatabaseAsync(database =>
				database.Set<DLR.Server.Data.Positions.RiderPosition>()
					.CountAsync(position => position.UserId == riderId));

			stored.ShouldBe(1);
		}
	}

	/// <summary>
	/// The counter has to outlive the rows. This is the whole reason it is a column rather than a
	/// query, so it is asserted across the sweep that does the deleting.
	/// </summary>
	[Fact]
	public async Task TheCounterSurvivesTheSweepThatDeletesThePositions()
	{
		Dictionary<string, string?> settings = AdminRosterSettings.Roster("TheAdmin");

		// The sweep deletes nothing until this is off (§7.11), and this test is about what
		// survives a real one.
		settings["Maintenance:DryRun"] = "false";

		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: settings);

		(Guid rideId, HttpClient rider, Guid riderId) = await LiveRiderAsync(app);

		using (rider)
		{
			await PublishAsync(rider, -33.85, 151.21);
			await PublishAsync(rider, -33.86, 151.22);

			await app.FlushPositionsAsync();
		}

		// The ride ends, and the nightly sweep takes every position with it (§5.5).
		await app.WithDatabaseAsync(async database =>
			await database.Set<GroupRide>()
				.Where(row => row.Id == rideId)
				.ExecuteUpdateAsync(row => row.SetProperty(x => x.State, GroupRideState.Completed)));

		await app.RunMaintenanceAsync();

		int remaining = await app.WithDatabaseAsync(database =>
			database.Set<DLR.Server.Data.Positions.RiderPosition>()
				.CountAsync(position => position.UserId == riderId));

		remaining.ShouldBe(0, "the sweep is what makes the counter necessary.");

		// And the administration screen still knows the rider was out there.
		using HttpClient admin = await SignedInAsync(app, "TheAdmin");

		IReadOnlyList<AdminUserRow> rows =
			(await admin.GetFromJsonAsync<List<AdminUserRow>>("/api/v1/admin/users"))!;

		AdminUserRow row = rows.Single(entry => entry.UserId == riderId);

		row.PositionsRecorded.ShouldBe(2);
		row.PositionsHeld.ShouldBe(0);
	}

	/// <summary>
	/// A rider sharing with two adventures publishes one fix and writes two rows. Counting the rows
	/// would say they rode twice as far as the rider beside them who joined one ride.
	/// </summary>
	[Fact]
	public async Task OneFixSharedWithTwoAdventures_CountsOnce()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(_, HttpClient rider, Guid riderId) = await LiveRiderAsync(app);

		using (rider)
		{
			// A second live ride the same rider shares with.
			using HttpClient organiser = await SignedInAsync(app, $"Org{Guid.NewGuid():N}"[..12]);

			RideDetail second = await CreateLiveRideAsync(app, organiser);

			await app.WithDatabaseAsync(async database =>
			{
				database.Add(new GroupRideMember
				{
					GroupRideId = second.Id,
					UserId = riderId,
					Role = GroupRideRole.Rider,
					JoinedUtc = DlrWebApplicationFactory.DefaultStart,
					ShareLocation = true,
				});

				await database.SaveChangesAsync();
			});

			await PublishAsync(rider, -33.85, 151.21);

			await app.FlushPositionsAsync();

			long counted = await app.WithDatabaseAsync(database =>
				database.Set<AppUser>()
					.Where(user => user.Id == riderId)
					.Select(user => user.PositionsRecorded)
					.SingleAsync());

			counted.ShouldBe(1, "one fix left the phone, whatever it landed in.");

			int stored = await app.WithDatabaseAsync(database =>
				database.Set<DLR.Server.Data.Positions.RiderPosition>()
					.CountAsync(position => position.UserId == riderId));

			stored.ShouldBe(2, "and it is on both maps, which is why the row count is not the answer.");
		}
	}

	// ---------- helpers ----------

	private static async Task<(Guid RideId, HttpClient Rider, Guid RiderId)> LiveRiderAsync(
		DlrWebApplicationFactory app)
	{
		using HttpClient organiser = await SignedInAsync(app, $"Dave{Guid.NewGuid():N}"[..14]);

		RideDetail ride = await CreateLiveRideAsync(app, organiser);

		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync($"Rider{Guid.NewGuid():N}"[..14]);

		await app.WithDatabaseAsync(async database =>
		{
			database.Add(new GroupRideMember
			{
				GroupRideId = ride.Id,
				UserId = session.User.Id,
				Role = GroupRideRole.Rider,
				JoinedUtc = DlrWebApplicationFactory.DefaultStart,
				ShareLocation = true,
			});

			await database.SaveChangesAsync();
		});

		return (ride.Id, app.CreateClient().Authenticated(session), session.User.Id);
	}

	private static async Task<RideDetail> CreateLiveRideAsync(
		DlrWebApplicationFactory app,
		HttpClient organiser)
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
				.ExecuteUpdateAsync(row => row.SetProperty(x => x.State, GroupRideState.Live)));

		return ride;
	}

	private static async Task PublishAsync(HttpClient client, double lat, double lon) =>
		await client.PostAsJsonAsync(
			"/api/v1/positions",
			new PositionUpdate(
				PositionScale.FromDegrees(lat),
				PositionScale.FromDegrees(lon),
				DlrWebApplicationFactory.DefaultStart));

	private static async Task<HttpClient> SignedInAsync(DlrWebApplicationFactory app, string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}
}
