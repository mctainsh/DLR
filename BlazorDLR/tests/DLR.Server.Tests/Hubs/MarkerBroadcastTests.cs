using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Moderation;
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
/// A marker placed by one member reaches the others' maps without a reload (§16.6).
/// <para>
/// <strong>This suite exists because the feature was broken in the one way nothing else would
/// notice.</strong> <c>IRideClient.MarkerAdded</c> took a single <c>MarkerDto</c> while
/// <c>SignalRRideHubClient</c> registered a two-argument handler, and SignalR dispatches on the
/// method name <em>and</em> the argument count - so the broadcast matched nothing, no exception was
/// raised anywhere, and markers simply never appeared on anybody else's map.
/// </para>
/// <para>
/// The endpoint tests could not see it: they assert what the endpoint <em>returns</em>. The bUnit
/// tests could not see it: they raise the client's events by hand and never involve SignalR at all.
/// Only a real connection, receiving a real broadcast, exercises the dispatch that was failing -
/// which is what everything below does. <c>HubContractRules</c> guards the same thing statically.
/// </para>
/// </summary>
public sealed class MarkerBroadcastTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";
	private const string MarkersUrl = "/api/v1/markers";

	/// <summary>How long a broadcast gets to arrive before the test calls it missing.</summary>
	private static readonly TimeSpan Arrives = TimeSpan.FromSeconds(10);

	/// <summary>The case the bug report described: one member adds a pin, another sees it appear.</summary>
	[Fact]
	public async Task AddingAMarker_ReachesTheOtherMembersMap()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(TokenResponse organiserSession, HttpClient organiser) = await SignedInAsync(app, "DaveSmith");
		using HttpClient author = (await SignedInAsync(app, "SamJones")).Client;

		using (organiser)
		{
			RideDetail ride = await CreateRideAsync(organiser);
			await JoinAsync(app, author, ride.Id);

			await using HubConnection listener = await HubClient.ConnectAsync(app, organiserSession);
			await listener.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);

			Task<MarkerDto> heard = HubClient.NextMarkerAsync(listener, ride.Id);

			MarkerDto placed = await CreateMarkerAsync(author, ride.Id);

			// The waiter filters on the ride id, so arriving at all is the assertion that the
			// message carried the right one.
			MarkerDto marker = await heard.WaitAsync(Arrives);

			marker.Id.ShouldBe(placed.Id);
			marker.Title.ShouldBe(placed.Title);
			marker.CreatedByUserName.ShouldBe("SamJones");
		}
	}

	/// <summary>Removing one reaches them the same way, which is what a report leans on (§17.7).</summary>
	[Fact]
	public async Task RemovingAMarker_ReachesTheOtherMembersMap()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(TokenResponse organiserSession, HttpClient organiser) = await SignedInAsync(app, "DaveSmith");

		using (organiser)
		{
			RideDetail ride = await CreateRideAsync(organiser);

			MarkerDto placed = await CreateMarkerAsync(organiser, ride.Id);

			await using HubConnection listener = await HubClient.ConnectAsync(app, organiserSession);
			await listener.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);

			Task<Guid> heard = HubClient.NextMarkerRemovalAsync(listener, ride.Id);

			using HttpResponseMessage deleted = await organiser.DeleteAsync($"{MarkersUrl}/{placed.Id}");

			deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());

			(await heard.WaitAsync(Arrives)).ShouldBe(placed.Id);
		}
	}

	/// <summary>
	/// And reporting one, which is the half of guideline 1.2 that says objectionable content leaves
	/// the feed <em>instantly</em> rather than on the next fetch (§10.2).
	/// </summary>
	[Fact]
	public async Task ReportingAMarker_TakesItOffTheOtherMembersMapAtOnce()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		(TokenResponse organiserSession, HttpClient organiser) = await SignedInAsync(app, "DaveSmith");
		using HttpClient author = (await SignedInAsync(app, "SamJones")).Client;

		using (organiser)
		{
			RideDetail ride = await CreateRideAsync(organiser);
			await JoinAsync(app, author, ride.Id);

			MarkerDto placed = await CreateMarkerAsync(author, ride.Id);

			await using HubConnection listener = await HubClient.ConnectAsync(app, organiserSession);
			await listener.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);

			Task<Guid> heard = HubClient.NextMarkerRemovalAsync(listener, ride.Id);

			using HttpResponseMessage filed = await organiser.PostAsJsonAsync(
				$"{MarkersUrl}/{placed.Id}/report",
				new ReportContentRequest("A photograph that should not be there"));

			filed.StatusCode.ShouldBe(HttpStatusCode.OK, await filed.Content.ReadAsStringAsync());

			(await heard.WaitAsync(Arrives)).ShouldBe(placed.Id);
		}
	}



	private static async Task<MarkerDto> CreateMarkerAsync(HttpClient client, Guid rideId)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			MarkersUrl,
			new CreateMarkerRequest(
				TrackId: null,
				GroupRideId: rideId,
				PositionScale.FromDegrees(-33.86),
				PositionScale.FromDegrees(151.20),
				"hazard",
				"Gravel on the corner"));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<MarkerDto>())!;
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

	private static async Task JoinAsync(DlrWebApplicationFactory app, HttpClient member, Guid rideId)
	{
		string code = await app.WithDatabaseAsync(database =>
			database.Set<GroupRide>()
				.Where(ride => ride.Id == rideId)
				.Select(ride => ride.JoinCode)
				.SingleAsync());

		using HttpResponseMessage response = await member.PostAsJsonAsync(
			$"{RidesUrl}/join",
			new JoinByCodeRequest(code));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
	}

	/// <summary>
	/// A signed-in client and its session. The session is what a hub connection needs and what an
	/// <see cref="HttpClient"/> does not expose.
	/// </summary>
	private static async Task<(TokenResponse Session, HttpClient Client)> SignedInAsync(
		DlrWebApplicationFactory app,
		string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return (session, app.CreateClient().Authenticated(session));
	}
}
