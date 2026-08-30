using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.Server.Data.Rides;
using DLR.Server.Data.Tracks;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using DLR.TestSupport.Tracks;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Rides;

/// <summary>
/// Deleting an adventure (§5.2).
/// <para>
/// <strong>Delete is not End.</strong> §5.6's End stops the location sharing and keeps the day —
/// the thread, the markers, who was there. This takes all of it, for every member and not only
/// for the organiser, which is why the two rules below are the ones worth holding: only the
/// organiser may do it, and not while the adventure is running.
/// </para>
/// <para>
/// The third rule is the one that is easy to get wrong: what a delete <em>reaches</em>. Members,
/// requests, route attachments and positions go with the ride; the organiser's own tracks do not,
/// because attaching a track to an adventure was never a transfer of it (§5.4).
/// </para>
/// </summary>
public sealed class RideDeleteTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";
	private const string TracksUrl = "/api/v1/tracks";

	/// <summary>
	/// The whole delete, in one statement, reaching everything that hangs off the ride — and
	/// stopping exactly at the track, which is the organiser's and was only ever borrowed.
	/// </summary>
	[Fact]
	public async Task Delete_TakesTheRideAndEverythingOnIt_ButNotTheTrack()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient member = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);
		await JoinAsync(app, member, ride.Id);

		TrackSummary track = await UploadAsync(organiser, "The long way", points: 20);
		await AttachAsync(organiser, ride.Id, track.Id);

		using HttpResponseMessage deleted = await organiser.DeleteAsync($"{RidesUrl}/{ride.Id}");

		deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());

		(await app.WithDatabaseAsync(database => database.Set<GroupRide>().CountAsync(row => row.Id == ride.Id)))
			.ShouldBe(0, "the ride itself is gone");

		(await app.WithDatabaseAsync(database => database.Set<GroupRideMember>().CountAsync(row => row.GroupRideId == ride.Id)))
			.ShouldBe(0, "the members cascade — both of them, not only the organiser");

		(await app.WithDatabaseAsync(database => database.Set<GroupRideRoute>().CountAsync(row => row.GroupRideId == ride.Id)))
			.ShouldBe(0, "the route attachments cascade");

		// The line itself stays. Attaching a track to an adventure lends it for the day (§5.4);
		// a delete that took the organiser's own route with it would be a data loss nobody asked
		// for and no screen warned about.
		(await app.WithDatabaseAsync(database => database.Set<Track>().CountAsync(row => row.Id == track.Id)))
			.ShouldBe(1, "the organiser's track is theirs — attaching it was never a transfer");
	}

	/// <summary>
	/// Delete is the only way to finish an adventure, so it has to take the live positions with
	/// it rather than leaving them for the nightly sweep a fortnight later.
	/// </summary>
	[Fact]
	public async Task Delete_WhileSomebodyIsSharing_TakesTheirPositions()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);
		await ShareAsync(organiser, ride.Id, share: true);
		await PublishAsync(organiser, -33.86, 151.20);

		CountPositions(app).ShouldBe(1, "there is something to take");

		using HttpResponseMessage deleted = await organiser.DeleteAsync($"{RidesUrl}/{ride.Id}");

		deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());

		CountPositions(app)
			.ShouldBe(0, "nobody consented to being findable in an adventure that no longer exists");
	}

	/// <summary>
	/// A member is not an organiser, and the answer is 404 rather than 403 — a ride id is
	/// shareable (§5.2), so an answer that confirmed the ride existed would be a probe.
	/// </summary>
	[Fact]
	public async Task Delete_ByAMember_Is404_AndTheRideStays()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient member = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);
		await JoinAsync(app, member, ride.Id);

		using HttpResponseMessage refused = await member.DeleteAsync($"{RidesUrl}/{ride.Id}");

		refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, await refused.Content.ReadAsStringAsync());

		(await app.WithDatabaseAsync(database => database.Set<GroupRide>().CountAsync(row => row.Id == ride.Id)))
			.ShouldBe(1);
	}

	/// <summary>A stranger gets the same answer a member does, which is the whole reason it is 404.</summary>
	[Fact]
	public async Task Delete_ByAStranger_Is404()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient stranger = await SignedInAsync(app, "PatBrown");

		RideDetail ride = await CreateRideAsync(organiser);

		using HttpResponseMessage refused = await stranger.DeleteAsync($"{RidesUrl}/{ride.Id}");

		refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, await refused.Content.ReadAsStringAsync());
	}

	/// <summary>
	/// The second press. It answers 404 rather than 204 for the same reason the stranger does —
	/// a 204 to any id at all would make this endpoint a way to ask whether a ride exists.
	/// </summary>
	[Fact]
	public async Task Delete_Twice_Is404TheSecondTime()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		using HttpResponseMessage first = await organiser.DeleteAsync($"{RidesUrl}/{ride.Id}");
		first.StatusCode.ShouldBe(HttpStatusCode.NoContent, await first.Content.ReadAsStringAsync());

		using HttpResponseMessage second = await organiser.DeleteAsync($"{RidesUrl}/{ride.Id}");
		second.StatusCode.ShouldBe(HttpStatusCode.NotFound, await second.Content.ReadAsStringAsync());
	}

	/// <summary>And it leaves the caller's own list, which is where the screen reads it back from.</summary>
	[Fact]
	public async Task Delete_RemovesItFromMyRides()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail keeping = await CreateRideAsync(organiser, "Sunday flat");
		RideDetail going = await CreateRideAsync(organiser, "Saturday hills");

		using HttpResponseMessage deleted = await organiser.DeleteAsync($"{RidesUrl}/{going.Id}");
		deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());

		MyRides mine = (await organiser.GetFromJsonAsync<MyRides>(RidesUrl))!;

		mine.Organised.Select(row => row.Id).ShouldBe([keeping.Id]);
	}

	// -- Helpers ------------------------------------------------------------------------------

	private static int CountPositions(DlrWebApplicationFactory app) => app.PositionCount();

	private static async Task<RideDetail> CreateRideAsync(HttpClient organiser, string name = "Saturday hills")
	{
		using HttpResponseMessage response = await organiser.PostAsJsonAsync(
			RidesUrl,
			new CreateRideRequest(
				name,
				DlrWebApplicationFactory.DefaultStart.AddDays(3),
				JoinPolicy: JoinPolicyDto.Open));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<RideDetail>())!;
	}

	private static async Task ShareAsync(HttpClient client, Guid rideId, bool share)
	{
		using HttpResponseMessage response = await client.PutAsJsonAsync(
			$"{RidesUrl}/{rideId}/sharing/me",
			new SetSharingRequest(share));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
	}

	private static async Task PublishAsync(HttpClient client, double latitude, double longitude)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			"/api/v1/positions",
			new PositionUpdate(
				PositionScale.FromDegrees(latitude),
				PositionScale.FromDegrees(longitude),
				DlrWebApplicationFactory.DefaultStart.AddDays(1)));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
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

	private static async Task AttachAsync(HttpClient organiser, Guid rideId, Guid trackId)
	{
		using HttpResponseMessage response = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{rideId}/routes",
			new AddRideRouteRequest(trackId));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
	}

	private static async Task<TrackSummary> UploadAsync(HttpClient client, string name, int points)
	{
		TrackGeometry geometry = new(
		[
			.. Enumerable.Range(0, points).Select(index => new TrackPoint(
				GpxFixtures.BaseLatitude + (index * GpxFixtures.MetresToDegreesLatitude(20)),
				GpxFixtures.BaseLongitude,
				50 + (index % 7),
				GpxFixtures.Start.AddSeconds(index * 10))),
		]);

		using HttpResponseMessage response = await client.PostAsJsonAsync(
			TracksUrl,
			new UploadTrackRequest(Guid.NewGuid(), geometry.Points, null, name));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<TrackSummary>())!;
	}

	private static async Task<HttpClient> SignedInAsync(DlrWebApplicationFactory app, string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}
}
