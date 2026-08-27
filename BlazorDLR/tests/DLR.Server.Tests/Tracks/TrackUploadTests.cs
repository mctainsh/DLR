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
/// Uploading, listing and reading a track (§6.2, §6.3, §9.1).
/// </summary>
public sealed class TrackUploadTests(PostgresFixture postgres)
{
	private const string TracksUrl = "/api/v1/tracks";

	/// <summary>
	/// A phone drains its outbox over a flaky connection and re-sends what it never saw
	/// acknowledged (§4.4). Without idempotency a ride recorded in a tunnel appears three
	/// times, and the rider deletes two of them by hand.
	/// </summary>
	[Fact]
	public async Task Upload_SameClientGuidTwice_IsIdempotent()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		Guid clientGuid = Guid.NewGuid();

		using HttpResponseMessage first = await client.PostAsJsonAsync(TracksUrl, Upload(clientGuid));

		first.StatusCode.ShouldBe(HttpStatusCode.Created);

		TrackSummary created = (await first.Content.ReadFromJsonAsync<TrackSummary>())!;

		using HttpResponseMessage second = await client.PostAsJsonAsync(TracksUrl, Upload(clientGuid));

		second.StatusCode.ShouldBe(HttpStatusCode.OK, "the second upload is not a new track");

		TrackSummary repeated = (await second.Content.ReadFromJsonAsync<TrackSummary>())!;

		repeated.Id.ShouldBe(created.Id);

		int stored = await app.WithDatabaseAsync(database => database.Set<Track>().CountAsync());

