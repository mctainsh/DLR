using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
using DLR.Server.Data.Positions;
using DLR.Server.Data.Rides;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Rides;

/// <summary>
/// Being in several rides at once (§5.7).
/// <para>
/// A weekend away with a big organised event running inside a small group of mates is the ordinary
/// case, not a corner one. The rider publishes <em>once</em> and the server decides which rides the
/// fix lands in, by each ride's own consent flag — because a client making that decision is a
/// client that can get it wrong in the direction that leaks.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MultiRideTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";

	/// <summary>
	/// Consent is filtered on the write. A rider sharing with A and not B has <strong>no row in
	/// B at all</strong> — not a hidden pin, because a hidden pin is still in the database, still
	/// in the fan-out and still on the wire.
	/// </summary>
	[Fact]
	public async Task Publish_SharingInRideAOnly_StoresNoRowForRideB()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail sunday = await LiveRideAsync(app, organiser);
		RideDetail charity = await LiveRideAsync(app, organiser);

		await JoinAsync(rider, sunday.JoinCode!);
		await JoinAsync(rider, charity.JoinCode!);

		// Shares with the regular group, not with the ride full of strangers. One global switch
		// could not express this, which is why the flag is per membership (§5.6).
		await ShareAsync(rider, sunday.Id, share: true);
		await ShareAsync(rider, charity.Id, share: false);

		PublishResult result = await PublishAsync(rider, -33.86, 151.20);

		result.RideIds.ShouldBe([sunday.Id]);

		await app.FlushPositionsAsync();

		Guid riderId = await IdOfAsync(app, "SamJones");

		List<Guid> stored = await app.WithDatabaseAsync(database =>
			database.Set<RiderPosition>()
				.Where(position => position.UserId == riderId)
				.Select(position => position.GroupRideId)
				.ToListAsync());

		stored.ShouldBe([sunday.Id], "no row in B at all — not a hidden one");

		// And nothing of theirs is visible to the charity ride's members either.
		List<RiderPositionDto> seen =
			(await organiser.GetFromJsonAsync<List<RiderPositionDto>>(
				$"{RidesUrl}/{charity.Id}/positions"))!;

		seen.ShouldBeEmpty();
	}

	/// <summary>
	/// One publish, many fan-outs (§5.7). The uplink stays flat, which is the half that matters
	/// for battery.
	/// </summary>
	[Fact]
	public async Task Publish_MemberOfThreeLiveRides_WritesToAllThree()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		List<Guid> rides = [];

		for (int index = 0; index < 3; index++)
		{
			RideDetail ride = await LiveRideAsync(app, organiser);

			await JoinAsync(rider, ride.JoinCode!);
			await ShareAsync(rider, ride.Id, share: true);

			rides.Add(ride.Id);
		}

		// One call. Not one per ride — publishing per ride would multiply the rider's uplink and
		// battery by the number of rides, for data the server can trivially copy.
		PublishResult result = await PublishAsync(rider, -33.86, 151.20);

		result.RideIds.Order().ShouldBe(rides.Order());

		await app.FlushPositionsAsync();

		foreach (Guid rideId in rides)
		{
			List<RiderPositionDto> seen =
				(await organiser.GetFromJsonAsync<List<RiderPositionDto>>(
					$"{RidesUrl}/{rideId}/positions"))!;

			seen.ShouldContain(
				position => position.UserName == "SamJones",
				$"ride {rideId} should have the fix");
		}
	}

	/// <summary>
	/// Unbounded means a rider can be broadcast into fifty groups at once (§5.7). Enforced when a
	/// ride goes <c>Live</c> rather than at join: being a <em>member</em> of many rides is fine.
	/// </summary>
	[Fact]
	public async Task LiveRideCap_ExceedingMaxConcurrent_IsRejectedAtRideStart()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: new Dictionary<string, string?>
			{
				["Ride:MaxConcurrentLiveRidesPerUser"] = "2",
			});

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		for (int index = 0; index < 2; index++)
		{
			RideDetail allowed = await CreateRideAsync(organiser);

			using HttpResponseMessage started = await StartAsync(organiser, allowed.Id);

			started.StatusCode.ShouldBe(HttpStatusCode.NoContent, $"ride {index + 1} is inside the cap");
		}

		RideDetail third = await CreateRideAsync(organiser);

		using HttpResponseMessage refused = await StartAsync(organiser, third.Id);

		refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);

		(await refused.Content.ReadAsStringAsync()).ShouldContain("already live in 2");

		// The ride is untouched — a refused start must not leave it half-started.
		RideDetail after = (await organiser.GetFromJsonAsync<RideDetail>($"{RidesUrl}/{third.Id}"))!;

		after.State.ShouldBe(RideStateDto.Open);
	}

	/// <summary>
	/// Membership is not the thing being capped. Joining a sixth ride while five are live is
	/// ordinary, and only <em>starting</em> another one is refused.
	/// </summary>
	[Fact]
	public async Task LiveRideCap_DoesNotStopJoiningMoreRides()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: new Dictionary<string, string?>
			{
				["Ride:MaxConcurrentLiveRidesPerUser"] = "1",
			});

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		for (int index = 0; index < 3; index++)
		{
			RideDetail ride = await LiveRideAsync(app, organiser);

			await JoinAsync(rider, ride.JoinCode!);
			await ShareAsync(rider, ride.Id, share: true);
		}

		// Three live rides, all joined, all sharing — none of it blocked by the cap.
		(await PublishAsync(rider, -33.86, 151.20)).RideIds.Count.ShouldBe(3);
	}

	/// <summary>
	/// A ride that is not <c>Live</c> takes no positions, whatever the consent flag says. The
	/// lifecycle is the outer gate and consent is the inner one.
	/// </summary>
	[Fact]
	public async Task Publish_RideNotYetLive_StoresNothing()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		await ShareAsync(organiser, ride.Id, share: true);

		(await PublishAsync(organiser, -33.86, 151.20)).RideIds.ShouldBeEmpty();

		await StartAsync(organiser, ride.Id);

		(await PublishAsync(organiser, -33.86, 151.20)).RideIds.ShouldBe([ride.Id]);
	}

	private static Task<Guid> IdOfAsync(DlrWebApplicationFactory app, string userName) =>
		app.WithDatabaseAsync(database => database.Users
			.Where(user => user.UserName == userName)
			.Select(user => user.Id)
			.SingleAsync());

	private static Task<HttpResponseMessage> StartAsync(HttpClient organiser, Guid rideId) =>
		organiser.PostAsync($"{RidesUrl}/{rideId}/start", content: null);

	private static async Task<PublishResult> PublishAsync(HttpClient client, double lat, double lon)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			"/api/v1/positions",
			new PositionUpdate(
				PositionScale.FromDegrees(lat),
				PositionScale.FromDegrees(lon),
				DlrWebApplicationFactory.DefaultStart));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<PublishResult>())!;
	}

	private static async Task ShareAsync(HttpClient client, Guid rideId, bool share)
	{
		using HttpResponseMessage response = await client.PutAsJsonAsync(
			$"{RidesUrl}/{rideId}/sharing/me",
			new SetSharingRequest(share));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
	}

	private static async Task JoinAsync(HttpClient client, string code)
	{
		using HttpResponseMessage response =
			await client.PostAsJsonAsync($"{RidesUrl}/join", new JoinByCodeRequest(code));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
	}

	private static async Task<RideDetail> CreateRideAsync(HttpClient organiser)
	{
		using HttpResponseMessage response = await organiser.PostAsJsonAsync(
			RidesUrl,
			new CreateRideRequest(
				"Saturday hills",
				DlrWebApplicationFactory.DefaultStart.AddDays(3),
				JoinPolicy: JoinPolicyDto.Open));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<RideDetail>())!;
	}

	/// <summary>Created and started through the real endpoint, so the cap is genuinely in play.</summary>
	private static async Task<RideDetail> LiveRideAsync(
		DlrWebApplicationFactory app,
		HttpClient organiser)
	{
		RideDetail ride = await CreateRideAsync(organiser);

		using HttpResponseMessage started = await StartAsync(organiser, ride.Id);

		if (started.StatusCode != HttpStatusCode.NoContent)
		{
			// The organiser's own cap got in the way of building the fixture. Set the state
			// directly so the test can be about the thing it is named for.
			await app.WithDatabaseAsync(async database =>
				await database.Set<GroupRide>()
					.Where(row => row.Id == ride.Id)
					.ExecuteUpdateAsync(row => row.SetProperty(x => x.State, GroupRideState.Live)));
		}

		return ride;
	}

	private static async Task<HttpClient> SignedInAsync(DlrWebApplicationFactory app, string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}
}
