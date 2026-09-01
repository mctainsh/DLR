using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
using DLR.Server.Data.Rides;
using DLR.Server.Hubs;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Hubs;

/// <summary>
/// Announcing a join request over the hub (§5.2, §5.3).
/// <para>
/// The waiting count is drawn on two screens - the info page's "Accept join requests" button and
/// the live map's hamburger - and until these messages existed it could only move on a reload. An
/// organiser riding a route does not reload.
/// </para>
/// <para>
/// What this file is really about is the <em>audience</em>. A pending request carries the asker's
/// handle and whatever they wrote to get in, and they are somebody the organiser has not yet
/// agreed to have on the ride; the ride's own group is fifty people it is not about. So the
/// delivery is to <see cref="RideHub.DecidersGroup"/> - exactly the set
/// <c>RideController.CanDecideAsync</c> would let call the list endpoint - and the tests that
/// matter most here are the two negative ones.
/// </para>
/// </summary>
public sealed class JoinRequestBroadcastTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";

	/// <summary>How long to wait for a message that should arrive.</summary>
	private static readonly TimeSpan Arrives = TimeSpan.FromSeconds(10);

	/// <summary>
	/// How long to wait before concluding a message did not arrive. Shorter than
	/// <see cref="Arrives"/> on purpose: it is spent in full by every test that uses it.
	/// </summary>
	private static readonly TimeSpan StaysAway = TimeSpan.FromSeconds(2);

	[Fact]
	public async Task Organiser_IsToldLive_WhenSomebodyAsksToJoin()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(TokenResponse organiser, HttpClient organiserClient) = await SignedInAsync(app, "DaveSmith");
		(_, HttpClient askerClient) = await SignedInAsync(app, "SamJones");

		using (organiserClient)
		using (askerClient)
		{
			RideDetail ride = await ApprovalRideAsync(app, organiserClient);

			await using HubConnection organiserHub = await HubClient.ConnectAsync(app, organiser);
			await organiserHub.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);

			Task<(Guid RideId, JoinRequestSummary Request)> heard = NextRequestAsync(organiserHub);

			await AskAsync(askerClient, ride.JoinCode!, "Riding up from the coast, can I tag along?");

			(Guid rideId, JoinRequestSummary request) = await heard.WaitAsync(Arrives);

			rideId.ShouldBe(ride.Id, "a client holds one session per adventure and has to know which");

			// The whole row, not a nudge to go and fetch it: the requests screen is one tap away and
			// the badge is drawn from a count the client already has.
			request.UserName.ShouldBe("SamJones");
			request.Message.ShouldBe("Riding up from the coast, can I tag along?");
		}
	}

	/// <summary>
	/// The negative half, and the reason the message does not go to <see cref="RideHub.Group"/>.
	/// </summary>
	[Fact]
	public async Task AnOrdinaryMember_IsNotToldWhoIsWaitingToGetIn()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(_, HttpClient organiserClient) = await SignedInAsync(app, "DaveSmith");
		(TokenResponse rider, HttpClient riderClient) = await SignedInAsync(app, "JanePark");
		(_, HttpClient askerClient) = await SignedInAsync(app, "SamJones");

		using (organiserClient)
		using (riderClient)
		using (askerClient)
		{
			RideDetail ride = await OpenRideAsync(app, organiserClient);

			await JoinAsync(riderClient, ride.JoinCode!);
			await RequireApprovalAsync(app, ride.Id);

			// A full member, on the ride's group, receiving positions - and still not entitled to
			// read a request the organiser has not answered.
			await using HubConnection riderHub = await HubClient.ConnectAsync(app, rider);
			await riderHub.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);

			Task<(Guid RideId, JoinRequestSummary Request)> leaked = NextRequestAsync(riderHub);

			await AskAsync(askerClient, ride.JoinCode!, "Let me in");

			Task finished = await Task.WhenAny(leaked, Task.Delay(StaysAway));

			finished.ShouldNotBe(
				leaked,
				"a pending request names somebody the organiser may be about to decline - the ride's "
				+ "other members are not an audience for it");
		}
	}

	/// <summary>
	/// A leader may decide (<c>CanDecideAsync</c>), so a leader gets the badge. The two have to
	/// agree: a screen that can act on a request it is never told about is a screen nobody uses.
	/// </summary>
	[Fact]
	public async Task ALeader_IsToldTheSameAsTheOrganiser()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(_, HttpClient organiserClient) = await SignedInAsync(app, "DaveSmith");
		(TokenResponse leader, HttpClient leaderClient) = await SignedInAsync(app, "JanePark");
		(_, HttpClient askerClient) = await SignedInAsync(app, "SamJones");

		using (organiserClient)
		using (leaderClient)
		using (askerClient)
		{
			RideDetail ride = await OpenRideAsync(app, organiserClient);

			await JoinAsync(leaderClient, ride.JoinCode!);
			await PromoteAsync(app, ride.Id, leader.User.Id, GroupRideRole.Leader);
			await RequireApprovalAsync(app, ride.Id);

			await using HubConnection leaderHub = await HubClient.ConnectAsync(app, leader);
			await leaderHub.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);

			Task<(Guid RideId, JoinRequestSummary Request)> heard = NextRequestAsync(leaderHub);

			await AskAsync(askerClient, ride.JoinCode!, null);

			(_, JoinRequestSummary request) = await heard.WaitAsync(Arrives);

			request.UserName.ShouldBe("SamJones");
		}
	}

	/// <summary>
	/// The other half of keeping the count honest: an answered request has to come off it on every
	/// device that was showing it, not only the one that answered.
	/// </summary>
	[Fact]
	public async Task Deciders_AreToldWhenARequestIsAnswered()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(TokenResponse organiser, HttpClient organiserClient) = await SignedInAsync(app, "DaveSmith");
		(_, HttpClient askerClient) = await SignedInAsync(app, "SamJones");

		using (organiserClient)
		using (askerClient)
		{
			RideDetail ride = await ApprovalRideAsync(app, organiserClient);

			JoinResult asked = await AskAsync(askerClient, ride.JoinCode!, null);

			asked.RequestId.ShouldNotBeNull("the fixture is only meaningful while somebody is waiting");

			// A second device of the organiser's - the map left open on a handlebar mount while the
			// decision is made on a phone. This is the connection the message exists for.
			await using HubConnection organiserHub = await HubClient.ConnectAsync(app, organiser);
			await organiserHub.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);

			Task<(Guid RideId, JoinResult Result)> heard = NextDecisionAsync(organiserHub);

			using HttpResponseMessage decided = await organiserClient.PostAsJsonAsync(
				$"{RidesUrl}/{ride.Id}/join-requests/{asked.RequestId}",
				new DecideJoinRequest(Admit: true, Block: false));

			decided.StatusCode.ShouldBe(
				HttpStatusCode.NoContent,
				await decided.Content.ReadAsStringAsync());

			(Guid rideId, JoinResult result) = await heard.WaitAsync(Arrives);

			rideId.ShouldBe(ride.Id);
			result.Joined.ShouldBeTrue();
			result.RequestId.ShouldBe(asked.RequestId);
		}
	}

	/// <summary>
	/// The third way a request stops being pending, and the badge has to follow it too: the asker
	/// changed their mind. Its own message rather than a decision with a false in it - nobody decided
	/// anything - but it moves the count the same way.
	/// </summary>
	[Fact]
	public async Task Deciders_AreToldWhenTheAskerTakesItBack()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(TokenResponse organiser, HttpClient organiserClient) = await SignedInAsync(app, "DaveSmith");
		(_, HttpClient askerClient) = await SignedInAsync(app, "SamJones");

		using (organiserClient)
		using (askerClient)
		{
			RideDetail ride = await ApprovalRideAsync(app, organiserClient);

			JoinResult asked = await AskAsync(askerClient, ride.JoinCode!, null);

			asked.RequestId.ShouldNotBeNull();

			await using HubConnection organiserHub = await HubClient.ConnectAsync(app, organiser);
			await organiserHub.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);

			Task<(Guid RideId, Guid RequestId)> heard = NextWithdrawalAsync(organiserHub);

			using HttpResponseMessage withdrawn = await askerClient.DeleteAsync(
				$"{RidesUrl}/{ride.Id}/join-requests/{asked.RequestId}");

			withdrawn.StatusCode.ShouldBe(
				HttpStatusCode.NoContent,
				await withdrawn.Content.ReadAsStringAsync());

			(Guid rideId, Guid requestId) = await heard.WaitAsync(Arrives);

			rideId.ShouldBe(ride.Id);
			requestId.ShouldBe(asked.RequestId!.Value);
		}
	}

	/// <summary>Waits for the next withdrawal announced on a connection.</summary>
	private static Task<(Guid RideId, Guid RequestId)> NextWithdrawalAsync(HubConnection connection)
	{
		TaskCompletionSource<(Guid, Guid)> received =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		connection.On<Guid, Guid>(
			nameof(IRideClient.JoinRequestWithdrawn),
			(rideId, requestId) => received.TrySetResult((rideId, requestId)));

		return received.Task;
	}

	/// <summary>Waits for the next join request announced on a connection.</summary>
	private static Task<(Guid RideId, JoinRequestSummary Request)> NextRequestAsync(
		HubConnection connection)
	{
		TaskCompletionSource<(Guid, JoinRequestSummary)> received =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		connection.On<Guid, JoinRequestSummary>(
			nameof(IRideClient.JoinRequestReceived),
			(rideId, request) => received.TrySetResult((rideId, request)));

		return received.Task;
	}

	/// <summary>Waits for the next decision announced on a connection.</summary>
	private static Task<(Guid RideId, JoinResult Result)> NextDecisionAsync(HubConnection connection)
	{
		TaskCompletionSource<(Guid, JoinResult)> received =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		connection.On<Guid, JoinResult>(
			nameof(IRideClient.JoinRequestDecided),
			(rideId, result) => received.TrySetResult((rideId, result)));

		return received.Task;
	}

	private static async Task<JoinResult> AskAsync(HttpClient client, string code, string? message)
	{
		using HttpResponseMessage response =
			await client.PostAsJsonAsync($"{RidesUrl}/join", new JoinByCodeRequest(code, message));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		JoinResult result = (await response.Content.ReadFromJsonAsync<JoinResult>())!;

		result.Joined.ShouldBeFalse("an approval ride creates a request rather than a membership");

		return result;
	}

	private static async Task JoinAsync(HttpClient client, string code)
	{
		using HttpResponseMessage response =
			await client.PostAsJsonAsync($"{RidesUrl}/join", new JoinByCodeRequest(code));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
	}

	/// <summary>An Open, Live ride - one anybody with the code walks straight into.</summary>
	private static async Task<RideDetail> OpenRideAsync(
		DlrWebApplicationFactory app,
		HttpClient organiser)
	{
		using HttpResponseMessage response = await organiser.PostAsJsonAsync(
			RidesUrl,
			new CreateRideRequest(
				"Saturday hills",
				DlrWebApplicationFactory.DefaultStart.AddDays(3),
				JoinPolicy: JoinPolicyDto.Open));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		RideDetail ride = (await response.Content.ReadFromJsonAsync<RideDetail>())!;

		return ride;
	}

	/// <summary>The same ride, already on approval - nobody gets in without being admitted.</summary>
	private static async Task<RideDetail> ApprovalRideAsync(
		DlrWebApplicationFactory app,
		HttpClient organiser)
	{
		RideDetail ride = await OpenRideAsync(app, organiser);

		await RequireApprovalAsync(app, ride.Id);

		return ride;
	}

	/// <summary>
	/// Flips a ride onto approval after the fact, which is what lets a test seat real members with
	/// the join code and only then start creating requests. There is no endpoint for it; the policy
	/// is set once at creation (§5.2).
	/// </summary>
	private static Task RequireApprovalAsync(DlrWebApplicationFactory app, Guid rideId) =>
		app.WithDatabaseAsync(async database =>
		{
			await database.Set<GroupRide>()
				.Where(row => row.Id == rideId)
				.ExecuteUpdateAsync(row => row.SetProperty(x => x.JoinPolicy, JoinPolicy.Approval));
		});

	/// <summary>Gives a member a role. Delegating leadership has no endpoint yet either.</summary>
	private static Task PromoteAsync(
		DlrWebApplicationFactory app,
		Guid rideId,
		Guid userId,
		GroupRideRole role) =>
		app.WithDatabaseAsync(async database =>
		{
			await database.Set<GroupRideMember>()
				.Where(row => row.GroupRideId == rideId && row.UserId == userId)
				.ExecuteUpdateAsync(row => row.SetProperty(x => x.Role, role));
		});

	private static async Task<(TokenResponse Session, HttpClient Client)> SignedInAsync(
		DlrWebApplicationFactory app,
		string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return (session, app.CreateClient().Authenticated(session));
	}
}
