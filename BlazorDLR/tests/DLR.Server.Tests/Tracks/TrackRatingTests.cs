using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Moderation;
using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using DLR.TestSupport.Tracks;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Tracks;

/// <summary>
/// Stars on a shared route (§6.2).
/// <para>
/// The boundary is the same one the sharing tests spend their attention on, asked about a second
/// verb: a route nobody shared cannot be rated by a stranger, a route somebody blocked cannot be
/// rated at all, and the scale refuses anything outside it. What is new here is the arithmetic -
/// an average is the one thing on this feature that can be quietly, plausibly wrong.
/// </para>
/// </summary>
public sealed class TrackRatingTests(PostgresFixture postgres)
{
	private const string TracksUrl = "/api/v1/tracks";

	[Fact]
	public async Task Rating_AveragesEverybodyAndReportsTheCallersOwn()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient alice = await SignedInAsync(app, "AliceBrown");
		using HttpClient bob = await SignedInAsync(app, "BobJones");

		Guid trackId = await ShareAsync(owner, "Coast run north");

		await RateAsync(alice, trackId, 5);
		TrackRatingSummary afterBob = await RateAsync(bob, trackId, 2);

		afterBob.Count.ShouldBe(2);
		afterBob.Average!.Value.ShouldBe(3.5, 0.0001);
		afterBob.Mine.ShouldBe(2, "the tally reports the caller's own answer, not the last one written");

		// And the same numbers to a third reader, whose own is null because they have not rated it.
		TrackRatingSummary asOwner = await ReadRatingAsync(owner, trackId);

