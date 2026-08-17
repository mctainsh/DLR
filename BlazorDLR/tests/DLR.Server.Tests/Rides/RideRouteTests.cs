using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.Server.Data.Rides;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using DLR.TestSupport.Tracks;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Rides;

/// <summary>
/// A ride's planned routes (§5.4).
/// <para>
/// The rule that shapes every test here is that a ride carries a <em>set</em>. The outline had
/// one nullable route per ride, and a real day out is commonly two or three — so attaching is
/// additive, the list is ordered by when each was attached, and the oldest one is the line
/// §5.4's gap list projects riders against.
/// </para>
/// <para>
/// The second rule is who may do what: <strong>reading is membership, writing is the
/// organiser.</strong> Everybody in the ride needs the lines to draw the map, and a member who
/// could attach one could put any of their own tracks on somebody else's ride.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class RideRouteTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";
	private const string TracksUrl = "/api/v1/tracks";

	[Fact]
	public async Task Routes_SeveralAttached_AreListedOldestFirstWithTheirLines()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient member = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, member, ride.Id);

		TrackSummary first = await UploadAsync(organiser, "The long way", points: 40);
		TrackSummary second = await UploadAsync(organiser, "The short way", points: 20);

		await AttachAsync(organiser, ride.Id, first.Id);
		await AttachAsync(organiser, ride.Id, second.Id);

		// Read as an ordinary member, which is the point: GET /tracks/{id} is owner-scoped and
		// answers 404 to everybody but the organiser (§15.4), so this endpoint is the only way
		// somebody else in the ride ever sees the line.
		RideRoute[] routes = await ListAsync(member, ride.Id);

		routes.Length.ShouldBe(2, "an adventure carries a set of routes, not one");

		routes[0].TrackId.ShouldBe(first.Id, "oldest attachment first — §5.4's gap list is projected against it");
		routes[1].TrackId.ShouldBe(second.Id);

		routes[0].Name.ShouldBe("The long way");
		routes[0].AddedByUserName.ShouldBe("DaveSmith");
		routes[0].DistanceM.ShouldBeGreaterThan(0);

		// The line travels encoded (§15.5) and decodes through the same codec the server used —
		// a precision mismatch here is what once drew a Sydney ride off the Gulf of Guinea.
		IReadOnlyList<(double Latitude, double Longitude)> line =
			PolylineCodec.DecodePoints(routes[0].EncodedPolyline);

		line.Count.ShouldBeGreaterThan(1, "a route the map can draw is more than one point");
		line[0].Latitude.ShouldBe(GpxFixtures.BaseLatitude, tolerance: 1e-5);

		routes[0].Bounds.ShouldNotBeNull("the map frames itself on the box");
	}

	/// <summary>
	/// Idempotent rather than a 409: the second tap of a button whose first tap was never
	/// acknowledged is the ordinary way this happens, and it has already got what it asked for.
	/// </summary>
	[Fact]
	public async Task Routes_AttachedTwice_IsNotDuplicated()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		TrackSummary track = await UploadAsync(organiser, "Saturday loop", points: 30);

		await AttachAsync(organiser, ride.Id, track.Id);

		using HttpResponseMessage again = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/routes",
			new AddRideRouteRequest(track.Id));

		again.StatusCode.ShouldBe(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());

		(await ListAsync(organiser, ride.Id)).Length.ShouldBe(1);
	}

	[Fact]
	public async Task Routes_Removed_LeaveTheRideAndNotTheLibrary()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		TrackSummary track = await UploadAsync(organiser, "Saturday loop", points: 30);

		await AttachAsync(organiser, ride.Id, track.Id);

		using (HttpResponseMessage removed = await organiser.DeleteAsync(
			$"{RidesUrl}/{ride.Id}/routes/{track.Id}"))
		{
			removed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await removed.Content.ReadAsStringAsync());
		}

		(await ListAsync(organiser, ride.Id)).ShouldBeEmpty();

		// Detaching a route from a ride is not an instruction to destroy the owner's copy of it —
		// same rule as §5.8's switches, where revoking a permission deletes nothing.
		using HttpResponseMessage stillThere = await organiser.GetAsync($"{TracksUrl}/{track.Id}");

		stillThere.StatusCode.ShouldBe(HttpStatusCode.OK, "the track itself is untouched");
	}

	[Fact]
	public async Task Routes_AttachedByAnOrdinaryMember_Returns403()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient member = await SignedInAsync(app, "SamJones");
		using HttpClient stranger = await SignedInAsync(app, "PatKim");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, member, ride.Id);

		TrackSummary theirs = await UploadAsync(member, "My own adventure", points: 25);

		using (HttpResponseMessage refused = await member.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/routes",
			new AddRideRouteRequest(theirs.Id)))
		{
			refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden,
				"§5.4: the organiser decides which routes an adventure has");
		}

		// And somebody not in the ride gets a 404, not a 403 — a ride id is shareable, so a
		// distinguishable refusal would make this an oracle for who is in which ride (§5.2).
		using HttpResponseMessage unknown = await stranger.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/routes",
			new AddRideRouteRequest(theirs.Id));

		unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);

		using HttpResponseMessage cannotRead = await stranger.GetAsync($"{RidesUrl}/{ride.Id}/routes");

		cannotRead.StatusCode.ShouldBe(HttpStatusCode.NotFound);

		(await ListAsync(organiser, ride.Id)).ShouldBeEmpty("neither refusal attached anything");
	}

	/// <summary>
	/// §15.4 draws this line for editing — "not the group-ride organiser, even for a route they
	/// were handed" — and attaching is the same question asked the other way round. A rider hands
	/// a route over by exporting the GPX; that round trip is the copy feature.
	/// </summary>
	[Fact]
	public async Task Routes_SomebodyElsesTrack_Returns404()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient member = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, member, ride.Id);

		TrackSummary theirs = await UploadAsync(member, "Sam's adventure", points: 25);

		using HttpResponseMessage refused = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/routes",
			new AddRideRouteRequest(theirs.Id));

		refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);

		(await ListAsync(organiser, ride.Id)).ShouldBeEmpty();
	}

	/// <summary>
	/// The §15.4 precondition, checkable now that a track can be a ride's route. Editing the
	/// geometry of a line a ride is running on silently moves every rider's place in §5.4's gap
	/// list — nobody rode anywhere, and the list reorders.
	/// </summary>
	[Fact]
	public async Task Routes_OfALiveRide_CannotHaveTheirTrackEdited()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		TrackSummary track = await UploadAsync(organiser, "Saturday loop", points: 60);

		await AttachAsync(organiser, ride.Id, track.Id);

		// Open, so the same edit is still allowed: the rule is about a ride in progress.
		using (HttpResponseMessage allowed = await organiser.PostAsJsonAsync(
			$"{TracksUrl}/{track.Id}/edit",
			new EditTrackRequest(track.Version, [new IndexRange(0, 5)])))
		{
			allowed.StatusCode.ShouldBe(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
		}

		using (HttpResponseMessage started = await organiser.PostAsync($"{RidesUrl}/{ride.Id}/start", content: null))
		{
			started.StatusCode.ShouldBe(HttpStatusCode.NoContent, await started.Content.ReadAsStringAsync());
		}

		using HttpResponseMessage refused = await organiser.PostAsJsonAsync(
			$"{TracksUrl}/{track.Id}/edit",
			new EditTrackRequest(track.Version + 1, [new IndexRange(0, 5)]));

		refused.StatusCode.ShouldBe(HttpStatusCode.Conflict,
			"§15.4: an adventure in progress is travelling this line");

		// Undo moves the line just as surely, so it meets the same precondition.
		using HttpResponseMessage undoRefused = await organiser.PostAsync(
			$"{TracksUrl}/{track.Id}/edit/undo", content: null);

		undoRefused.StatusCode.ShouldBe(HttpStatusCode.Conflict);

		// Attaching another option mid-ride is fine, and moves nobody: the gap list is projected
		// against the oldest attachment, which has not changed.
		TrackSummary detour = await UploadAsync(organiser, "The detour", points: 20);

		await AttachAsync(organiser, ride.Id, detour.Id);

		RideRoute[] routes = await ListAsync(organiser, ride.Id);

		routes.Length.ShouldBe(2);
		routes[0].TrackId.ShouldBe(track.Id, "the line the gap list uses did not move");
	}

	[Fact]
	public async Task Routes_OfAFinishedRide_CannotBeChanged()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		TrackSummary track = await UploadAsync(organiser, "Saturday loop", points: 30);
		TrackSummary another = await UploadAsync(organiser, "Another one", points: 30);

		await AttachAsync(organiser, ride.Id, track.Id);

		using (HttpResponseMessage ended = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/ending",
			new EndRideRequest(RideEndingDto.Immediate)))
		{
			ended.StatusCode.ShouldBe(HttpStatusCode.NoContent, await ended.Content.ReadAsStringAsync());
		}

		using (HttpResponseMessage refused = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/routes",
			new AddRideRouteRequest(another.Id)))
		{
			refused.StatusCode.ShouldBe(HttpStatusCode.Conflict,
				"the routes of a finished adventure are part of the record of it");
		}

		using HttpResponseMessage cannotRemove = await organiser.DeleteAsync(
			$"{RidesUrl}/{ride.Id}/routes/{track.Id}");

		cannotRemove.StatusCode.ShouldBe(HttpStatusCode.Conflict);

		// And it is still readable, which is the whole point of keeping it.
		(await ListAsync(organiser, ride.Id)).Length.ShouldBe(1);
	}

	/// <summary>
	/// Every member downloads every route's line on every load of the ride, so an unbounded set is
	/// a payload one organiser grows on everyone else's connection.
	/// </summary>
	[Fact]
	public async Task Routes_PastTheCap_Returns409()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: new Dictionary<string, string?> { ["Ride:MaxRoutesPerRide"] = "2" });

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		await AttachAsync(organiser, ride.Id, (await UploadAsync(organiser, "One", points: 20)).Id);
		await AttachAsync(organiser, ride.Id, (await UploadAsync(organiser, "Two", points: 20)).Id);

		TrackSummary third = await UploadAsync(organiser, "Three", points: 20);

		using HttpResponseMessage refused = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/routes",
			new AddRideRouteRequest(third.Id));

		refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);

		(await ListAsync(organiser, ride.Id)).Length.ShouldBe(2);
	}

	[Fact]
	public async Task Routes_TrackDeleted_TakeTheAttachmentWithThem()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		TrackSummary track = await UploadAsync(organiser, "Saturday loop", points: 30);

		await AttachAsync(organiser, ride.Id, track.Id);

		// Straight to the table: there is no delete-track endpoint yet, and this test is about the
		// cascade rather than about how a track comes to be gone. A ride pointing at a track that
		// no longer exists would be a route the map cannot draw and the organiser cannot remove.
		await app.WithDatabaseAsync(async database =>
		{
			await database
				.Set<Data.Tracks.Track>()
				.Where(row => row.Id == track.Id)
				.ExecuteDeleteAsync();
		});

		(await ListAsync(organiser, ride.Id)).ShouldBeEmpty();

		int orphans = await app.WithDatabaseAsync(database =>
			database.Set<GroupRideRoute>().CountAsync(route => route.TrackId == track.Id));

		orphans.ShouldBe(0);
	}

	private static async Task<RideRoute[]> ListAsync(HttpClient client, Guid rideId)
	{
		using HttpResponseMessage response = await client.GetAsync($"{RidesUrl}/{rideId}/routes");

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<RideRoute[]>())!;
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

	private static async Task<HttpClient> SignedInAsync(DlrWebApplicationFactory app, string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}
}
