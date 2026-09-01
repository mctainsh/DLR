using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Tracks;
using DLR.Server.Data.Tracks;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using DLR.TestSupport.Tracks;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Tracks;

/// <summary>
/// <c>POST /tracks/import</c> (§15.3, §6.3). The reader and the hostile corpus are SRV-14's;
/// this is the HTTP layer over them, and the part a stranger's file actually reaches.
/// </summary>
public sealed class TrackImportTests(PostgresFixture postgres)
{
	private const string ImportUrl = "/api/v1/tracks/import";

	/// <summary>
	/// Preview is <c>?dryRun=true</c> against this same endpoint, not server-side staging
	/// (§15.3). Holding a parsed result between two calls would need its own storage, its own
	/// expiry sweep and its own orphan cleanup - a whole mechanism to save re-uploading a file
	/// capped at 25 MB.
	/// </summary>
	[Fact]
	public async Task Import_DryRun_PersistsNothing()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackImportResult preview = await ImportAsync(
			client,
			GpxFixtures.SingleTrack(points: 30, name: "Sunday loop"),
			dryRun: true);

		preview.DryRun.ShouldBeTrue();

		ImportedTrackResult track = preview.Tracks.ShouldHaveSingleItem();

		// Everything that would be created, reported without creating it.
		track.Name.ShouldBe("Sunday loop");
		track.PointCount.ShouldBe(30);
		track.DistanceM.ShouldBeGreaterThan(0);
		track.DurationS.ShouldNotBeNull();

		track.TrackId.ShouldBeNull("a preview has nothing to give an id to");

		(await app.WithDatabaseAsync(database => database.Set<Track>().CountAsync())).ShouldBe(0);

		Directory
			.EnumerateFiles(app.BlobRoot, "*", SearchOption.AllDirectories)
			.ShouldBeEmpty("a dry run must not leave a blob for the orphan sweep to find");

		// And the client re-posts to commit, which is the whole flow.
		TrackImportResult committed = await ImportAsync(
			client,
			GpxFixtures.SingleTrack(points: 30, name: "Sunday loop"));

		committed.DryRun.ShouldBeFalse();
		committed.Tracks.ShouldHaveSingleItem().TrackId.ShouldNotBeNull();

