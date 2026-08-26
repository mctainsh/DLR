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
/// Announcing a new member to the ride (§5.2, §5.3).
/// <para>
/// Unlike a join <em>request</em>, this one goes to <see cref="RideHub.Group"/> — everybody. A
/// member list is drawn for every rider on the ride, and somebody who is now on it is not a
/// confidence: the alternative is fifty people looking at a list that is a row short until each of
/// them happens to reload.
/// </para>
/// <para>
/// There are two doors into a ride (§5.2) — a code on an open ride, and an organiser admitting a
/// request — and from the ride's point of view they are one event. Both are tested here, along with
/// the decline that must announce nothing.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MemberJoinedBroadcastTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";

	private static readonly TimeSpan Arrives = TimeSpan.FromSeconds(10);

	private static readonly TimeSpan StaysAway = TimeSpan.FromSeconds(2);

	[Fact]
	public async Task TheRideIsTold_WhenSomebodyWalksInWithTheCode()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(TokenResponse organiser, HttpClient organiserClient) = await SignedInAsync(app, "DaveSmith");
		(_, HttpClient joinerClient) = await SignedInAsync(app, "SamJones");

		using (organiserClient)
		using (joinerClient)
		{
			RideDetail ride = await OpenRideAsync(app, organiserClient);

			await using HubConnection organiserHub = await HubClient.ConnectAsync(app, organiser);
			await organiserHub.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);

			Task<(Guid RideId, RideMemberSummary Member)> heard = NextMemberAsync(organiserHub);

			await JoinAsync(joinerClient, ride.JoinCode!);

			(Guid rideId, RideMemberSummary member) = await heard.WaitAsync(Arrives);

			rideId.ShouldBe(ride.Id);
			member.UserName.ShouldBe("SamJones");
			member.Role.ShouldBe(nameof(GroupRideRole.Rider));

			// Joining a ride and agreeing to broadcast are separate decisions, and the second one
			// defaults to off (§5.6). A member list that drew a new arrival as sharing would be
			// claiming a consent nobody gave.
			member.Sharing.ShouldBeFalse();

			// And "not sharing" is not "no signal" — neither is a fix they have not sent yet.
			member.HasPosition.ShouldBeFalse();
		}
	}

	[Fact]
	public async Task TheRideIsTold_WhenTheOrganiserAdmitsARequest()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(TokenResponse organiser, HttpClient organiserClient) = await SignedInAsync(app, "DaveSmith");
		(_, HttpClient askerClient) = await SignedInAsync(app, "SamJones");

		using (organiserClient)
		using (askerClient)
		{
			RideDetail ride = await OpenRideAsync(app, organiserClient);

			await RequireApprovalAsync(app, ride.Id);

			JoinResult asked = await AskAsync(askerClient, ride.JoinCode!);

			await using HubConnection organiserHub = await HubClient.ConnectAsync(app, organiser);
			await organiserHub.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);

			Task<(Guid RideId, RideMemberSummary Member)> heard = NextMemberAsync(organiserHub);

			await DecideAsync(organiserClient, ride.Id, asked.RequestId!.Value, admit: true);

			(_, RideMemberSummary member) = await heard.WaitAsync(Arrives);

			member.UserName.ShouldBe(
				"SamJones",
				"the second door into a ride is still the same event to everybody already on it");
		}
	}

	/// <summary>
	/// The negative case, and the one an implementation drifts into getting wrong: declining
	/// answers a request and changes nobody's membership.
	/// </summary>
	[Fact]
	public async Task ADeclinedRequest_TellsTheRideNothing()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(TokenResponse organiser, HttpClient organiserClient) = await SignedInAsync(app, "DaveSmith");
		(_, HttpClient askerClient) = await SignedInAsync(app, "SamJones");

		using (organiserClient)
		using (askerClient)
		{
			RideDetail ride = await OpenRideAsync(app, organiserClient);

			await RequireApprovalAsync(app, ride.Id);

			JoinResult asked = await AskAsync(askerClient, ride.JoinCode!);

			await using HubConnection organiserHub = await HubClient.ConnectAsync(app, organiser);
			await organiserHub.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);

			Task<(Guid RideId, RideMemberSummary Member)> leaked = NextMemberAsync(organiserHub);

			await DecideAsync(organiserClient, ride.Id, asked.RequestId!.Value, admit: false);

			Task finished = await Task.WhenAny(leaked, Task.Delay(StaysAway));

			finished.ShouldNotBe(leaked, "nobody joined, so there is nothing to put in the member list");
		}
	}

	/// <summary>Waits for the next member announced on a connection.</summary>
	private static Task<(Guid RideId, RideMemberSummary Member)> NextMemberAsync(HubConnection connection)
	{
		TaskCompletionSource<(Guid, RideMemberSummary)> received =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		connection.On<Guid, RideMemberSummary>(
			nameof(IRideClient.MemberJoined),
			(rideId, member) => received.TrySetResult((rideId, member)));

		return received.Task;
	}

	private static async Task DecideAsync(HttpClient organiser, Guid rideId, Guid requestId, bool admit)
	{
		using HttpResponseMessage response = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{rideId}/join-requests/{requestId}",
			new DecideJoinRequest(admit, Block: false));

		response.StatusCode.ShouldBe(
			HttpStatusCode.NoContent,
			await response.Content.ReadAsStringAsync());
	}

	private static async Task<JoinResult> AskAsync(HttpClient client, string code)
	{
		using HttpResponseMessage response =
			await client.PostAsJsonAsync($"{RidesUrl}/join", new JoinByCodeRequest(code));

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

		await app.WithDatabaseAsync(async database =>
		{
			await database.Set<GroupRide>()
				.Where(row => row.Id == ride.Id)
				.ExecuteUpdateAsync(row => row.SetProperty(x => x.State, GroupRideState.Live));
		});

		return ride;
	}

	private static Task RequireApprovalAsync(DlrWebApplicationFactory app, Guid rideId) =>
		app.WithDatabaseAsync(async database =>
		{
			await database.Set<GroupRide>()
				.Where(row => row.Id == rideId)
				.ExecuteUpdateAsync(row => row.SetProperty(x => x.JoinPolicy, JoinPolicy.Approval));
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