		asOwner.Count.ShouldBe(2);
		asOwner.Average!.Value.ShouldBe(3.5, 0.0001);
		asOwner.Mine.ShouldBeNull();
	}

	/// <summary>
	/// The primary key is the rule (<c>TrackRating</c>), so a second rating from the same rider
	/// replaces rather than accumulates - otherwise anybody could move an average by tapping.
	/// </summary>
	[Fact]
	public async Task RatingTwice_ReplacesRatherThanCountingTwice()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		Guid trackId = await ShareAsync(owner, "Coast run north");

		await RateAsync(reader, trackId, 5);
		TrackRatingSummary again = await RateAsync(reader, trackId, 1);

		again.Count.ShouldBe(1);
		again.Average!.Value.ShouldBe(1, 0.0001);
		again.Mine.ShouldBe(1);
	}

	/// <summary>
	/// Withdrawing is a delete, never a zero - a stored nought would average in as the worst
	/// possible score for every rider who changed their mind.
	/// </summary>
	[Fact]
	public async Task Withdrawing_RemovesTheRatingRatherThanScoringItZero()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient alice = await SignedInAsync(app, "AliceBrown");
		using HttpClient bob = await SignedInAsync(app, "BobJones");

		Guid trackId = await ShareAsync(owner, "Coast run north");

		await RateAsync(alice, trackId, 4);
		await RateAsync(bob, trackId, 2);

		using HttpResponseMessage cleared = await bob.DeleteAsync($"{TracksUrl}/{trackId}/rating");

		cleared.StatusCode.ShouldBe(HttpStatusCode.OK, await cleared.Content.ReadAsStringAsync());

		TrackRatingSummary after = (await cleared.Content.ReadFromJsonAsync<TrackRatingSummary>())!;

		after.Count.ShouldBe(1);
		after.Average!.Value.ShouldBe(4, 0.0001, "Bob's 2 is gone rather than counted as a nought");
		after.Mine.ShouldBeNull();
	}

	[Fact]
	public async Task WithdrawingARatingNeverGiven_IsSuccessRatherThanNotFound()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		Guid trackId = await ShareAsync(owner, "Coast run north");

		// A phone draining an outbox sends this twice, and a rider tapping their own star to clear
		// it does not need to be told they had not rated it.
		using HttpResponseMessage response = await reader.DeleteAsync($"{TracksUrl}/{trackId}/rating");

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		TrackRatingSummary after = (await response.Content.ReadFromJsonAsync<TrackRatingSummary>())!;

		after.Count.ShouldBe(0);
		after.Average.ShouldBeNull("nobody has rated it, which is not the same as a score of zero");
	}

	[Theory]
	[InlineData(0)]
	[InlineData(6)]
	[InlineData(-1)]
	public async Task StarsOutsideTheScale_AreRefusedWithASentence(int stars)
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		Guid trackId = await ShareAsync(owner, "Coast run north");

		using HttpResponseMessage response = await reader.PutAsJsonAsync(
			$"{TracksUrl}/{trackId}/rating",
			new RateTrackRequest(stars));

		// A 400 naming the scale, not a 500 from a check constraint.
		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task APrivateRoute_CannotBeRatedByAnybodyElse_AndIsA404()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		TrackSummary track = await UploadAsync(owner);

		using HttpResponseMessage response = await reader.PutAsJsonAsync(
			$"{TracksUrl}/{track.Id}/rating",
			new RateTrackRequest(5));

		// 404 and not 403, so a track id - which travels in links - cannot be used to ask which
		// identifiers are real (§15.4).
		response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task ARouteWhoseOwnerTheReaderBlocked_IsNotRateable()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		Guid trackId = await ShareAsync(owner, "Coast run north");

		Guid ownerId = await IdOfAsync(app, "DaveSmith");

		using HttpResponseMessage blocked = await reader.PostAsJsonAsync("/api/v1/blocks", new BlockUserRequest(ownerId));

		blocked.IsSuccessStatusCode.ShouldBeTrue(await blocked.Content.ReadAsStringAsync());

		using HttpResponseMessage response = await reader.PutAsJsonAsync(
			$"{TracksUrl}/{trackId}/rating",
			new RateTrackRequest(5));

		// The browse list already drops their routes; a block that left the rating reachable would
		// be a block with a hole in it (§17.7).
		response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
	}

	/// <summary>
	/// A block hides authored content. A rating is anonymous by construction - nothing anywhere
	/// says who gave a route three stars - so it stays in the tally, and every reader is shown the
	/// same number. Filtering it would leak, in the difference between two readers' averages, that
	/// a blocked rider had rated this route.
	/// </summary>
	[Fact]
	public async Task ABlockedRidersRating_StaysInTheAverageEverybodyElseSees()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient alice = await SignedInAsync(app, "AliceBrown");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		Guid trackId = await ShareAsync(owner, "Coast run north");

		await RateAsync(alice, trackId, 1);
		await RateAsync(reader, trackId, 5);

		Guid aliceId = await IdOfAsync(app, "AliceBrown");

		using HttpResponseMessage blocked = await reader.PostAsJsonAsync("/api/v1/blocks", new BlockUserRequest(aliceId));

		blocked.IsSuccessStatusCode.ShouldBeTrue(await blocked.Content.ReadAsStringAsync());

		TrackRatingSummary after = await ReadRatingAsync(reader, trackId);

		after.Count.ShouldBe(2);
		after.Average!.Value.ShouldBe(3, 0.0001);
	}

	[Fact]
	public async Task Browse_CarriesTheAverageAndTheCountOnEveryRow()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient alice = await SignedInAsync(app, "AliceBrown");
		using HttpClient bob = await SignedInAsync(app, "BobJones");

		Guid rated = await ShareAsync(owner, "Coast run north");
		Guid unrated = await ShareAsync(owner, "Hinterland loop", latitudeOffsetDeg: 1);

		await RateAsync(alice, rated, 5);
		await RateAsync(bob, rated, 4);

		SharedTrackPage page = (await alice.GetFromJsonAsync<SharedTrackPage>($"{TracksUrl}/shared?page=1"))!;

		SharedTrackSummary withStars = page.Items.Single(row => row.Id == rated);

		withStars.RatingCount.ShouldBe(2);
		withStars.RatingAverage!.Value.ShouldBe(4.5, 0.0001);

		SharedTrackSummary without = page.Items.Single(row => row.Id == unrated);

		without.RatingCount.ShouldBe(0);
		without.RatingAverage.ShouldBeNull("a route nobody rated has no score, which is not a score of zero");
	}

	/// <summary>
	/// Deleting the route takes its ratings with it. There is nothing left for them to be about,
	/// and rows pointing at a track that is gone would be a foreign key violation waiting for the
	/// next write.
	/// </summary>
	[Fact]
	public async Task DeletingTheRoute_TakesItsRatingsWithIt()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		Guid trackId = await ShareAsync(owner, "Coast run north");

		await RateAsync(reader, trackId, 4);

		using HttpResponseMessage deleted = await owner.DeleteAsync($"{TracksUrl}/{trackId}");

		deleted.IsSuccessStatusCode.ShouldBeTrue(await deleted.Content.ReadAsStringAsync());

		int left = await app.WithDatabaseAsync(database =>
			database.Set<Data.Tracks.TrackRating>().CountAsync(row => row.TrackId == trackId));

		left.ShouldBe(0);
	}

	// ---------- helpers ----------

	private static async Task<TrackRatingSummary> RateAsync(HttpClient client, Guid trackId, int stars)
	{
		using HttpResponseMessage response = await client.PutAsJsonAsync(
			$"{TracksUrl}/{trackId}/rating",
			new RateTrackRequest(stars));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<TrackRatingSummary>())!;
	}

	private static async Task<TrackRatingSummary> ReadRatingAsync(HttpClient client, Guid trackId) =>
		(await client.GetFromJsonAsync<TrackRatingSummary>($"{TracksUrl}/{trackId}/rating"))!;

	private static async Task<Guid> ShareAsync(
		HttpClient client,
		string name,
		double latitudeOffsetDeg = 0)
	{
		TrackSummary track = await UploadAsync(client, name, latitudeOffsetDeg: latitudeOffsetDeg);

		using HttpResponseMessage response = await client.PatchAsync(
			$"{TracksUrl}/{track.Id}/details",
			JsonContent.Create(new UpdateTrackDetailsRequest(null, null, TrackVisibilityDto.Public)));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return track.Id;
	}

	private static async Task<TrackSummary> UploadAsync(
		HttpClient client,
		string? name = "Morning loop",
		int points = 20,
		double latitudeOffsetDeg = 0)
	{
		TrackGeometry geometry = new(
		[
			.. Enumerable.Range(0, points).Select(index => new TrackPoint(
				GpxFixtures.BaseLatitude + latitudeOffsetDeg + (index * GpxFixtures.MetresToDegreesLatitude(20)),
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

	private static Task<Guid> IdOfAsync(DlrWebApplicationFactory app, string userName) =>
		app.WithDatabaseAsync(database =>
			database.Set<Data.Identity.AppUser>()
				.Where(user => user.UserName == userName)
				.Select(user => user.Id)
				.SingleAsync());

	private static async Task<HttpClient> SignedInAsync(
		DlrWebApplicationFactory app,
		string userName = "DaveSmith")
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}
}
