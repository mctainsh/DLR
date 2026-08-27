using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Moderation;
using DLR.Core.Contracts.Rides;
using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using DLR.TestSupport.Tracks;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Comments;

/// <summary>
/// A shared route's thread (§6.2, §17).
/// <para>
/// The conversation machinery itself is <c>CommentTests</c>'s subject and is not retested here —
/// it is the same table, the same controller and the same code path, which is the whole point of
/// the arrangement. What these tests are about is the half that <em>is</em> different: who gets in.
/// An adventure's thread is the people the organiser admitted; a route's is everybody, right up to
/// the moment the route comes off the list or somebody blocks its owner.
/// </para>
/// <para>
/// Two of them are about the seam rather than the feature — that a route comment cannot be
/// mistaken for a ride comment by the queries, and that the idempotency index actually decides
/// anything on a column that is null for half the table. Both are the kind of thing that looks
/// obviously right and is obviously wrong the first time somebody drains an outbox twice.
/// </para>
/// </summary>
public sealed class RouteThreadTests(PostgresFixture postgres)
{
	private const string TracksUrl = "/api/v1/tracks";

	[Fact]
	public async Task AnySignedInRider_CanReadAndPostToASharedRoutesThread()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient stranger = await SignedInAsync(app, "RileyJones");

		Guid trackId = await ShareAsync(owner, "Coast run north");

		CommentDto posted = await PostAsync(stranger, trackId, "Gravel after the second bridge.");

		posted.TrackId.ShouldBe(trackId);
		posted.GroupRideId.ShouldBeNull("a post hangs off one subject, and this one hangs off a route");
		posted.AuthorUserName.ShouldBe("RileyJones");

		// And the owner reads it, which is the point of a conversation about a published route.
		CommentPage page = await ThreadAsync(owner, trackId);

