using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Moderation;
using DLR.Core.Contracts.Rides;
using DLR.Server.Hubs;
using DLR.Server.Tests.Hubs;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.Server.Tests.Positions;

/// <summary>
/// What a block does to a live map (§16.5, §17.7).
/// <para>
/// <strong>Symmetric, which is the whole point.</strong> Blocking somebody's posts one way round is
/// a reading preference. A block on a map that only worked one way would leave the person you
/// blocked still watching where you are, which is the case the control exists for - and the case
/// App Review asks about.
/// </para>
/// <para>
/// The filter has to hold on <em>both</em> channels, and these tests say so separately: the
/// snapshot a client fetches on load, and the batch the broadcast pushes every five seconds. A pin
/// the snapshot hands over is on the map until the next batch takes it away, and a filter applied
/// to only one of the two is a hole that looks closed.
/// </para>
/// </summary>
public sealed class PositionBlockTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";
	private const string BlocksUrl = "/api/v1/blocks";

	[Fact]
	public async Task Blocking_TakesThePinOffTheSnapshot_BothWays()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		RideDetail ride = await CreateRideAsync(organiser);

		using HttpClient rider = await SignedInAsync(app, "SamJones");
		await JoinAsync(rider, ride.JoinCode!);

		await ShareAsync(organiser, ride.Id);
		await ShareAsync(rider, ride.Id);
		await PublishAsync(organiser, -33.86, 151.20);
		await PublishAsync(rider, -33.87, 151.21);

		// Both on the map before anybody blocks anybody, so the assertions below are about the
		// block rather than about a ride that was empty all along.
		(await Snapshot(organiser, ride.Id)).Count.ShouldBe(2);
		(await Snapshot(rider, ride.Id)).Count.ShouldBe(2);

		Guid riderId = (await Snapshot(organiser, ride.Id))
			.Single(position => position.UserName == "SamJones")
			.UserId;

		using HttpResponseMessage blocked = await organiser.PostAsJsonAsync(BlocksUrl, new BlockUserRequest(riderId));
		blocked.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		IReadOnlyList<RiderPositionDto> organiserSees = await Snapshot(organiser, ride.Id);
		IReadOnlyList<RiderPositionDto> riderSees = await Snapshot(rider, ride.Id);

		organiserSees.ShouldHaveSingleItem().UserName.ShouldBe("DaveSmith",
			"the blocker must not see the traveller they blocked.");

		riderSees.ShouldHaveSingleItem().UserName.ShouldBe("SamJones",
			"and the person blocked must not go on watching the blocker - a one-way block on a map "
			+ "leaves the watching half intact, which is the half that matters.");
	}

	[Fact]
	public async Task Blocking_IsNotAnnounced_AndDoesNotRemoveAnybodyFromTheRide()
	{
		// §16.5: a block that announced itself would turn a quiet decision into the confrontation
		// it exists to avoid. The member row stays - they are still on the adventure - and only the
		// position goes.
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		RideDetail ride = await CreateRideAsync(organiser);

		using HttpClient rider = await SignedInAsync(app, "SamJones");
		await JoinAsync(rider, ride.JoinCode!);
		await ShareAsync(rider, ride.Id);
		await PublishAsync(rider, -33.87, 151.21);

		Guid riderId = (await Snapshot(organiser, ride.Id)).Single().UserId;

		await organiser.PostAsJsonAsync(BlocksUrl, new BlockUserRequest(riderId));

		RideDetail blockerSees = (await organiser.GetFromJsonAsync<RideDetail>($"{RidesUrl}/{ride.Id}"))!;
		RideMemberSummary blockedRow = blockerSees.Members.Single(row => row.UserName == "SamJones");

		blockedRow.Blocked.ShouldBeTrue("the reader's own list says which rows are their own doing.");
		blockedRow.HasPosition.ShouldBeFalse("no fix for them reaches this reader on any channel.");
		blockedRow.Sharing.ShouldBeTrue("the block is the reader's decision, not a change to theirs.");

		// The other side is told nothing at all.
		RideDetail blockedSees = (await rider.GetFromJsonAsync<RideDetail>($"{RidesUrl}/{ride.Id}"))!;

		blockedSees.Members
			.Single(row => row.UserName == "DaveSmith")
			.Blocked
			.ShouldBeTrue("the map hides them both ways, so the row has to explain the empty pin.");
	}

	[Fact]
	public async Task Unblocking_PutsThePinBack()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		RideDetail ride = await CreateRideAsync(organiser);

		using HttpClient rider = await SignedInAsync(app, "SamJones");
		await JoinAsync(rider, ride.JoinCode!);
		await ShareAsync(rider, ride.Id);
		await PublishAsync(rider, -33.87, 151.21);

		Guid riderId = (await Snapshot(organiser, ride.Id)).Single().UserId;

		await organiser.PostAsJsonAsync(BlocksUrl, new BlockUserRequest(riderId));
		(await Snapshot(organiser, ride.Id)).ShouldBeEmpty();

		using HttpResponseMessage unblocked = await organiser.DeleteAsync($"{BlocksUrl}/{riderId}");
		unblocked.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		// Nothing was deleted from the cache on the way in, so there is nothing to wait for on the
		// way out - the pin is simply not filtered any more.
		(await Snapshot(organiser, ride.Id)).ShouldHaveSingleItem().UserName.ShouldBe("SamJones");
	}

	[Fact]
	public async Task ABlockBack_SurvivesTheOtherSideUnblocking()
	{
		// Two blocks are two facts. Unblocking clears the direction it was made in and no other,
		// or one party could restore their own visibility by blocking and immediately unblocking.
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		RideDetail ride = await CreateRideAsync(organiser);

		using HttpClient rider = await SignedInAsync(app, "SamJones");
		await JoinAsync(rider, ride.JoinCode!);

		await ShareAsync(organiser, ride.Id);
		await ShareAsync(rider, ride.Id);
		await PublishAsync(organiser, -33.86, 151.20);
		await PublishAsync(rider, -33.87, 151.21);

		IReadOnlyList<RiderPositionDto> both = await Snapshot(organiser, ride.Id);
		Guid organiserId = both.Single(position => position.UserName == "DaveSmith").UserId;
		Guid riderId = both.Single(position => position.UserName == "SamJones").UserId;

		await organiser.PostAsJsonAsync(BlocksUrl, new BlockUserRequest(riderId));
		await rider.PostAsJsonAsync(BlocksUrl, new BlockUserRequest(organiserId));

		await organiser.DeleteAsync($"{BlocksUrl}/{riderId}");

		(await Snapshot(organiser, ride.Id)).ShouldHaveSingleItem().UserName.ShouldBe("DaveSmith",
			"the other side's block still stands, and it is not the unblocker's to lift.");
	}

	[Fact]
	public async Task ABlockedTraveller_IsLeftOutOfTheBroadcastBatch_NotJustOutOfTheSnapshot()
	{
		// The live channel, which is the one a rider actually watches. Filtering only the snapshot
		// would hide a pin for exactly as long as it took the next batch to put it back - and the
		// blocked party's own batch has to stay whole, or the block would cost them the map.
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(TokenResponse organiserSession, HttpClient organiser) = await SessionAsync(app, "DaveSmith");
		(TokenResponse riderSession, HttpClient rider) = await SessionAsync(app, "SamJones");

		using (organiser)
		using (rider)
		{
			RideDetail ride = await CreateRideAsync(organiser);
			await JoinAsync(rider, ride.JoinCode!);

			await ShareAsync(organiser, ride.Id);
			await ShareAsync(rider, ride.Id);

			await using HubConnection organiserHub = await HubClient.ConnectAsync(app, organiserSession);
			await using HubConnection riderHub = await HubClient.ConnectAsync(app, riderSession);

			await organiserHub.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);
			await riderHub.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);

			await PublishAsync(organiser, -33.86, 151.20);
			await PublishAsync(rider, -33.87, 151.21);

			IReadOnlyList<RiderPositionDto> both = await Snapshot(organiser, ride.Id);
			Guid organiserId = both.Single(position => position.UserName == "DaveSmith").UserId;
			Guid riderId = both.Single(position => position.UserName == "SamJones").UserId;

			await organiser.PostAsJsonAsync(BlocksUrl, new BlockUserRequest(riderId));

			Task<PositionBatch> seenByOrganiser = HubClient.NextBatchAsync(organiserHub, ride.Id);
			Task<PositionBatch> seenByRider = HubClient.NextBatchAsync(riderHub, ride.Id);

			await app.Services.GetRequiredService<RideBroadcastService>().BroadcastAsync(CancellationToken.None);

			PositionBatch blockerSees = await seenByOrganiser.WaitAsync(TimeSpan.FromSeconds(10));

			blockerSees.Positions.ShouldHaveSingleItem().UserId.ShouldBe(organiserId,
				"a position the reader may not see has no business being in the fan-out.");

			PositionBatch blockedSees = await seenByRider.WaitAsync(TimeSpan.FromSeconds(10));

			blockedSees.Positions.ShouldHaveSingleItem().UserId.ShouldBe(riderId,
				"the block is symmetric, so the other side loses the blocker's pin and keeps the rest.");
		}
	}

	[Fact]
	public async Task BothSides_AreToldToDropTheOtherPin_AndNobodyElseIsTold()
	{
		// The gap this closes: a batch lists the riders the server has a fix for and never says
		// "and this one is gone", and the client upserts from it. So the party who did *not* press
		// block would have gone on drawing the other's last pin for the rest of the adventure -
		// frozen, which is the one thing a block must not leave behind.
		//
		// And a third member must hear nothing at all: a block is between two people (§16.5).
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(TokenResponse organiserSession, HttpClient organiser) = await SessionAsync(app, "DaveSmith");
		(TokenResponse riderSession, HttpClient rider) = await SessionAsync(app, "SamJones");
		(TokenResponse bystanderSession, HttpClient bystander) = await SessionAsync(app, "PatBrown");

		using (organiser)
		using (rider)
		using (bystander)
		{
			RideDetail ride = await CreateRideAsync(organiser);
			await JoinAsync(rider, ride.JoinCode!);
			await JoinAsync(bystander, ride.JoinCode!);

			await ShareAsync(rider, ride.Id);
			await PublishAsync(rider, -33.87, 151.21);

			Guid organiserId = (await organiser.GetFromJsonAsync<RideDetail>($"{RidesUrl}/{ride.Id}"))!
				.Members.Single(member => member.UserName == "DaveSmith").UserId;
			Guid riderId = (await Snapshot(organiser, ride.Id)).Single().UserId;

			await using HubConnection organiserHub = await HubClient.ConnectAsync(app, organiserSession);
			await using HubConnection riderHub = await HubClient.ConnectAsync(app, riderSession);
			await using HubConnection bystanderHub = await HubClient.ConnectAsync(app, bystanderSession);

			await organiserHub.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);
			await riderHub.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);
			await bystanderHub.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);

			Task<(Guid Member, bool Blocked)> seenByOrganiser = HubClient.NextBlockAsync(organiserHub, ride.Id);
			Task<(Guid Member, bool Blocked)> seenByRider = HubClient.NextBlockAsync(riderHub, ride.Id);
			Task<(Guid Member, bool Blocked)> seenByBystander = HubClient.NextBlockAsync(bystanderHub, ride.Id);

			await organiser.PostAsJsonAsync(BlocksUrl, new BlockUserRequest(riderId));

			(Guid Member, bool Blocked) blocker = await seenByOrganiser.WaitAsync(TimeSpan.FromSeconds(10));
			(Guid Member, bool Blocked) blocked = await seenByRider.WaitAsync(TimeSpan.FromSeconds(10));

			blocker.Member.ShouldBe(riderId);
			blocker.Blocked.ShouldBeTrue();

			blocked.Member.ShouldBe(organiserId,
				"each side is told about the other, so neither message says who started it.");
			blocked.Blocked.ShouldBeTrue();

			// Give a leak time to arrive before concluding it did not.
			await Task.Delay(TimeSpan.FromMilliseconds(500));

			seenByBystander.IsCompleted.ShouldBeFalse(
				"the other members of the adventure have no business being told two of them fell out.");
		}
	}

	private static async Task<(TokenResponse Session, HttpClient Client)> SessionAsync(
		DlrWebApplicationFactory app,
		string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return (session, app.CreateClient().Authenticated(session));
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
