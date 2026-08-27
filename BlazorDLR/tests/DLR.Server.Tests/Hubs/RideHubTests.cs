using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
using DLR.Server.Data.Rides;
using DLR.Server.Hubs;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.Server.Tests.Hubs;

/// <summary>
/// The live connection (§5.3, §7.6).
/// <para>
/// The membership check in <c>JoinRide</c> is the only thing standing between an authenticated
/// account and a stranger's live location, since the confirmed-email gate went in v0.5. Most of
/// this file is about that one check.
/// </para>
/// </summary>
public sealed class RideHubTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";

	/// <summary>Authentication is not authorisation (§7.6).</summary>
	[Fact]
	public async Task Hub_JoinRide_NonMemberIsRejected()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(TokenResponse organiser, HttpClient organiserClient) = await SignedInAsync(app, "DaveSmith");
		(TokenResponse outsider, _) = await SignedInAsync(app, "NosyNed");

		using (organiserClient)
		{
			RideDetail ride = await LiveRideAsync(app, organiserClient);

			await using HubConnection connection = await HubClient.ConnectAsync(app, outsider);

			// The token was accepted — the connection is open. That is exactly the point: being
			// signed in is not being in the ride.
			connection.State.ShouldBe(HubConnectionState.Connected);

			HubException refused = await Should.ThrowAsync<HubException>(
				() => connection.InvokeAsync(nameof(RideHub.JoinRide), ride.Id));

			refused.Message.ShouldContain(
				"No such adventure",
				Case.Insensitive,
				"a distinguishable refusal turns this into an oracle for who is in which adventure");

			_ = organiser;
		}
	}

	/// <summary>
	/// A pending requester is not a member (§5.2). Checking the request table instead of the
	/// member table would admit precisely the people the organiser has not decided about.
	/// </summary>
	[Fact]
	public async Task Hub_JoinRide_PendingRequesterIsRejected()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(_, HttpClient organiserClient) = await SignedInAsync(app, "DaveSmith");
		(TokenResponse waiting, HttpClient waitingClient) = await SignedInAsync(app, "SamJones");

		using (organiserClient)
		using (waitingClient)
		{
			RideDetail ride = await LiveRideAsync(app, organiserClient, JoinPolicyDto.Approval);

			using HttpResponseMessage asked = await waitingClient.PostAsJsonAsync(
				$"{RidesUrl}/join",
				new JoinByCodeRequest(ride.JoinCode!));

			JoinResult result = (await asked.Content.ReadFromJsonAsync<JoinResult>())!;

			result.Joined.ShouldBeFalse("the fixture is only meaningful while they are still waiting");
			result.RequestId.ShouldNotBeNull();

			await using HubConnection connection = await HubClient.ConnectAsync(app, waiting);

			await Should.ThrowAsync<HubException>(
				() => connection.InvokeAsync(nameof(RideHub.JoinRide), ride.Id));
		}
	}

	[Fact]
	public async Task Hub_ConnectionWithoutToken_IsRejected()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		await using HubConnection connection = HubClient.Build(app, token: null);

		await Should.ThrowAsync<Exception>(() => connection.StartAsync());

		connection.State.ShouldBe(HubConnectionState.Disconnected);
	}

	/// <summary>
	/// §7.6's second rule. A 15-minute access token must not kill a two-hour ride's connection
	/// every quarter of an hour — <c>CloseOnAuthenticationExpiration</c> stays at its default.
	/// </summary>
	[Fact]
	public async Task Hub_LongLivedConnection_SurvivesAccessTokenExpiry()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(TokenResponse organiser, HttpClient organiserClient) = await SignedInAsync(app, "DaveSmith");

		using (organiserClient)
		{
			RideDetail ride = await LiveRideAsync(app, organiserClient);

			await ShareAsync(organiserClient, ride.Id);

			await using HubConnection connection = await HubClient.ConnectAsync(app, organiser);

			await connection.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);

			// Well past the access token's fifteen minutes, and past a couple of refresh
			// rotations too. The rider is still on the bike.
			app.Clock.Advance(TimeSpan.FromHours(2));

			connection.State.ShouldBe(HubConnectionState.Connected);

			// Still functional, not merely still open — the distinction matters, because a
			// connection that has silently lost its principal would still report Connected.
			Task<PositionBatch> batch = HubClient.NextBatchAsync(connection, ride.Id);

			await connection.InvokeAsync(
				nameof(RideHub.PublishPosition),
				new PositionUpdate(
					PositionScale.FromDegrees(-33.86),
					PositionScale.FromDegrees(151.20),
					app.Clock.GetUtcNow()));

			await BroadcastAsync(app);

			PositionBatch received = await batch.WaitAsync(TimeSpan.FromSeconds(10));

			received.Positions.ShouldHaveSingleItem().UserId.ShouldBe(organiser.User.Id);
		}
	}

	/// <summary>
	/// The other half of §7.6's first rule, and the half a test could easily skip: the lift is
	/// scoped to the hub path. Accepting query-string tokens everywhere would scatter credentials
	/// through access logs, referrer headers and browser history.
	/// </summary>
	[Fact]
	public async Task QueryStringToken_IsAcceptedOnTheHubPathOnly()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(TokenResponse session, HttpClient client) = await SignedInAsync(app, "DaveSmith");

		using (client)
		{
			using HttpClient anonymous = app.CreateClient();

			// Works on the hub: the connection opens with no Authorization header anywhere.
			await using HubConnection connection =
				await HubClient.ConnectAsync(app, session);

			connection.State.ShouldBe(HubConnectionState.Connected);

			// And is ignored on an ordinary API route.
			using HttpResponseMessage viaQuery = await anonymous.GetAsync(
				$"/api/v1/me/profile?access_token={session.AccessToken}");

			viaQuery.StatusCode.ShouldBe(
				HttpStatusCode.Unauthorized,
				"a token in a query string is a token in the access log");
		}
	}

	/// <summary>The fan-out itself: one batch to the ride's group (§5.3).</summary>
	[Fact]
	public async Task Broadcast_SendsOneBatchPerRideToItsMembers()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(TokenResponse organiser, HttpClient organiserClient) = await SignedInAsync(app, "DaveSmith");
		(TokenResponse rider, HttpClient riderClient) = await SignedInAsync(app, "SamJones");

		using (organiserClient)
		using (riderClient)
		{
			RideDetail ride = await LiveRideAsync(app, organiserClient);

			await JoinAsync(riderClient, ride.JoinCode!);

			await ShareAsync(organiserClient, ride.Id);
			await ShareAsync(riderClient, ride.Id);

			await using HubConnection organiserHub = await HubClient.ConnectAsync(app, organiser);
			await using HubConnection riderHub = await HubClient.ConnectAsync(app, rider);

			await organiserHub.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);
			await riderHub.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);

			Task<PositionBatch> seenByOrganiser = HubClient.NextBatchAsync(organiserHub, ride.Id);
			Task<PositionBatch> seenByRider = HubClient.NextBatchAsync(riderHub, ride.Id);

			await PublishAsync(organiserHub, app, -33.86, 151.20);
			await PublishAsync(riderHub, app, -33.87, 151.21);

			await BroadcastAsync(app);

			PositionBatch batch = await seenByOrganiser.WaitAsync(TimeSpan.FromSeconds(10));

			batch.Positions.Count.ShouldBe(2, "one batch carries everybody, not one message each");

			// Both members get it — the group is the unit of delivery.
			(await seenByRider.WaitAsync(TimeSpan.FromSeconds(10))).Positions.Count.ShouldBe(2);
		}
	}

	/// <summary>
	/// A ride a connection never joined must not reach it, even while the same account is
	/// legitimately connected to another ride.
	/// </summary>
	[Fact]
	public async Task Broadcast_ReachesOnlyTheRidesAConnectionJoined()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(TokenResponse organiser, HttpClient organiserClient) = await SignedInAsync(app, "DaveSmith");
		(TokenResponse other, HttpClient otherClient) = await SignedInAsync(app, "SamJones");

		using (organiserClient)
		using (otherClient)
		{
			RideDetail mine = await LiveRideAsync(app, otherClient);
			RideDetail theirs = await LiveRideAsync(app, organiserClient);

			await ShareAsync(organiserClient, theirs.Id);

			await using HubConnection watcher = await HubClient.ConnectAsync(app, other);

			await watcher.InvokeAsync(nameof(RideHub.JoinRide), mine.Id);

			Task<PositionBatch> leaked = HubClient.NextBatchAsync(watcher, theirs.Id);

			await using HubConnection publisher = await HubClient.ConnectAsync(app, organiser);

			await publisher.InvokeAsync(nameof(RideHub.JoinRide), theirs.Id);

			await PublishAsync(publisher, app, -33.86, 151.20);

			await BroadcastAsync(app);

			// Give a leak time to arrive before concluding it did not.
			Task finished = await Task.WhenAny(leaked, Task.Delay(TimeSpan.FromSeconds(2)));

			finished.ShouldNotBe(leaked, "a connection receives only the adventures it joined");
		}
	}

	private static Task BroadcastAsync(DlrWebApplicationFactory app) =>
		app.Services.GetRequiredService<RideBroadcastService>().BroadcastAsync(CancellationToken.None);

	private static Task PublishAsync(
		HubConnection connection,
		DlrWebApplicationFactory app,
		double lat,
		double lon) =>
		connection.InvokeAsync(
			nameof(RideHub.PublishPosition),
			new PositionUpdate(
				PositionScale.FromDegrees(lat),
				PositionScale.FromDegrees(lon),
				app.Clock.GetUtcNow()));

	private static async Task ShareAsync(HttpClient client, Guid rideId)
	{
		using HttpResponseMessage response = await client.PutAsJsonAsync(
			$"{RidesUrl}/{rideId}/sharing/me",
			new SetSharingRequest(true));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
	}

	private static async Task JoinAsync(HttpClient client, string code)
	{
		using HttpResponseMessage response =
			await client.PostAsJsonAsync($"{RidesUrl}/join", new JoinByCodeRequest(code));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
	}

	private static async Task<RideDetail> LiveRideAsync(
		DlrWebApplicationFactory app,
		HttpClient organiser,
		JoinPolicyDto policy = JoinPolicyDto.Open)
	{
		using HttpResponseMessage response = await organiser.PostAsJsonAsync(
			RidesUrl,
			new CreateRideRequest(
				"Saturday hills",
				DlrWebApplicationFactory.DefaultStart.AddDays(3),
				JoinPolicy: policy));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		RideDetail ride = (await response.Content.ReadFromJsonAsync<RideDetail>())!;

		await app.WithDatabaseAsync(async database =>
			await database.Set<GroupRide>()
				.Where(row => row.Id == ride.Id)
				.ExecuteUpdateAsync(row => row.SetProperty(x => x.State, GroupRideState.Live)));

		return ride;
	}

	private static async Task<(TokenResponse Session, HttpClient Client)> SignedInAsync(
		DlrWebApplicationFactory app,
		string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return (session, app.CreateClient().Authenticated(session));
	}
}