		page.Comments.Single().Body.ShouldBe("Gravel after the second bridge.");
	}

	[Fact]
	public async Task APrivateRoutesThread_IsA404ToEverybodyButItsOwner()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient stranger = await SignedInAsync(app, "RileyJones");

		TrackSummary track = await UploadAsync(owner);

		using HttpResponseMessage read = await stranger.GetAsync($"{TracksUrl}/{track.Id}/comments");

		read.StatusCode.ShouldBe(HttpStatusCode.NotFound, await read.Content.ReadAsStringAsync());

		// The owner still reaches their own, so a route that was never shared is not a thread the
		// person who recorded it is locked out of.
		using HttpResponseMessage mine = await owner.GetAsync($"{TracksUrl}/{track.Id}/comments");

		mine.StatusCode.ShouldBe(HttpStatusCode.OK, await mine.Content.ReadAsStringAsync());
	}

	/// <summary>
	/// Un-sharing hides the thread and keeps the posts. Deleting a conversation because somebody
	/// took a route off a list would destroy other people's writing over a reversible act — and
	/// re-sharing has to bring the thread back rather than start a new one.
	/// </summary>
	[Fact]
	public async Task UnsharingHidesTheThread_AndResharingBringsItBack()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		Guid trackId = await ShareAsync(owner, "Coast run north");

		await PostAsync(reader, trackId, "Worth the climb.");

		await SetVisibilityAsync(owner, trackId, TrackVisibilityDto.Private);

		using HttpResponseMessage hidden = await reader.GetAsync($"{TracksUrl}/{trackId}/comments");

		hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound, await hidden.Content.ReadAsStringAsync());

		await SetVisibilityAsync(owner, trackId, TrackVisibilityDto.Public);

		CommentPage back = await ThreadAsync(reader, trackId);

		back.Comments.Single().Body.ShouldBe("Worth the climb.");
	}

	[Fact]
	public async Task ARouteWhoseOwnerTheReaderBlocked_HasNoThreadForThem()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		Guid trackId = await ShareAsync(owner, "Coast run north");

		Guid ownerId = await IdOfAsync(app, "DaveSmith");

		using HttpResponseMessage blocked = await reader.PostAsJsonAsync("/api/v1/blocks", new BlockUserRequest(ownerId));

		blocked.IsSuccessStatusCode.ShouldBeTrue(await blocked.Content.ReadAsStringAsync());

		using HttpResponseMessage response = await reader.GetAsync($"{TracksUrl}/{trackId}/comments");

		// The browse list already drops their routes (§17.7); leaving the thread reachable would
		// be a block with a hole in it.
		response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
	}

	/// <summary>
	/// The route's owner runs its thread, on the organiser's reasoning exactly (§17.6, §17.7).
	/// </summary>
	[Fact]
	public async Task TheRoutesOwner_CanDeleteAndPinSomebodyElsesPost()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		Guid trackId = await ShareAsync(owner, "Coast run north");

		CommentDto keep = await PostAsync(reader, trackId, "Worth the climb.");
		CommentDto remove = await PostAsync(reader, trackId, "Also worth the climb.");

		using HttpResponseMessage pinned = await owner.PostAsJsonAsync(
			$"/api/v1/comments/{keep.Id}/pin",
			new PinCommentRequest(true));

		pinned.StatusCode.ShouldBe(HttpStatusCode.OK, await pinned.Content.ReadAsStringAsync());

		using HttpResponseMessage deleted = await owner.DeleteAsync($"/api/v1/comments/{remove.Id}");

		deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());

		CommentPage page = await ThreadAsync(owner, trackId);

		page.Pinned.Single().Id.ShouldBe(keep.Id);
		page.Comments.ShouldNotContain(comment => comment.Id == remove.Id);
	}

	[Fact]
	public async Task AReader_CannotPinOrDeleteSomebodyElsesPost()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient alice = await SignedInAsync(app, "AliceBrown");
		using HttpClient bob = await SignedInAsync(app, "BobJones");

		Guid trackId = await ShareAsync(owner, "Coast run north");

		CommentDto hers = await PostAsync(alice, trackId, "Worth the climb.");

		using HttpResponseMessage pin = await bob.PostAsJsonAsync(
			$"/api/v1/comments/{hers.Id}/pin",
			new PinCommentRequest(true));

		pin.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await pin.Content.ReadAsStringAsync());

		using HttpResponseMessage delete = await bob.DeleteAsync($"/api/v1/comments/{hers.Id}");

		delete.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await delete.Content.ReadAsStringAsync());
	}

	/// <summary>
	/// The unique index PostgreSQL can actually decide on. <c>ux_ride_comment_client</c> leads on
	/// <c>group_ride_id</c>, which is null for every route comment — and nulls are distinct in a
	/// unique index, so that one lets every re-send through. This is the second index earning its
	/// place.
	/// </summary>
	[Fact]
	public async Task RepostingTheSameClientGuid_IsTheSamePostRatherThanASecondOne()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		Guid trackId = await ShareAsync(owner, "Coast run north");

		Guid clientGuid = Guid.NewGuid();

		CommentDto first = await PostAsync(reader, trackId, "Worth the climb.", clientGuid);
		CommentDto second = await PostAsync(reader, trackId, "Worth the climb.", clientGuid);

		second.Id.ShouldBe(first.Id, "a drained outbox re-sending a post is not a second post (§17.3)");

		(await ThreadAsync(reader, trackId)).Comments.Count.ShouldBe(1);
	}

	/// <summary>
	/// The two threads are one table, so every query that reads one has to exclude the other. A
	/// filter that compared only the id it cared about would make an adventure's thread the union
	/// of itself and every route comment ever written.
	/// </summary>
	[Fact]
	public async Task ARoutesThreadAndAnAdventuresThread_DoNotLeakIntoEachOther()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		Guid trackId = await ShareAsync(rider, "Coast run north");

		using HttpResponseMessage created = await rider.PostAsJsonAsync(
			"/api/v1/group-rides",
			new CreateRideRequest(
				"Sunday run",
				DlrWebApplicationFactory.DefaultStart.AddDays(3),
				JoinPolicy: JoinPolicyDto.Open));

		created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

		RideDetail ride = (await created.Content.ReadFromJsonAsync<RideDetail>())!;

		await PostAsync(rider, trackId, "About the road.");

		using HttpResponseMessage toRide = await rider.PostAsJsonAsync(
			$"/api/v1/group-rides/{ride.Id}/comments",
			new PostCommentRequest(Guid.NewGuid(), "About the ride."));

		toRide.StatusCode.ShouldBe(HttpStatusCode.Created, await toRide.Content.ReadAsStringAsync());

		CommentPage routeThread = await ThreadAsync(rider, trackId);
		CommentPage rideThread = (await rider.GetFromJsonAsync<CommentPage>($"/api/v1/group-rides/{ride.Id}/comments"))!;

		routeThread.Comments.Single().Body.ShouldBe("About the road.");
		rideThread.Comments.Single().Body.ShouldBe("About the ride.");
	}

	/// <summary>
	/// Reporting has to reach the most public thread on the service, which is exactly the one a
	/// membership check would have left unreportable — and a report on a route's post carries no
	/// ride, because there is no organiser to route it to.
	/// </summary>
	[Fact]
	public async Task APostOnARoutesThread_CanBeReported()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient author = await SignedInAsync(app, "AliceBrown");
		using HttpClient reporter = await SignedInAsync(app, "BobJones");

		Guid trackId = await ShareAsync(owner, "Coast run north");

		CommentDto post = await PostAsync(author, trackId, "Something objectionable.");

		using HttpResponseMessage response = await reporter.PostAsJsonAsync(
			$"/api/v1/comments/{post.Id}/report",
			new ReportContentRequest("Not on."));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		Guid? rideOnReport = await app.WithDatabaseAsync(database =>
			database.Set<Data.Moderation.ContentReport>()
				.Where(report => report.TargetId == post.Id)
				.Select(report => report.GroupRideId)
				.SingleAsync());

		rideOnReport.ShouldBeNull("a route's post has no organiser to route the report to");
	}

	[Fact]
	public async Task DeletingTheRoute_TakesItsThreadWithIt()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		Guid trackId = await ShareAsync(owner, "Coast run north");

		await PostAsync(reader, trackId, "Worth the climb.");

		using HttpResponseMessage deleted = await owner.DeleteAsync($"{TracksUrl}/{trackId}");

		deleted.IsSuccessStatusCode.ShouldBeTrue(await deleted.Content.ReadAsStringAsync());

		int left = await app.WithDatabaseAsync(database =>
			database.Set<Data.Comments.RideComment>().CountAsync(row => row.TrackId == trackId));

		left.ShouldBe(0);
	}

	/// <summary>
	/// Reactions never learned that a second kind of thread exists — they key on a comment id and
	/// ask <c>CommentThreadAccess</c> the same question they always asked. This is that claim
	/// tested rather than asserted.
	/// </summary>
	[Fact]
	public async Task ReactionsWorkOnARoutesThreadWithoutAnythingBeingAddedForThem()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		Guid trackId = await ShareAsync(owner, "Coast run north");

		CommentDto post = await PostAsync(reader, trackId, "Worth the climb.");

		using HttpResponseMessage reacted = await owner.PutAsJsonAsync(
			$"/api/v1/comments/{post.Id}/reaction",
			new SetReactionRequest("like"));

		reacted.StatusCode.ShouldBe(HttpStatusCode.OK, await reacted.Content.ReadAsStringAsync());

		ReactionCounts counts = (await reacted.Content.ReadFromJsonAsync<ReactionCounts>())!;

		counts.Counts["like"].ShouldBe(1);
		counts.Mine.ShouldBe("like");
	}

	// ---------- helpers ----------

	private static async Task<CommentDto> PostAsync(
		HttpClient client,
		Guid trackId,
		string body,
		Guid? clientGuid = null)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			$"{TracksUrl}/{trackId}/comments",
			new PostCommentRequest(clientGuid ?? Guid.NewGuid(), body));

		// Created for a new post, OK for one the server had already seen (§17.3).
		response.StatusCode.ShouldBeOneOf(
			[HttpStatusCode.Created, HttpStatusCode.OK],
			await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<CommentDto>())!;
	}

	private static async Task<CommentPage> ThreadAsync(HttpClient client, Guid trackId) =>
		(await client.GetFromJsonAsync<CommentPage>($"{TracksUrl}/{trackId}/comments"))!;

	private static async Task<Guid> ShareAsync(HttpClient client, string name)
	{
		TrackSummary track = await UploadAsync(client, name);

		await SetVisibilityAsync(client, track.Id, TrackVisibilityDto.Public);

		return track.Id;
	}

	private static async Task SetVisibilityAsync(HttpClient client, Guid trackId, TrackVisibilityDto visibility)
	{
		using HttpResponseMessage response = await client.PatchAsync(
			$"{TracksUrl}/{trackId}/details",
			JsonContent.Create(new UpdateTrackDetailsRequest(null, null, visibility)));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
	}

	private static async Task<TrackSummary> UploadAsync(HttpClient client, string? name = "Morning loop")
	{
		TrackGeometry geometry = new(
		[
			.. Enumerable.Range(0, 20).Select(index => new TrackPoint(
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
