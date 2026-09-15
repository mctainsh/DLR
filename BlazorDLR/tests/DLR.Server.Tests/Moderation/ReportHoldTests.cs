using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Moderation;
using DLR.Core.Contracts.Photos;
using DLR.Core.Contracts.Rides;
using DLR.Server.Data.Moderation;
using DLR.Server.Data.Rides;
using DLR.Server.Hubs;
using DLR.Server.Tests.Admin;
using DLR.Server.Tests.Hubs;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using DLR.TestSupport.Photos;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Moderation;

/// <summary>
/// Reported content comes down at once and stays down until it is judged (§17.7, §10.2).
/// <para>
/// App Store guideline 1.2 asks that objectionable content be removed from the feed
/// <em>instantly</em>, not queued behind an operator waking up. The cost is that one report from
/// one account hides a post from everybody, so the restore path is tested as carefully as the hold
/// itself - without it the hold is a way for anybody to silence anybody.
/// </para>
/// </summary>
public sealed class ReportHoldTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";
	private const string CommentsUrl = "/api/v1/comments";
	private const string MarkersUrl = "/api/v1/markers";
	private const string ReportsUrl = "/api/v1/admin/reports";

	/// <summary>
	/// The whole of what 1.2 asks for on the content half: report it, and it is gone for everybody
	/// straight away - including for the person who wrote it and for the organiser.
	/// </summary>
	[Fact]
	public async Task Report_HidesTheCommentFromEverybodyAtOnce()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: OperatorAnd());

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient author = await SignedInAsync(app, "SamJones");
		using HttpClient reporter = await SignedInAsync(app, "AlexLee");

		RideDetail ride = await CreateRideAsync(organiser);
		await JoinAsync(app, author, ride.Id);
		await JoinAsync(app, reporter, ride.Id);

		CommentDto offending = await PostAsync(author, ride.Id, "Something worth reporting");
		CommentDto innocent = await PostAsync(author, ride.Id, "Fuel stop in ten minutes");

		await ReportCommentAsync(reporter, offending.Id);

		foreach ((string who, HttpClient client) in new[]
		{
			("the reporter", reporter),
			("the author", author),
			("the organiser", organiser),
		})
		{
			CommentPage page = await ThreadAsync(client, ride.Id);

			page.Comments.ShouldNotContain(
				comment => comment.Id == offending.Id,
				$"a reported post is held from {who} too");

			page.Comments.ShouldContain(
				comment => comment.Id == innocent.Id,
				"holding one post must not take the thread with it");
		}
	}

	/// <summary>A marker's note and photograph are content a member can see, so it holds the same way.</summary>
	[Fact]
	public async Task Report_HidesTheMarkerFromEverybodyAtOnce()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: OperatorAnd());

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient author = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);
		await JoinAsync(app, author, ride.Id);

		MarkerDto marker = await CreateMarkerAsync(author, ride.Id);

		(await MarkersAsync(organiser, ride.Id)).ShouldContain(row => row.Id == marker.Id);

		using (HttpResponseMessage filed = await organiser.PostAsJsonAsync(
			$"{MarkersUrl}/{marker.Id}/report",
			new ReportContentRequest("A photograph that should not be there")))
		{
			filed.StatusCode.ShouldBe(HttpStatusCode.OK, await filed.Content.ReadAsStringAsync());
		}

		(await MarkersAsync(organiser, ride.Id)).ShouldBeEmpty();
		(await MarkersAsync(author, ride.Id)).ShouldBeEmpty("held from its own author as well");
	}

	/// <summary>
	/// A hold has to cover the write paths, not only the read ones.
	/// <para>
	/// Editing a marker broadcasts <c>MarkerUpdated</c> and every client upserts what it is sent,
	/// so a hold applied only where content is projected would let the author of a reported pin put
	/// it back on every map in the adventure - with fresh content - simply by saving it again.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Report_StopsTheAuthorEditingTheMarkerBackIntoView()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: OperatorAnd());

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient author = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);
		await JoinAsync(app, author, ride.Id);

		MarkerDto marker = await CreateMarkerAsync(author, ride.Id);

		using (HttpResponseMessage filed = await organiser.PostAsJsonAsync(
			$"{MarkersUrl}/{marker.Id}/report",
			new ReportContentRequest("A photograph that should not be there")))
		{
			filed.StatusCode.ShouldBe(HttpStatusCode.OK, await filed.Content.ReadAsStringAsync());
		}

		using HttpResponseMessage edited = await author.PutAsJsonAsync(
			$"{MarkersUrl}/{marker.Id}",
			new UpdateMarkerRequest(
				PositionScale.FromDegrees(-33.86),
				PositionScale.FromDegrees(151.20),
				"hazard",
				"Something else entirely"));

		edited.StatusCode.ShouldBe(
			HttpStatusCode.NotFound,
			"a held marker is not there for anybody, including the account that wrote it");

		(await MarkersAsync(organiser, ride.Id)).ShouldBeEmpty("and it stays off the map");
	}

	/// <summary>
	/// Every write path, not the two somebody remembered.
	/// <para>
	/// The hold used to be checked in <c>EditAsync</c> and <c>PinAsync</c> by hand, which left
	/// react, vote and close-poll resolving the same post through the same helper with no check at
	/// all - so a post hidden from everybody could still be reacted to and voted in, and the
	/// reaction broadcast to the adventure. It lives in <c>ForCommentAsync</c> now, which is the
	/// chokepoint all six already went through.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Report_ClosesEveryWritePathOnTheHeldPost()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: OperatorAnd());

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient author = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);
		await JoinAsync(app, author, ride.Id);

		CommentDto post = await PostAsync(author, ride.Id, "Something worth reporting");

		await ReportCommentAsync(organiser, post.Id);

		// Edit and pin, which were guarded by hand...
		using (HttpResponseMessage edited = await author.PatchAsJsonAsync(
			$"{CommentsUrl}/{post.Id}",
			new EditCommentRequest("Something else entirely")))
		{
			edited.StatusCode.ShouldBe(HttpStatusCode.NotFound, "edit");
		}

		using (HttpResponseMessage pinned = await organiser.PostAsJsonAsync(
			$"{CommentsUrl}/{post.Id}/pin",
			new PinCommentRequest(true)))
		{
			pinned.StatusCode.ShouldBe(HttpStatusCode.NotFound, "pin");
		}

		// ...and the three that were not.
		using (HttpResponseMessage reacted = await organiser.PutAsJsonAsync(
			$"{CommentsUrl}/{post.Id}/reaction",
			new SetReactionRequest("👍")))
		{
			reacted.StatusCode.ShouldBe(
				HttpStatusCode.NotFound,
				"reacting to a post nobody can see would broadcast a tally for it");
		}

		using (HttpResponseMessage voted = await organiser.PostAsJsonAsync(
			$"{CommentsUrl}/{post.Id}/votes",
			new CastVoteRequest([Guid.NewGuid()])))
		{
			voted.StatusCode.ShouldBe(HttpStatusCode.NotFound, "vote");
		}

		// Deleting is the exception, and deliberately so: taking held content down is the outcome
		// the hold waits for, and ContentReport's snapshot exists precisely so that an organiser
		// removing an abusive post does not destroy the evidence for the report against it.
		using HttpResponseMessage deleted = await organiser.DeleteAsync($"{CommentsUrl}/{post.Id}");

		deleted.StatusCode.ShouldBe(
			HttpStatusCode.NoContent,
			"the organiser is the fastest moderator a thread has and must not be blocked by a report");
	}

	/// <summary>
	/// A photograph attached to held content is the thing that was reported.
	/// <para>
	/// Holding the marker removes every reference to the picture, which is not the same as removing
	/// the picture: <c>GET /api/v1/photos/{id}</c> has no visibility rule of its own, so an id noted
	/// before the report would otherwise still fetch the bytes - and for a photo report that is the
	/// whole of what was objected to.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Report_StopsThePhotographOnHeldContentBeingServed()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: OperatorAnd());

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient author = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);
		await JoinAsync(app, author, ride.Id);

		MarkerDto marker = await CreateMarkerAsync(author, ride.Id);

		PhotoUploaded photo = await UploadAsync(author, ImageFixtures.Jpeg(640, 480));

		using (HttpResponseMessage attached = await author.PatchAsJsonAsync(
			$"{MarkersUrl}/{marker.Id}/photo",
			new AttachPhotoRequest(photo.PhotoId)))
		{
			attached.StatusCode.ShouldBe(HttpStatusCode.OK, await attached.Content.ReadAsStringAsync());
		}

		// Readable by another member before the report, which is the point of attaching it.
		using (HttpResponseMessage before = await organiser.GetAsync($"/api/v1/photos/{photo.PhotoId}"))
		{
			before.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		using (HttpResponseMessage filed = await organiser.PostAsJsonAsync(
			$"{MarkersUrl}/{marker.Id}/report",
			new ReportContentRequest("A photograph that should not be there")))
		{
			filed.StatusCode.ShouldBe(HttpStatusCode.OK, await filed.Content.ReadAsStringAsync());
		}

		using HttpResponseMessage after = await organiser.GetAsync($"/api/v1/photos/{photo.PhotoId}");

		after.StatusCode.ShouldBe(
			HttpStatusCode.NotFound,
			"the picture is what was reported - hiding only the pin that points at it is not hiding it");
	}

	/// <summary>
	/// The half that makes the hold defensible. A report can be wrong, and restoring has to put the
	/// post back for everybody rather than merely close a ticket.
	/// </summary>
	[Fact]
	public async Task Restore_PutsTheCommentBack()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: OperatorAnd("TheAdmin"));

		using HttpClient admin = await SignedInAsync(app, "TheAdmin");
		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient author = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);
		await JoinAsync(app, author, ride.Id);

		CommentDto post = await PostAsync(author, ride.Id, "Perfectly ordinary");

		await ReportCommentAsync(organiser, post.Id);

		OpenReport[] queue = (await admin.GetFromJsonAsync<OpenReport[]>(ReportsUrl))!;

		queue.Length.ShouldBe(1);
		queue[0].TargetId.ShouldBe(post.Id);
		queue[0].AuthorUserName.ShouldBe("SamJones");
		queue[0].ReportedByUserName.ShouldBe("DaveSmith");
		queue[0].ContentSnapshot.ShouldContain("Perfectly ordinary");

		using (HttpResponseMessage restored = await admin.PostAsJsonAsync(
			$"{ReportsUrl}/{queue[0].ReportId}/restore",
			new { }))
		{
			restored.StatusCode.ShouldBe(HttpStatusCode.OK, await restored.Content.ReadAsStringAsync());
		}

		CommentPage page = await ThreadAsync(organiser, ride.Id);

		page.Comments.ShouldContain(comment => comment.Id == post.Id, "a cleared report releases the post");

		// And the queue is empty, so the operator is not looking at a row that does nothing.
		(await admin.GetFromJsonAsync<OpenReport[]>(ReportsUrl))!.ShouldBeEmpty();
	}

	/// <summary>
	/// Restoring has to reach the clients that already dropped the post, not only the next reader.
	/// <para>
	/// Reporting broadcasts a removal, so without an inverse an operator clearing a bad report
	/// changes nothing anybody can see until they reopen the adventure - and a restore that had
	/// stopped working would look identical to one that worked. The message is
	/// <c>CommentRestored</c> rather than <c>CommentPosted</c> because the latter is what raises a
	/// notification on every member's phone, and a post coming back is not news.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Restore_TellsTheAdventureThePostIsBack()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: OperatorAnd("TheAdmin"));

		using HttpClient admin = await SignedInAsync(app, "TheAdmin");
		(HttpClient organiser, TokenResponse organiserSession) = await SignedInWithSessionAsync(app, "DaveSmith");
		using HttpClient author = await SignedInAsync(app, "SamJones");

		using (organiser)
		{
			RideDetail ride = await CreateRideAsync(organiser);
			await JoinAsync(app, author, ride.Id);

			CommentDto post = await PostAsync(author, ride.Id, "Perfectly ordinary");

			await using HubConnection listener = await HubClient.ConnectAsync(app, organiserSession);
			await listener.InvokeAsync(nameof(RideHub.JoinRide), ride.Id);

			Task<CommentDto> restoredOnTheWire = HubClient.NextRestoreAsync(listener);

			await ReportCommentAsync(organiser, post.Id);

			OpenReport[] queue = (await admin.GetFromJsonAsync<OpenReport[]>(ReportsUrl))!;

			using HttpResponseMessage restored = await admin.PostAsJsonAsync(
				$"{ReportsUrl}/{queue[0].ReportId}/restore",
				new { });

			restored.StatusCode.ShouldBe(HttpStatusCode.OK, await restored.Content.ReadAsStringAsync());

			CommentDto back = await restoredOnTheWire.WaitAsync(Arrives);

			back.Id.ShouldBe(post.Id);
			back.Body.ShouldBe("Perfectly ordinary");
		}
	}

	/// <summary>
	/// Two people reporting the same post is one decision, not two. Resolving only the report that
	/// was pressed would leave the content held by its sibling and the operator pressing Restore at
	/// a row that never changes.
	/// </summary>
	[Fact]
	public async Task Restore_ClearsEveryReportOnTheSameContent()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: OperatorAnd("TheAdmin"));

		using HttpClient admin = await SignedInAsync(app, "TheAdmin");
		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient author = await SignedInAsync(app, "SamJones");
		using HttpClient second = await SignedInAsync(app, "AlexLee");

		RideDetail ride = await CreateRideAsync(organiser);
		await JoinAsync(app, author, ride.Id);
		await JoinAsync(app, second, ride.Id);

		CommentDto post = await PostAsync(author, ride.Id, "Contentious");

		await ReportCommentAsync(organiser, post.Id);
		await ReportCommentAsync(second, post.Id);

		OpenReport[] queue = (await admin.GetFromJsonAsync<OpenReport[]>(ReportsUrl))!;

		queue.Length.ShouldBe(2);
		queue.ShouldAllBe(report => report.ReportsOnTarget == 2, "the pile-up is visible to the operator");

		using HttpResponseMessage restored = await admin.PostAsJsonAsync(
			$"{ReportsUrl}/{queue[0].ReportId}/restore",
			new { });

		ReportResolved outcome = (await restored.Content.ReadFromJsonAsync<ReportResolved>())!;

		outcome.ReportsResolved.ShouldBe(2);
		outcome.ContentRemoved.ShouldBeFalse();

		CommentPage page = await ThreadAsync(organiser, ride.Id);

		page.Comments.ShouldContain(comment => comment.Id == post.Id);
	}

	/// <summary>Removing deletes the content and closes the report, and the snapshot outlives both.</summary>
	[Fact]
	public async Task Remove_DeletesTheContentAndKeepsTheEvidence()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: OperatorAnd("TheAdmin"));

		using HttpClient admin = await SignedInAsync(app, "TheAdmin");
		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient author = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);
		await JoinAsync(app, author, ride.Id);

		CommentDto post = await PostAsync(author, ride.Id, "Genuinely abusive");

		await ReportCommentAsync(organiser, post.Id);

		OpenReport[] queue = (await admin.GetFromJsonAsync<OpenReport[]>(ReportsUrl))!;

		using (HttpResponseMessage removed = await admin.PostAsJsonAsync(
			$"{ReportsUrl}/{queue[0].ReportId}/remove",
			new { }))
		{
			removed.StatusCode.ShouldBe(HttpStatusCode.OK, await removed.Content.ReadAsStringAsync());
		}

		int comments = await app.WithDatabaseAsync(database =>
			database.Set<Data.Comments.RideComment>().CountAsync());

		comments.ShouldBe(0, "the post is gone for good");

		ContentReport report = await app.WithDatabaseAsync(database =>
			database.Set<ContentReport>().SingleAsync());

		report.ResolvedUtc.ShouldNotBeNull("the report is closed");
		report.ContentSnapshot.ShouldContain("Genuinely abusive", Case.Insensitive);
	}

	/// <summary>
	/// The queue is the operator's, and reaching it is the roster's business rather than
	/// membership of anything. An ordinary account asking is a 403, not a shorter list.
	/// </summary>
	[Fact]
	public async Task Queue_IsRefusedToAnybodyNotOnTheRoster()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: OperatorAnd("TheAdmin"));

		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		using HttpResponseMessage response = await rider.GetAsync(ReportsUrl);

		response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
	}

	/// <summary>
	/// The roster, plus enough room on §7.8's registration ladder for the cast these tests need.
	/// Four accounts from one address is past the shipping threshold, and a test about moderation
	/// should fail on moderation rather than on abuse throttling.
	/// </summary>
	private static Dictionary<string, string?> OperatorAnd(params string[] admins)
	{
		Dictionary<string, string?> settings = AdminRosterSettings.Roster(admins);

		settings["Abuse:FreeAccountsPerAddress"] = "20";

		return settings;
	}

	/// <summary>How long a broadcast gets to arrive before the test calls it missing.</summary>
	private static readonly TimeSpan Arrives = TimeSpan.FromSeconds(10);


	/// <summary>
	/// Signs an account in and keeps its session, which a hub connection needs and an
	/// <see cref="HttpClient"/> does not expose.
	/// </summary>
	private static async Task<(HttpClient Client, TokenResponse Session)> SignedInWithSessionAsync(
		DlrWebApplicationFactory app,
		string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return (app.CreateClient().Authenticated(session), session);
	}

	/// <summary>Uploads a picture and returns what the server called it.</summary>
	private static async Task<PhotoUploaded> UploadAsync(HttpClient client, byte[] bytes)
	{
		using MultipartFormDataContent form = [];
		using ByteArrayContent file = new(bytes);

		file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
		form.Add(file, "file", "photo.jpg");

		using HttpResponseMessage response = await client.PostAsync("/api/v1/photos", form);

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<PhotoUploaded>())!;
	}

	private static async Task ReportCommentAsync(HttpClient client, Guid commentId)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			$"{CommentsUrl}/{commentId}/report",
			new ReportContentRequest("Reported by a reader"));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
	}

	private static async Task<CommentDto> PostAsync(HttpClient client, Guid rideId, string body)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			$"{RidesUrl}/{rideId}/comments",
			new PostCommentRequest(Guid.NewGuid(), body));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<CommentDto>())!;
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

	private static async Task<MarkerDto[]> MarkersAsync(HttpClient client, Guid rideId) =>
		(await client.GetFromJsonAsync<MarkerDto[]>($"{RidesUrl}/{rideId}/markers"))!;

	private static async Task<CommentPage> ThreadAsync(HttpClient client, Guid rideId)
	{
		using HttpResponseMessage response = await client.GetAsync($"{RidesUrl}/{rideId}/comments");

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<CommentPage>())!;
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