		(await app.WithDatabaseAsync(database => database.Set<Track>().CountAsync())).ShouldBe(1);
	}

	/// <summary>
	/// Duplicate <em>detection</em>, not prevention (§15.3). Re-importing on purpose is
	/// legitimate - a second copy to edit differently - and doing it by accident is the common
	/// case. A warning serves both; a refusal serves neither.
	/// </summary>
	[Fact]
	public async Task Import_SameContentTwice_WarnsButProceeds()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		string gpx = GpxFixtures.SingleTrack(points: 25);

		TrackImportResult first = await ImportAsync(client, gpx);

		first.Tracks.ShouldHaveSingleItem().DuplicateOfTrackId.ShouldBeNull();

		Guid originalId = first.Tracks[0].TrackId!.Value;

		// A month later, which is the case the warning is written for - and long past the
		// fifteen-minute access token, so the rider signs in again exactly as they would have.
		app.Clock.Advance(TimeSpan.FromDays(30));

		using HttpClient later = await SignedInAgainAsync(app, "DaveSmith");

		TrackImportResult second = await ImportAsync(later, gpx);

		ImportedTrackResult repeated = second.Tracks.ShouldHaveSingleItem();

		repeated.DuplicateOfTrackId.ShouldBe(originalId, "the warning names the earlier copy");

		repeated.DuplicateImportedUtc.ShouldBe(
			DlrWebApplicationFactory.DefaultStart,
			"'you imported this on 3 June' needs the date, not just the fact");

		repeated.TrackId.ShouldNotBeNull();
		repeated.TrackId.ShouldNotBe(originalId, "it proceeded - there are two tracks now");

		(await app.WithDatabaseAsync(database => database.Set<Track>().CountAsync())).ShouldBe(2);
	}

	/// <summary>
	/// Two riders with the same ride is not a duplicate, it is a group. The lookup is scoped to
	/// the owner for that reason.
	/// </summary>
	[Fact]
	public async Task Import_SameContentAsAnotherAccount_IsNotADuplicate()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient dave = await SignedInAsync(app, "DaveSmith");
		using HttpClient sam = await SignedInAsync(app, "SamJones");

		string gpx = GpxFixtures.SingleTrack(points: 25);

		await ImportAsync(dave, gpx);

		TrackImportResult sams = await ImportAsync(sam, gpx);

		sams.Tracks.ShouldHaveSingleItem().DuplicateOfTrackId.ShouldBeNull();
	}

	[Fact]
	public async Task Import_ExceedsSizeCap_Returns413()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: new Dictionary<string, string?> { ["Tracks:MaxUploadBytes"] = "2048" });

		using HttpClient client = await SignedInAsync(app);

		using HttpResponseMessage response = await PostAsync(
			client,
			GpxFixtures.SingleTrack(points: 500),
			dryRun: false);

		response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);

		(await app.WithDatabaseAsync(database => database.Set<Track>().CountAsync())).ShouldBe(0);
	}

	/// <summary>
	/// A file can be small and still be pathological, so the point cap is separate from the
	/// byte cap and is enforced mid-parse (§15.3).
	/// </summary>
	[Fact]
	public async Task Import_ExceedsPointCap_Returns413AndNamesTheCap()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: new Dictionary<string, string?> { ["Tracks:MaxPointsPerFile"] = "50" });

		using HttpClient client = await SignedInAsync(app);

		using HttpResponseMessage response = await PostAsync(
			client,
			GpxFixtures.SingleTrack(points: 500),
			dryRun: false);

		response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);

		(await response.Content.ReadAsStringAsync()).ShouldContain("50");
	}

	/// <summary>
	/// Read here and persisted in SRV-26. Counting them is what lets the preview say how many
	/// markers a file would create (§15.3, §16.6).
	/// </summary>
	[Fact]
	public async Task Import_WaypointsPresent_AreCountedForTheMarkersTheyWillBecome()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		string gpx = GpxFixtures.SingleTrack(points: 10) + string.Empty;

		TrackImportResult result = await ImportAsync(client, GpxFixtures.WithWaypoints(3), dryRun: true);

		result.WaypointCount.ShouldBe(
			3,
			"the preview reports how many markers will be created (§15.3); creating them is SRV-26");
	}

	/// <summary>
	/// "Invalid file" is useless to somebody whose exporter emits something slightly unusual,
	/// and this is a feature people meet with files from a dozen different tools (§15.3).
	/// </summary>
	[Theory]
	[InlineData("NotXml", "Not an XML file")]
	[InlineData("NotGpx", "Not a GPX file")]
	[InlineData("Truncated", "File is incomplete")]
	[InlineData("WithDtd", "Document type declarations are not accepted")]
	public async Task Import_UnusableFile_ReturnsProblemDetailsNamingTheProblem(
		string fixture,
		string expectedTitle)
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		string gpx = fixture switch
		{
			"NotXml" => GpxFixtures.NotXml(),
			"NotGpx" => GpxFixtures.NotGpx(),
			"Truncated" => GpxFixtures.Truncated(),
			_ => GpxFixtures.WithDtd(),
		};

		using HttpResponseMessage response = await PostAsync(client, gpx, dryRun: true);

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();

		problem.GetProperty("title").GetString().ShouldBe(expectedTitle);

		problem.GetProperty("detail").GetString()
			.ShouldNotBeNullOrWhiteSpace("the detail is what somebody actually reads");
	}

	/// <summary>
	/// Planning tools emit <c>&lt;rte&gt;</c>, and rejecting them would fail the most common
	/// import there is. It arrives as a track with no timestamps (§15.3).
	/// </summary>
	[Fact]
	public async Task Import_Route_ArrivesAsATrackWithoutTimes()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackImportResult result = await ImportAsync(client, GpxFixtures.Route(points: 8));

		ImportedTrackResult route = result.Tracks.ShouldHaveSingleItem();

		route.From.ShouldBe(ImportedFrom.Route);
		route.DurationS.ShouldBeNull();
		route.DistanceM.ShouldBeGreaterThan(0);

		Track stored = await app.WithDatabaseAsync(async database =>
			await database.Set<Track>().SingleAsync());

		stored.Source.ShouldBe(TrackSource.Imported);
		stored.StartedUtc.ShouldBeNull();
		stored.ImportedFileName.ShouldBe("ride.gpx");
	}

	[Fact]
	public async Task Import_ManyTracks_CreatesOnePerTrkAndReportsTruncation()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: new Dictionary<string, string?> { ["Tracks:MaxTracksPerFile"] = "3" });

		using HttpClient client = await SignedInAsync(app);

		TrackImportResult result = await ImportAsync(client, GpxFixtures.ManyTracks(6));

		result.Tracks.Count.ShouldBe(3);
		result.TracksTruncated.ShouldBeTrue();

		(await app.WithDatabaseAsync(database => database.Set<Track>().CountAsync())).ShouldBe(3);
	}

	[Fact]
	public async Task Import_RateLimit_IsPerAccountAndReturns429()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: new Dictionary<string, string?>
			{
				["RateLimits:ImportPerHourPerUser"] = "2",
				["RateLimits:ImportPerDayPerUser"] = "100",
			});

		using HttpClient client = await SignedInAsync(app, "DaveSmith");

		for (int attempt = 1; attempt <= 2; attempt++)
		{
			using HttpResponseMessage allowed =
				await PostAsync(client, GpxFixtures.SingleTrack(points: 5), dryRun: true);

			allowed.StatusCode.ShouldBe(HttpStatusCode.OK, $"import {attempt} is inside the limit");
		}

		using HttpResponseMessage limited =
			await PostAsync(client, GpxFixtures.SingleTrack(points: 5), dryRun: true);

		limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

		// Per account, not per address: another rider on the same connection is unaffected.
		using HttpClient other = await SignedInAsync(app, "SamJones");

		using HttpResponseMessage unaffected =
			await PostAsync(other, GpxFixtures.SingleTrack(points: 5), dryRun: true);

		unaffected.StatusCode.ShouldBe(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Import_WithoutAToken_IsRejected()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response =
			await PostAsync(client, GpxFixtures.SingleTrack(points: 5), dryRun: true);

		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	private static async Task<TrackImportResult> ImportAsync(
		HttpClient client,
		string gpx,
		bool dryRun = false)
	{
		using HttpResponseMessage response = await PostAsync(client, gpx, dryRun);

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<TrackImportResult>())!;
	}

	private static async Task<HttpResponseMessage> PostAsync(
		HttpClient client,
		string gpx,
		bool dryRun)
	{
		using MultipartFormDataContent form = [];

		ByteArrayContent file = new(Encoding.UTF8.GetBytes(gpx));

		file.Headers.ContentType = new MediaTypeHeaderValue("application/gpx+xml");

		form.Add(file, "file", "ride.gpx");

		return await client.PostAsync($"{ImportUrl}?dryRun={dryRun}", form);
	}

	private static async Task<HttpClient> SignedInAsync(
		DlrWebApplicationFactory app,
		string userName = "DaveSmith")
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}

	/// <summary>A fresh token for an account that already exists - the password grant (§7.4).</summary>
	private static async Task<HttpClient> SignedInAgainAsync(
		DlrWebApplicationFactory app,
		string userName)
	{
		using HttpClient anonymous = app.CreateClient();

		using HttpResponseMessage response = await anonymous.PostAsJsonAsync(
			"/api/v1/auth/token",
			new TokenRequest(GrantTypes.Password, userName, TestRegistration.ValidPassword));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return app.CreateClient().Authenticated(
			(await response.Content.ReadFromJsonAsync<TokenResponse>())!);
	}
}
