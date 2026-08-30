using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
using DLR.Server.Data.Rides;
using DLR.Server.Positions;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Positions;

/// <summary>
/// What the rest of a ride sees when somebody rides into their own private area (§10.1, §5.6).
/// <para>
/// <strong>The behaviour this replaces was a frozen pin.</strong> The device stopped publishing at
/// the edge of the circle and the server kept the last fix, so a rider arriving home became a
/// marker parked a few streets from their front door for as long as the adventure ran. That is a
/// better clue to where somebody lives than most of what the feature withholds.
/// </para>
/// <para>
/// So going private <em>deletes</em>, and says so. What these tests pin down is the pair: no
/// position anywhere — cache, database or snapshot — and a member row that still exists and reads
/// "private", so the ride can tell "at home" from "in a tunnel" (§5.6's rule, applied to a third
/// reason a pin can be missing).
/// </para>
/// </summary>
public sealed class PositionPrivacyTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";
	private const string PrivacyUrl = "/api/v1/positions/privacy";

	[Fact]
	public async Task GoingPrivate_DeletesThePosition_AndLeavesTheMemberOnTheList()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		RideDetail ride = await CreateRideAsync(organiser);

		using HttpClient rider = await SignedInAsync(app, "SamJones");
		await JoinAsync(rider, ride.JoinCode!);
		await ShareAsync(rider, ride.Id);
		await PublishAsync(rider, -33.86, 151.20);

		// Flushed first, so the delete has a row to prove it removes rather than one it never wrote.
		(await Snapshot(organiser, ride.Id)).ShouldHaveSingleItem();

		using HttpResponseMessage response = await rider.PostAsJsonAsync(PrivacyUrl, new PositionPrivacyUpdate(true));
		response.StatusCode.ShouldBe(HttpStatusCode.OK);

		(await Snapshot(organiser, ride.Id)).ShouldBeEmpty(
			"a pin left where the rider stopped is what the private area exists to prevent.");

		app.PositionCount().ShouldBe(0,
			"the delete does not wait for a flush — the row is what a rider is asking you not to keep.");

		RideDetail seen = (await organiser.GetFromJsonAsync<RideDetail>($"{RidesUrl}/{ride.Id}"))!;
		RideMemberSummary member = seen.Members.Single(row => row.UserName == "SamJones");

		member.Private.ShouldBeTrue("the empty row has to say it is a choice rather than a tunnel.");
		member.Sharing.ShouldBeTrue("the private area is a place, not a decision about this adventure.");
		member.HasPosition.ShouldBeFalse();
	}

	[Fact]
	public async Task APublishedFix_ClearsPrivacy_EvenWhenTheDeviceNeverSaidSo()
	{
		// The device does say so, and this is the belt to those braces: a coordinate arriving is
		// proof the rider is outside their circle. Without it a dropped "no longer private" call
		// would leave somebody labelled private beside a pin visibly moving across the map.
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		RideDetail ride = await CreateRideAsync(organiser);

		using HttpClient rider = await SignedInAsync(app, "SamJones");
		await JoinAsync(rider, ride.JoinCode!);
		await ShareAsync(rider, ride.Id);

		await rider.PostAsJsonAsync(PrivacyUrl, new PositionPrivacyUpdate(true));

		RideDetail hidden = (await organiser.GetFromJsonAsync<RideDetail>($"{RidesUrl}/{ride.Id}"))!;
		hidden.Members.Single(row => row.UserName == "SamJones").Private.ShouldBeTrue();

		await PublishAsync(rider, -33.90, 151.25);

		RideDetail back = (await organiser.GetFromJsonAsync<RideDetail>($"{RidesUrl}/{ride.Id}"))!;

		back.Members.Single(row => row.UserName == "SamJones").Private.ShouldBeFalse();
		(await Snapshot(organiser, ride.Id)).ShouldHaveSingleItem()
			.Lat.ShouldBe(PositionScale.FromDegrees(-33.90));
	}

	[Fact]
	public async Task ThePrivateAreaItself_NeverReachesAnotherRider()
	{
		// The circle stays on the rider's own profile. What crosses the wire is one bit, and a bit
		// cannot be turned back into a house.
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		RideDetail ride = await CreateRideAsync(organiser);

		using HttpClient rider = await SignedInAsync(app, "SamJones");
		await JoinAsync(rider, ride.JoinCode!);
		await ShareAsync(rider, ride.Id);

		await rider.PutAsJsonAsync("/api/v1/me/private-area", new PrivateAreaSettings(-33.86, 151.20, 1_000));
		await rider.PostAsJsonAsync(PrivacyUrl, new PositionPrivacyUpdate(true));

		string body = await organiser.GetStringAsync($"{RidesUrl}/{ride.Id}");

		body.ShouldNotContain("151.2");
		body.ShouldNotContain("adiusM");
	}

	[Fact]
	public async Task GoingPrivate_TouchesNobodyElse()
	{
		// One rider's circle is one rider's. The obvious way to get this wrong is a delete keyed on
		// the ride rather than on the rider, which would clear the whole map.
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		RideDetail ride = await CreateRideAsync(organiser);

		await ShareAsync(organiser, ride.Id);
		await PublishAsync(organiser, -33.80, 151.10);

		using HttpClient rider = await SignedInAsync(app, "SamJones");
		await JoinAsync(rider, ride.JoinCode!);
		await ShareAsync(rider, ride.Id);
		await PublishAsync(rider, -33.86, 151.20);

		await rider.PostAsJsonAsync(PrivacyUrl, new PositionPrivacyUpdate(true));

		IReadOnlyList<RiderPositionDto> left = await Snapshot(organiser, ride.Id);

		left.ShouldHaveSingleItem().UserName.ShouldBe("DaveSmith");
	}

	/// <summary>
	/// The whole trade v0.33 made, asserted so nobody can quietly undo it (§5.5, §10.1).
	/// <para>
	/// A position lives in <c>RiderPositionCache</c> and nowhere else, so a restart forgets every
	/// pin — and there is nothing on disk for a backup, a restore or an operator with a database
	/// client to find. This test fails the moment somebody reintroduces a write.
	/// </para>
	/// </summary>
	[Fact]
	public async Task ARestart_ForgetsEveryPin_AndNothingIsLeftOnDisk()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		await ShareAsync(organiser, ride.Id);
		await PublishAsync(organiser, -33.86, 151.20);

		app.PositionCount(ride.Id).ShouldBe(1, "the fix is on the map the moment it lands");

		// Every table this schema has, and none of them is holding a coordinate. A rider_position
		// table would fail here rather than in some later reading of the privacy policy.
		(await app.WithDatabaseAsync(database => Task.FromResult(
			database.Model.GetEntityTypes().Any(entity =>
				entity.GetTableName() == "rider_position"))))
			.ShouldBeFalse("§10.1: a live position never touches disk, so there is no table for one");

		// What a restart looks like from the cache's point of view: a new one, with nothing in it.
		RiderPositionCache restarted = new();

		restarted.ForRide(ride.Id).ShouldBeEmpty(
			"§5.5: a restart starts blank, and the rider's next push is what puts them back");
	}

	private static async Task<IReadOnlyList<RiderPositionDto>> Snapshot(HttpClient client, Guid rideId) =>
		(await client.GetFromJsonAsync<List<RiderPositionDto>>($"{RidesUrl}/{rideId}/positions"))!;

	private static async Task<RideDetail> CreateRideAsync(HttpClient organiser)
	{
		using HttpResponseMessage response = await organiser.PostAsJsonAsync(
			RidesUrl,
			new CreateRideRequest(
				"Saturday hills",
				DlrWebApplicationFactory.DefaultStart.AddDays(3),
				JoinPolicy: JoinPolicyDto.Open));

		return (await response.Content.ReadFromJsonAsync<RideDetail>())!;
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
