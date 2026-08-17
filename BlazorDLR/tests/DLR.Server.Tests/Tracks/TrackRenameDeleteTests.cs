using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Rides;
using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.Server.Data.Markers;
using DLR.Server.Data.Rides;
using DLR.Server.Data.Tracks;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using DLR.TestSupport.Tracks;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Tracks;

/// <summary>
/// Renaming and deleting a stored track (§15.1, §15.4, §15.5).
/// <para>
/// Both apply to a recorded track and an imported one without distinction — §15.1 makes them one
/// entity, and <c>Source</c> is display and support information, never authorisation. The tests
/// below therefore prove the pair once against each source rather than twice against one.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TrackRenameDeleteTests(PostgresFixture postgres)
{
	private const string TracksUrl = "/api/v1/tracks";
	private const string RidesUrl = "/api/v1/group-rides";

	[Theory]
	[InlineData(TrackSourceDto.Recorded)]
	[InlineData(TrackSourceDto.Imported)]
	public async Task Rename_ChangesTheName_ForEitherSource(TrackSourceDto source)
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client, name: "Course 3", source: source);

		TrackSummary renamed = await RenameAsync(client, track.Id, "  Saturday coast run  ");

		renamed.Name.ShouldBe("Saturday coast run", "the name is trimmed on the way in");
		renamed.Source.ShouldBe(source, "a rename does not turn an imported track into a recorded one");

		// And it is what the list and the detail read afterwards, not just what the write echoed.
		List<TrackSummary> listed = (await client.GetFromJsonAsync<List<TrackSummary>>(TracksUrl))!;

		listed.ShouldHaveSingleItem().Name.ShouldBe("Saturday coast run");
	}

	/// <summary>
	/// §15.5's version guards point indices. A rename moves no point, so bumping the version would
	/// refuse an editor open in another tab over a change that cannot have invalidated it.
	/// </summary>
	[Fact]
	public async Task Rename_DoesNotBumpTheVersion_SoAnOpenEditStillApplies()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client, points: 60);

		track.Version.ShouldBe(1);

		TrackSummary renamed = await RenameAsync(client, track.Id, "The long way");

		renamed.Version.ShouldBe(1);

		// The edit was composed before the rename and is still good.
		using HttpResponseMessage edit = await client.PostAsJsonAsync(
			$"{TracksUrl}/{track.Id}/edit",
			new EditTrackRequest(track.Version, [new IndexRange(0, 5)]));

		edit.StatusCode.ShouldBe(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync());

		TrackEditResponse edited = (await edit.Content.ReadFromJsonAsync<TrackEditResponse>())!;

		edited.Track.Name.ShouldBe("The long way", "the edit rewrote the line, not the name");
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public async Task Rename_WithoutAName_IsRefused(string name)
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client, name: "Morning loop");

		using HttpResponseMessage response = await client.PatchAsJsonAsync(
			$"{TracksUrl}/{track.Id}",
			new RenameTrackRequest(name));

		response.StatusCode.ShouldBe(
			HttpStatusCode.BadRequest,
			"an empty box is refused rather than read as 'clear the name'");

		TrackSummary unchanged = (await client.GetFromJsonAsync<TrackDetail>($"{TracksUrl}/{track.Id}"))!.Track;

		unchanged.Name.ShouldBe("Morning loop");
	}

	[Fact]
	public async Task Rename_LongerThanTheColumn_IsRefusedRatherThanTruncated()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client, name: "Morning loop");

		using HttpResponseMessage response = await client.PatchAsJsonAsync(
			$"{TracksUrl}/{track.Id}",
			new RenameTrackRequest(new string('x', TrackNaming.MaxLength + 1)));

		response.StatusCode.ShouldBe(
			HttpStatusCode.BadRequest,
			"a traveller who typed it should be told, not silently given something shorter");
	}

	/// <summary>
	/// An upload carrying a name too long for the column is a 400, not the 500 a
	/// <c>DbUpdateException</c> would surface as.
	/// </summary>
	[Fact]
	public async Task Upload_WithAnOverlongName_IsRefusedWithoutLeavingABlob()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackGeometry geometry = Geometry(20);

		using HttpResponseMessage response = await client.PostAsJsonAsync(
			TracksUrl,
			new UploadTrackRequest(
				Guid.NewGuid(),
				geometry.Points,
				null,
				new string('x', TrackNaming.MaxLength + 1)));

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		(await app.WithDatabaseAsync(database => database.Set<Track>().CountAsync())).ShouldBe(0);

		Directory
			.EnumerateFiles(app.BlobRoot, "*", SearchOption.AllDirectories)
			.ShouldBeEmpty("a refused upload must not leave a blob behind");
	}

	[Fact]
	public async Task Rename_AnotherAccountsTrack_IsNotFound()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient dave = await SignedInAsync(app, "DaveSmith");
		using HttpClient sam = await SignedInAsync(app, "SamJones");

		TrackSummary track = await UploadAsync(dave, name: "Dave's adventure");

		using HttpResponseMessage response = await sam.PatchAsJsonAsync(
			$"{TracksUrl}/{track.Id}",
			new RenameTrackRequest("Sam's adventure"));

		response.StatusCode.ShouldBe(
			HttpStatusCode.NotFound,
			"the same answer the detail read gives — a distinguishable one would be a way to ask " +
			"whether a track id exists");

		TrackSummary unchanged = (await dave.GetFromJsonAsync<TrackDetail>($"{TracksUrl}/{track.Id}"))!.Track;

		unchanged.Name.ShouldBe("Dave's adventure");
	}

	[Theory]
	[InlineData(TrackSourceDto.Recorded)]
	[InlineData(TrackSourceDto.Imported)]
	public async Task Delete_RemovesTheRowAndItsPoints_ForEitherSource(TrackSourceDto source)
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client, source: source);

		Directory
			.EnumerateFiles(app.BlobRoot, "*", SearchOption.AllDirectories)
			.Count()
			.ShouldBe(1);

		using HttpResponseMessage response = await client.DeleteAsync($"{TracksUrl}/{track.Id}");

		response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());

		(await app.WithDatabaseAsync(database => database.Set<Track>().CountAsync())).ShouldBe(0);

		// The point of doing this in the endpoint rather than leaving it to the nightly sweep: a
		// track somebody deleted is off the disk now, not by tomorrow (§16.6).
		Directory
			.EnumerateFiles(app.BlobRoot, "*", SearchOption.AllDirectories)
			.ShouldBeEmpty("ON DELETE CASCADE does not reach a filesystem");

		(await client.GetAsync($"{TracksUrl}/{track.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	/// <summary>
	/// The retained original is the rider's data while it exists (§15.6) and goes with the track.
	/// Leaving it behind would keep the pre-trim line — the one that still has their address on it
	/// — on the disk after they deleted the ride it belonged to.
	/// </summary>
	[Fact]
	public async Task Delete_TakesTheRetainedOriginalAndItsBlob()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client, points: 60);

		using (HttpResponseMessage edit = await client.PostAsJsonAsync(
			$"{TracksUrl}/{track.Id}/edit",
			new EditTrackRequest(track.Version, [new IndexRange(0, 20)])))
		{
			edit.StatusCode.ShouldBe(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync());
		}

		(await app.WithDatabaseAsync(database => database.Set<TrackRevision>().CountAsync())).ShouldBe(1);

		Directory
			.EnumerateFiles(app.BlobRoot, "*", SearchOption.AllDirectories)
			.Count()
			.ShouldBe(2, "the edited line and the original it displaced");

		using HttpResponseMessage response = await client.DeleteAsync($"{TracksUrl}/{track.Id}");

		response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		(await app.WithDatabaseAsync(database => database.Set<TrackRevision>().CountAsync())).ShouldBe(0);

		Directory
			.EnumerateFiles(app.BlobRoot, "*", SearchOption.AllDirectories)
			.ShouldBeEmpty("both blobs, or the pre-trim line outlives the track it was trimmed from");
	}

	/// <summary>
	/// Markers hang off exactly one parent (§16.1). The track going takes them with it — a marker
	/// whose parent no longer exists is a pin on nothing.
	/// </summary>
	[Fact]
	public async Task Delete_CascadesTheTracksMarkers()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client);
		TrackSummary keep = await UploadAsync(client, name: "Kept", clientGuid: Guid.NewGuid());

		await AddMarkerAsync(client, track.Id, "Gravel");
		await AddMarkerAsync(client, keep.Id, "Fuel");

		(await app.WithDatabaseAsync(database => database.Set<Marker>().CountAsync())).ShouldBe(2);

		using HttpResponseMessage response = await client.DeleteAsync($"{TracksUrl}/{track.Id}");

		response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		List<Marker> remaining = await app.WithDatabaseAsync(database =>
			database.Set<Marker>().AsNoTracking().ToListAsync());

		remaining.ShouldHaveSingleItem().TrackId.ShouldBe(keep.Id, "only the deleted track's markers go");
	}

	[Fact]
	public async Task Delete_AnotherAccountsTrack_IsNotFound_AndLeavesItAlone()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient dave = await SignedInAsync(app, "DaveSmith");
		using HttpClient sam = await SignedInAsync(app, "SamJones");

		TrackSummary track = await UploadAsync(dave, name: "Dave's adventure");

		using HttpResponseMessage response = await sam.DeleteAsync($"{TracksUrl}/{track.Id}");

		response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

		(await app.WithDatabaseAsync(database => database.Set<Track>().CountAsync())).ShouldBe(1);

		Directory
			.EnumerateFiles(app.BlobRoot, "*", SearchOption.AllDirectories)
			.Count()
			.ShouldBe(1, "a refused delete must not take the blob of a track it did not delete");
	}

	[Fact]
	public async Task Delete_Twice_IsNotFoundTheSecondTime()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client);

		(await client.DeleteAsync($"{TracksUrl}/{track.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

		using HttpResponseMessage again = await client.DeleteAsync($"{TracksUrl}/{track.Id}");

		again.StatusCode.ShouldBe(
			HttpStatusCode.NotFound,
			"answering 204 to any id at all would make this a way to probe for tracks");
	}

	/// <summary>
	/// The §15.4 precondition an edit meets, and a delete meets it for a stronger reason: an edit
	/// moves the line a ride in progress is measured against, and a delete takes it away — the
	/// attachment cascades and every rider's place in §5.4's gap list goes with it, mid-ride.
	/// </summary>
	[Fact]
	public async Task Delete_TrackOfALiveRide_IsRefused()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		TrackSummary track = await UploadAsync(organiser, name: "Saturday loop", points: 60);

		using (HttpResponseMessage attached = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/routes",
			new AddRideRouteRequest(track.Id)))
		{
			attached.StatusCode.ShouldBe(HttpStatusCode.Created, await attached.Content.ReadAsStringAsync());
		}

		using (HttpResponseMessage started = await organiser.PostAsync($"{RidesUrl}/{ride.Id}/start", content: null))
		{
			started.StatusCode.ShouldBe(HttpStatusCode.NoContent, await started.Content.ReadAsStringAsync());
		}

		using HttpResponseMessage refused = await organiser.DeleteAsync($"{TracksUrl}/{track.Id}");

		refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, "an adventure in progress is travelling this line");

		(await app.WithDatabaseAsync(database => database.Set<Track>().CountAsync())).ShouldBe(1);

		// Renaming it meanwhile is fine: it moves nobody, and a route the organiser mislabelled is
		// exactly the thing worth fixing while the ride is running.
		TrackSummary renamed = await RenameAsync(organiser, track.Id, "Saturday loop — long option");

		renamed.Name.ShouldBe("Saturday loop — long option");

		// Once the ride has ended the delete goes through, and the attachment goes with it.
		using (HttpResponseMessage ended = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/ending",
			new EndRideRequest(RideEndingDto.Immediate)))
		{
			ended.StatusCode.ShouldBe(HttpStatusCode.NoContent, await ended.Content.ReadAsStringAsync());
		}

		using HttpResponseMessage allowed = await organiser.DeleteAsync($"{TracksUrl}/{track.Id}");

		allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await allowed.Content.ReadAsStringAsync());

		(await app.WithDatabaseAsync(database => database.Set<GroupRideRoute>().CountAsync()))
			.ShouldBe(0, "an adventure pointing at a track that no longer exists is a route nobody can remove");
	}

	[Fact]
	public async Task RenameAndDelete_WithoutAToken_AreRejected()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient owner = await SignedInAsync(app);
		using HttpClient anonymous = app.CreateClient();

		TrackSummary track = await UploadAsync(owner);

		using HttpResponseMessage rename = await anonymous.PatchAsJsonAsync(
			$"{TracksUrl}/{track.Id}",
			new RenameTrackRequest("Mine now"));

		rename.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		using HttpResponseMessage delete = await anonymous.DeleteAsync($"{TracksUrl}/{track.Id}");

		delete.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	private static async Task<TrackSummary> RenameAsync(HttpClient client, Guid trackId, string name)
	{
		using HttpResponseMessage response = await client.PatchAsJsonAsync(
			$"{TracksUrl}/{trackId}",
			new RenameTrackRequest(name));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<TrackSummary>())!;
	}

	private static async Task AddMarkerAsync(HttpClient client, Guid trackId, string title)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			"/api/v1/markers",
			new CreateMarkerRequest(
				trackId,
				GroupRideId: null,
				(int)(GpxFixtures.BaseLatitude * 1e5),
				(int)(GpxFixtures.BaseLongitude * 1e5),
				"hazard",
				title));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
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

	private static async Task<TrackSummary> UploadAsync(
		HttpClient client,
		string? name = "Morning loop",
		int points = 20,
		TrackSourceDto source = TrackSourceDto.Recorded,
		Guid? clientGuid = null)
	{
		TrackGeometry geometry = Geometry(points);

		using HttpResponseMessage response = await client.PostAsJsonAsync(
			TracksUrl,
			new UploadTrackRequest(
				clientGuid ?? Guid.NewGuid(),
				geometry.Points,
				null,
				name,
				source,
				source == TrackSourceDto.Imported ? "ride.gpx" : null));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<TrackSummary>())!;
	}

	private static TrackGeometry Geometry(int points) =>
		new(
		[
			.. Enumerable.Range(0, points).Select(index => new TrackPoint(
				GpxFixtures.BaseLatitude + (index * GpxFixtures.MetresToDegreesLatitude(20)),
				GpxFixtures.BaseLongitude,
				50 + (index % 7),
				GpxFixtures.Start.AddSeconds(index * 10))),
		]);

	private static async Task<HttpClient> SignedInAsync(
		DlrWebApplicationFactory app,
		string userName = "DaveSmith")
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}
}
