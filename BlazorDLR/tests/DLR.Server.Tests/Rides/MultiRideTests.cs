using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
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
/// fix lands in, by each ride's own consent flag - because a client making that decision is a
/// client that can get it wrong in the direction that leaks.
/// </para>
/// </summary>
public sealed class MultiRideTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";

	/// <summary>
	/// Consent is filtered on the write. A rider sharing with A and not B has <strong>no row in
	/// B at all</strong> - not a hidden pin, because a hidden pin is still in the database, still
	/// in the fan-out and still on the wire.
	/// </summary>
	[Fact]
	public async Task Publish_SharingInRideAOnly_StoresNoRowForRideB()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail sunday = await CreateRideAsync(organiser);
		RideDetail charity = await CreateRideAsync(organiser);

		await JoinAsync(rider, sunday.JoinCode!);
		await JoinAsync(rider, charity.JoinCode!);

		// Shares with the regular group, not with the ride full of strangers. One global switch
		// could not express this, which is why the flag is per membership (§5.6).
		await ShareAsync(rider, sunday.Id, share: true);
		await ShareAsync(rider, charity.Id, share: false);

		PublishResult result = await PublishAsync(rider, -33.86, 151.20);

		result.RideIds.ShouldBe([sunday.Id]);

		app.PositionCount(sunday.Id).ShouldBe(1);
		app.PositionCount(charity.Id).ShouldBe(0, "nothing held in B at all - not a hidden pin");

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
			RideDetail ride = await CreateRideAsync(organiser);

			await JoinAsync(rider, ride.JoinCode!);
			await ShareAsync(rider, ride.Id, share: true);

			rides.Add(ride.Id);
		}

		// One call. Not one per ride - publishing per ride would multiply the rider's uplink and
		// battery by the number of rides, for data the server can trivially copy.
		PublishResult result = await PublishAsync(rider, -33.86, 151.20);

		result.RideIds.Order().ShouldBe(rides.Order());

		foreach (Guid rideId in rides)
		{
			List<RiderPositionDto> seen =
				(await organiser.GetFromJsonAsync<List<RiderPositionDto>>(
					$"{RidesUrl}/{rideId}/positions"))!;

			seen.ShouldContain(
				position => position.UserName == "SamJones",
				$"adventure {rideId} should have the fix");
		}
	}

	private static Task<Guid> IdOfAsync(DlrWebApplicationFactory app, string userName) =>
		app.WithDatabaseAsync(database => database.Users
			.Where(user => user.UserName == userName)
			.Select(user => user.Id)
			.SingleAsync());

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

	private static async Task<HttpClient> SignedInAsync(DlrWebApplicationFactory app, string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}
}