		stored.ShouldBe(1);
	}

	/// <summary>
	/// The same identifier from a different account is a different track. Scoping the unique
	/// index to the owner is what stops one rider's client identifier colliding with another's
	/// — which, since the client picks it, would otherwise be a way to make somebody else's
	/// upload disappear.
	/// </summary>
	[Fact]
	public async Task Upload_SameClientGuidFromAnotherAccount_IsItsOwnTrack()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient dave = await SignedInAsync(app, "DaveSmith");
		using HttpClient sam = await SignedInAsync(app, "SamJones");

		Guid clientGuid = Guid.NewGuid();

		using HttpResponseMessage first = await dave.PostAsJsonAsync(TracksUrl, Upload(clientGuid));
		using HttpResponseMessage second = await sam.PostAsJsonAsync(TracksUrl, Upload(clientGuid));

		first.StatusCode.ShouldBe(HttpStatusCode.Created);
		second.StatusCode.ShouldBe(HttpStatusCode.Created);

		int stored = await app.WithDatabaseAsync(database => database.Set<Track>().CountAsync());

		stored.ShouldBe(2);
	}

	[Fact]
	public async Task Upload_StoresBlobAndComputesContentHash()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackGeometry geometry = Geometry();

		using HttpResponseMessage response =
			await client.PostAsJsonAsync(TracksUrl, Upload(Guid.NewGuid(), geometry));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		Track stored = await app.WithDatabaseAsync(async database =>
			await database.Set<Track>().SingleAsync());

		stored.BlobRef.ShouldNotBeNullOrWhiteSpace();
		stored.ContentHash.Length.ShouldBe(32, "SHA-256 is 32 bytes");

		stored.ContentHash.ShouldBe(
			TrackBlobCodec.ContentHash(geometry),
			"the hash identifies the points, so the same adventure hashes the same way wherever it " +
			"was computed (§15.3)");

		// The blob is a real file on the volume, and it round-trips losslessly — which is what
		// makes Edit_NoOpEdit_ProducesIdenticalStats true after a save as well as before one.
		string path = Directory
			.EnumerateFiles(app.BlobRoot, "*", SearchOption.AllDirectories)
			.ShouldHaveSingleItem();

		await using FileStream blob = File.OpenRead(path);

		TrackGeometry readBack = TrackBlobCodec.Read(blob);

		readBack.Points.ShouldBe(geometry.Points);
		TrackStats.From(readBack).ShouldBe(TrackStats.From(geometry));
	}

	/// <summary>
	/// An imported route has no start time at all, and a ride imported today belongs at the top
	/// of the list whenever it was actually ridden (§6.2).
	/// </summary>
	[Fact]
	public async Task TrackList_SortsOnCreatedUtc_NotStartedUtc()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		// Upload order is deliberately the *opposite* of ride order. Sorting on started_utc
		// would otherwise give the same answer here as sorting on created_utc, and the test
		// would pass without distinguishing them — PostgreSQL also puts NULLs first on a
		// descending sort, so even the untimed route would land where it belongs by accident.
		TrackGeometry recent = Geometry(startingAt: DlrWebApplicationFactory.DefaultStart);

		await client.PostAsJsonAsync(TracksUrl, Upload(Guid.NewGuid(), recent, name: "Ridden today"));

		app.Clock.Advance(TimeSpan.FromMinutes(1));

		TrackGeometry old = Geometry(startingAt: DlrWebApplicationFactory.DefaultStart.AddYears(-1));

		await client.PostAsJsonAsync(TracksUrl, Upload(Guid.NewGuid(), old, name: "Ridden last year"));

		app.Clock.Advance(TimeSpan.FromMinutes(1));

		TrackGeometry route = Geometry(timed: false);

		await client.PostAsJsonAsync(TracksUrl, Upload(Guid.NewGuid(), route, name: "Planned route"));

		List<TrackSummary> tracks = (await client.GetFromJsonAsync<List<TrackSummary>>(TracksUrl))!;

		tracks.Select(track => track.Name).ShouldBe(
			["Planned route", "Ridden last year", "Ridden today"],
			"newest upload first. Sorting on started_utc would put last year's adventure below " +
			"today's, and a route imported a moment ago has no start time to sort by at all");

		tracks[0].StartedUtc.ShouldBeNull();
		tracks[0].DurationS.ShouldBeNull("a route has a shape but was never ridden (§15.1)");
	}

	[Fact]
	public async Task TrackList_ShowsOnlyTheCallersTracks()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient dave = await SignedInAsync(app, "DaveSmith");
		using HttpClient sam = await SignedInAsync(app, "SamJones");

		await dave.PostAsJsonAsync(TracksUrl, Upload(Guid.NewGuid(), name: "Dave's adventure"));

		(await sam.GetFromJsonAsync<List<TrackSummary>>(TracksUrl))!.ShouldBeEmpty();
	}

	[Fact]
	public async Task TrackDetail_ReturnsTheSimplifiedLineAndBounds()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackGeometry geometry = Geometry(points: 400);

		using HttpResponseMessage upload =
			await client.PostAsJsonAsync(TracksUrl, Upload(Guid.NewGuid(), geometry));

		TrackSummary created = (await upload.Content.ReadFromJsonAsync<TrackSummary>())!;

		TrackDetail detail =
			(await client.GetFromJsonAsync<TrackDetail>($"{TracksUrl}/{created.Id}"))!;

		detail.Track.Id.ShouldBe(created.Id);
		detail.Track.PointCount.ShouldBe(400, "the count is the real one, not the drawn one");

		detail.Polyline.Count.ShouldBeLessThan(
			400,
			"the map draws a reduced line; the editor never addresses these indices (§15.5)");

		detail.Polyline.Count.ShouldBeGreaterThanOrEqualTo(2);

		detail.Bounds.ShouldNotBeNull();
		detail.Bounds!.Value.MinLatitude.ShouldBeLessThan(detail.Bounds.Value.MaxLatitude);
	}

	[Fact]
	public async Task TrackDetail_AnotherAccountsTrack_IsNotFound()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient dave = await SignedInAsync(app, "DaveSmith");
		using HttpClient sam = await SignedInAsync(app, "SamJones");

		using HttpResponseMessage upload = await dave.PostAsJsonAsync(TracksUrl, Upload(Guid.NewGuid()));

		TrackSummary created = (await upload.Content.ReadFromJsonAsync<TrackSummary>())!;

		using HttpResponseMessage response = await sam.GetAsync($"{TracksUrl}/{created.Id}");

		response.StatusCode.ShouldBe(
			HttpStatusCode.NotFound,
			"a distinguishable answer would be a way to ask whether a track id exists");
	}

	[Fact]
	public async Task Upload_WithoutAToken_IsRejected()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response = await client.PostAsJsonAsync(TracksUrl, Upload(Guid.NewGuid()));

		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	/// <summary>
	/// The app parses GPX with the same reader the server does (§15.7), and the server
	/// re-validates anyway: a client-supplied point list is untrusted input regardless of which
	/// of our own clients produced it (§15.2).
	/// </summary>
	[Fact]
	public async Task Upload_ImpossibleCoordinates_IsRejected()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		UploadTrackRequest request = new(
			Guid.NewGuid(),
			[new TrackPoint(0, 0), new TrackPoint(91.5, 0), new TrackPoint(1, 1)]);

		using HttpResponseMessage response = await client.PostAsJsonAsync(TracksUrl, request);

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		(await app.WithDatabaseAsync(database => database.Set<Track>().CountAsync())).ShouldBe(0);

		Directory
			.EnumerateFiles(app.BlobRoot, "*", SearchOption.AllDirectories)
			.ShouldBeEmpty("a refused upload must not leave a blob behind");
	}

	[Fact]
	public async Task Upload_FewerThanTwoPoints_IsRejected()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		UploadTrackRequest request = new(Guid.NewGuid(), [new TrackPoint(-27.47, 153.02)]);

		using HttpResponseMessage response = await client.PostAsJsonAsync(TracksUrl, request);

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}

	/// <summary>
	/// Recording is not gated by §7.8's ladder. The restriction targets the social surface,
	/// which is what abuse would be after; a rider held back by it keeps their own rides.
	/// </summary>
	[Fact]
	public async Task Upload_RestrictedAccount_MayStillRecord()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient signup = app.CreateClient().From("203.0.113.210");

		for (int account = 1; account <= 3; account++)
		{
			await signup.RegisterAsync($"Traveller{account}");
		}

		TokenResponse restricted =
			await signup.RegisterAsync("Rider4", email: "rider4@example.com");

		using HttpClient client = app.CreateClient().Authenticated(restricted);

		using HttpResponseMessage response = await client.PostAsJsonAsync(TracksUrl, Upload(Guid.NewGuid()));

		response.StatusCode.ShouldBe(HttpStatusCode.Created);
	}

	private static async Task<HttpClient> SignedInAsync(
		DlrWebApplicationFactory app,
		string userName = "DaveSmith")
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}

	private static UploadTrackRequest Upload(
		Guid clientGuid,
		TrackGeometry? geometry = null,
		string? name = "Morning loop")
	{
		TrackGeometry track = geometry ?? Geometry();

		return new UploadTrackRequest(
			clientGuid,
			track.Points,
			track.SegmentStarts.Skip(1).ToList(),
			name);
	}

	/// <param name="points">How many points.</param>
	/// <param name="startingAt">When the first fix was taken.</param>
	/// <param name="timed">
	/// False for a planned route, which has no timestamps at all. A separate flag rather than a
	/// null <paramref name="startingAt"/>, because <c>?? default</c> would turn an explicit
	/// "none" back into a time and quietly make the untimed case untestable.
	/// </param>
	private static TrackGeometry Geometry(
		int points = 20,
		DateTimeOffset? startingAt = null,
		bool timed = true)
	{
		DateTimeOffset? start = timed ? startingAt ?? GpxFixtures.Start : null;

		return new TrackGeometry(
		[
			.. Enumerable.Range(0, points).Select(index => new TrackPoint(
				GpxFixtures.BaseLatitude + (index * GpxFixtures.MetresToDegreesLatitude(20)),
				GpxFixtures.BaseLongitude,
				50 + index,
				start?.AddSeconds(index * 10))),
		]);
	}
}
