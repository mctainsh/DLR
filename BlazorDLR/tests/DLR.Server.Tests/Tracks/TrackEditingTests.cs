using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.Server.Data.Tracks;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using DLR.TestSupport.Tracks;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Tracks;

/// <summary>
/// Editing, versioning and undo (§15.4, §15.5, §15.6).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TrackEditingTests(PostgresFixture postgres)
{
	private const string TracksUrl = "/api/v1/tracks";

	/// <summary>
	/// Two browser tabs editing one track is the realistic case, and silently applying stale
	/// indices would cut the wrong span (§15.5).
	/// </summary>
	[Fact]
	public async Task Edit_StaleVersion_Returns409()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client, points: 60);

		track.Version.ShouldBe(1);

		await EditAsync(client, track.Id, version: 1, new IndexRange(0, 5));

		// The second tab still thinks it is version 1.
		using HttpResponseMessage stale = await PostEditAsync(
			client,
			track.Id,
			version: 1,
			new IndexRange(50, 55));

		stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);

		(await stale.Content.ReadAsStringAsync()).ShouldContain(
			"version 2",
			Case.Insensitive);

		Track stored = await StoredAsync(app, track.Id);

		stored.Version.ShouldBe(2, "the stale edit changed nothing");
		stored.PointCount.ShouldBe(55);
	}

	/// <summary>
	/// 403 rather than 404, unlike everything else in this API (§15.4). A share link makes a
	/// track's id legitimately known to people who do not own it.
	/// </summary>
	[Fact]
	public async Task Edit_ByNonOwner_Returns403()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient dave = await SignedInAsync(app, "DaveSmith");
		using HttpClient sam = await SignedInAsync(app, "SamJones");

		TrackSummary track = await UploadAsync(dave, points: 40);

		using HttpResponseMessage response =
			await PostEditAsync(sam, track.Id, version: 1, new IndexRange(0, 5));

		response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		(await StoredAsync(app, track.Id)).PointCount.ShouldBe(40);
	}

	/// <summary>
	/// A track has exactly one writer at any moment (§15.4). Editing before the hand-over would
	/// have the server rewriting a display copy while the device still held the real one.
	/// </summary>
	[Fact]
	public async Task Edit_TrackNotFullyUploaded_Returns409()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client, points: 40);

		await app.WithDatabaseAsync(async database =>
		{
			Track row = await database.Set<Track>().SingleAsync(t => t.Id == track.Id);

			row.IsFullyUploaded = false;

			await database.SaveChangesAsync();
		});

		using HttpResponseMessage response =
			await PostEditAsync(client, track.Id, version: 1, new IndexRange(0, 5));

		response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

		(await response.Content.ReadAsStringAsync()).ShouldContain("still being uploaded");
	}

	/// <summary>
	/// §15.5 calls this the single most important implementation constraint in the section.
	/// The map draws a simplified line; an edit addressed against <em>that</em> index space
	/// deletes a different point on the server — plausibly hundreds of metres away, invisibly,
	/// and only on tracks dense enough for simplification to have done anything.
	/// </summary>
	[Fact]
	public async Task Edit_IndicesApplyToRawPoints_NotSimplifiedPolyline()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		// Dense and near-straight, so simplification discards most of it and the two index
		// spaces are wildly different. On a track where they happen to agree this test would
		// pass whichever one the server used.
		TrackSummary track = await UploadAsync(client, points: 300, metresApart: 3);

		TrackDetail detail =
			(await client.GetFromJsonAsync<TrackDetail>($"{TracksUrl}/{track.Id}"))!;

		detail.Polyline.Count.ShouldBeLessThan(
			20,
			"the fixture has to be one where the two index spaces cannot be confused");

		TrackPointsResponse before = await PointsAsync(client, track.Id);

		IReadOnlyList<(double Latitude, double Longitude)> raw =
			PolylineCodec.DecodePoints(before.Polyline);

		// Remove ten raw points from the middle — an index far beyond the simplified line's
		// entire length, so a server working in the wrong space could not even apply it.
		await EditAsync(client, track.Id, version: 1, new IndexRange(100, 110));

		TrackPointsResponse after = await PointsAsync(client, track.Id);

		after.PointCount.ShouldBe(290);

		IReadOnlyList<(double Latitude, double Longitude)> edited =
			PolylineCodec.DecodePoints(after.Polyline);

		// The point that was at raw index 110 is now at 100, and everything before 100 is where
		// it was. That is only true if the server counted in raw indices.
		edited[99].Latitude.ShouldBe(raw[99].Latitude, tolerance: 0.0000005);
		edited[100].Latitude.ShouldBe(raw[110].Latitude, tolerance: 0.0000005);
	}

	/// <summary>
	/// A stale polyline would keep drawing the trimmed span on a map, which for the privacy
	/// case is the entire failure (§15.5).
	/// </summary>
	[Fact]
	public async Task Edit_SimplifiedPolylineAndContentHash_AreRegenerated()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client, points: 200, metresApart: 15);

		Track before = await StoredAsync(app, track.Id);

		byte[] hashBefore = before.ContentHash;
		byte[] polylineBefore = before.SimplifiedPolyline;
		double distanceBefore = before.DistanceM;

		await EditAsync(client, track.Id, version: 1, new IndexRange(0, 100));

		Track after = await StoredAsync(app, track.Id);

		after.ContentHash.ShouldNotBe(hashBefore, "the content changed, so its hash must too");
		after.SimplifiedPolyline.ShouldNotBe(polylineBefore);

		after.DistanceM.ShouldBeLessThan(distanceBefore);
		after.PointCount.ShouldBe(100);
		after.EditedUtc.ShouldNotBeNull();

		// Recomputed, not adjusted (§15.5). The stored numbers are what the surviving points
		// say, not the old numbers with the removed span subtracted.
		using MemoryStream blob = new(after.SimplifiedPolyline);

		TrackGeometry simplified = TrackBlobCodec.Read(blob);

		simplified.Points.Count.ShouldBeLessThanOrEqualTo(100);
	}

	[Theory]
	[InlineData(0, 0, "empty")]
	[InlineData(-1, 5, "below zero")]
	[InlineData(30, 500, "past the end")]
	public async Task Edit_InvalidRange_Returns400NamingIt(int from, int to, string why)
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client, points: 40);

		using HttpResponseMessage response =
			await PostEditAsync(client, track.Id, version: 1, new IndexRange(from, to));

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, why);

		(await response.Content.ReadAsStringAsync()).ShouldContain($"[{from}, {to})");
	}

	[Fact]
	public async Task Edit_LeavingFewerThanTwoPoints_Returns400()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client, points: 40);

		using HttpResponseMessage response =
			await PostEditAsync(client, track.Id, version: 1, new IndexRange(0, 39));

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		(await response.Content.ReadAsStringAsync()).ShouldContain("delete the track");
	}

	/// <summary>
	/// Undo is itself an edit, not a rewind: the restored points become a new version, so the
	/// chain only ever moves forward (§15.6).
	/// </summary>
	[Fact]
	public async Task Undo_WithinWindow_RestoresPreviousPointsAsNewVersion()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client, points: 80);

		TrackPointsResponse original = await PointsAsync(client, track.Id);

		TrackEditResponse edited = await EditAsync(client, track.Id, version: 1, new IndexRange(0, 30));

		edited.Track.PointCount.ShouldBe(50);

		edited.UndoAvailableUntilUtc.ShouldBe(
			DlrWebApplicationFactory.DefaultStart.AddDays(7),
			"seven days is the default window (§15.8)");

		// Six days on: inside the window, and well past the fifteen-minute access token — so
		// the rider signs in again, exactly as they would have.
		app.Clock.Advance(TimeSpan.FromDays(6));

		using HttpClient later = await ReauthenticatedAsync(app, "DaveSmith");

		using HttpResponseMessage response =
			await later.PostAsync($"{TracksUrl}/{track.Id}/edit/undo", null);

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		TrackEditResponse restored = (await response.Content.ReadFromJsonAsync<TrackEditResponse>())!;

		restored.Track.PointCount.ShouldBe(80);

		restored.Track.Version.ShouldBe(
			3,
			"undo moves the chain forward — a device replaces its cached copy on the version " +
			"number and never has to reason about going backwards");

		restored.UndoAvailableUntilUtc.ShouldBeNull("the safety net is spent");

		TrackPointsResponse now = await PointsAsync(later, track.Id);

		now.Polyline.ShouldBe(original.Polyline, "the points came back byte for byte");
	}

	[Fact]
	public async Task Undo_AfterWindow_Returns404()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client, points: 60);

		await EditAsync(client, track.Id, version: 1, new IndexRange(0, 20));

		app.Clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));

		using HttpClient later = await ReauthenticatedAsync(app, "DaveSmith");

		using HttpResponseMessage response =
			await later.PostAsync($"{TracksUrl}/{track.Id}/edit/undo", null);

		response.StatusCode.ShouldBe(
			HttpStatusCode.NotFound,
			"the clock decides, not whether the nightly sweep happened to have run");
	}

	/// <summary>
	/// Exactly one revision per track. Undo is a safety net for the last action, not a history
	/// feature — and unbounded revisions would quietly triple storage on a 40 GB disk (§15.6).
	/// </summary>
	[Fact]
	public async Task Undo_SecondEditWithinWindow_ReplacesRetainedOriginal()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client, points: 100);

		await EditAsync(client, track.Id, version: 1, new IndexRange(0, 20));

		app.Clock.Advance(TimeSpan.FromDays(2));

		using HttpClient twoDaysLater = await ReauthenticatedAsync(app, "DaveSmith");

		await EditAsync(twoDaysLater, track.Id, version: 2, new IndexRange(0, 20));

		int revisions = await app.WithDatabaseAsync(database =>
			database.Set<TrackRevision>().CountAsync());

		revisions.ShouldBe(1, "one row per track, always");

		TrackRevision revision = await app.WithDatabaseAsync(async database =>
			await database.Set<TrackRevision>().SingleAsync());

		revision.Version.ShouldBe(2, "what is retained is the 80-point version, not the 100");

		revision.PurgeAfterUtc.ShouldBe(
			DlrWebApplicationFactory.DefaultStart.AddDays(9),
			"the second edit restarts the clock");

		// Undo goes back one step, not all the way.
		using HttpResponseMessage response =
			await twoDaysLater.PostAsync($"{TracksUrl}/{track.Id}/edit/undo", null);

		TrackEditResponse restored = (await response.Content.ReadFromJsonAsync<TrackEditResponse>())!;

		restored.Track.PointCount.ShouldBe(80);

		// And the blob the first edit displaced is gone, not merely unreferenced.
		int blobs = Directory.EnumerateFiles(app.BlobRoot, "*", SearchOption.AllDirectories).Count();

		blobs.ShouldBe(1, "one live blob; the 100-point original went when it was superseded");
	}

	/// <summary>
	/// For the rider who has just trimmed their home address off a track and does not want to
	/// wait seven days (§15.6).
	/// </summary>
	[Fact]
	public async Task PurgeNow_DeletesRetainedOriginalImmediately()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client, points: 60);

		await EditAsync(client, track.Id, version: 1, new IndexRange(0, 20));

		(await app.WithDatabaseAsync(database => database.Set<TrackRevision>().CountAsync()))
			.ShouldBe(1);

		using HttpResponseMessage purged =
			await client.DeleteAsync($"{TracksUrl}/{track.Id}/previous-version");

		purged.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		(await app.WithDatabaseAsync(database => database.Set<TrackRevision>().CountAsync()))
			.ShouldBe(0);

		Directory
			.EnumerateFiles(app.BlobRoot, "*", SearchOption.AllDirectories)
			.Count()
			.ShouldBe(1, "the trimmed points are off the disk, not merely unreachable");

		// And undo is gone with it, which is the trade the rider made knowingly.
		using HttpResponseMessage undo =
			await client.PostAsync($"{TracksUrl}/{track.Id}/edit/undo", null);

		undo.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task PurgeNow_WithNothingRetained_IsNotAnError()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client, points: 20);

		using HttpResponseMessage response =
			await client.DeleteAsync($"{TracksUrl}/{track.Id}/previous-version");

		response.StatusCode.ShouldBe(
			HttpStatusCode.NoContent,
			"the nightly sweep and an impatient traveller must not race each other into a 404");
	}

	private static async Task<TrackEditResponse> EditAsync(
		HttpClient client,
		Guid trackId,
		int version,
		params IndexRange[] removals)
	{
		using HttpResponseMessage response = await PostEditAsync(client, trackId, version, removals);

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<TrackEditResponse>())!;
	}

	private static Task<HttpResponseMessage> PostEditAsync(
		HttpClient client,
		Guid trackId,
		int version,
		params IndexRange[] removals) =>
		client.PostAsJsonAsync(
			$"{TracksUrl}/{trackId}/edit",
			new EditTrackRequest(version, removals));

	private static async Task<TrackPointsResponse> PointsAsync(HttpClient client, Guid trackId) =>
		(await client.GetFromJsonAsync<TrackPointsResponse>($"{TracksUrl}/{trackId}/points"))!;

	private static async Task<Track> StoredAsync(DlrWebApplicationFactory app, Guid trackId) =>
		await app.WithDatabaseAsync(async database =>
			await database.Set<Track>().AsNoTracking().SingleAsync(track => track.Id == trackId));

	private static async Task<TrackSummary> UploadAsync(
		HttpClient client,
		int points,
		double metresApart = 20)
	{
		TrackGeometry geometry = new(
		[
			.. Enumerable.Range(0, points).Select(index => new TrackPoint(
				GpxFixtures.BaseLatitude + (index * GpxFixtures.MetresToDegreesLatitude(metresApart)),
				GpxFixtures.BaseLongitude,
				50 + (index % 7),
				GpxFixtures.Start.AddSeconds(index * 10))),
		]);

		using HttpResponseMessage response = await client.PostAsJsonAsync(
			TracksUrl,
			new UploadTrackRequest(Guid.NewGuid(), geometry.Points, null, "Morning loop"));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<TrackSummary>())!;
	}

	private static async Task<HttpClient> SignedInAsync(
		DlrWebApplicationFactory app,
		string userName = "DaveSmith")
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}

	private static async Task<HttpClient> ReauthenticatedAsync(
		DlrWebApplicationFactory app,
		string userName)
	{
		using HttpClient anonymous = app.CreateClient();

		return app.CreateClient().Authenticated(await anonymous.SignInAsync(userName));
	}
}
