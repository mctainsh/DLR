using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Moderation;
using DLR.Core.Contracts.Rides;
using DLR.Server.Data.Moderation;
using DLR.Server.Data.Rides;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Moderation;

/// <summary>
/// Reporting and blocking (§16.5, §17.7, §10.2).
/// <para>
/// This ships <strong>before the first store submission</strong>, not before the first comment.
/// Apple and Play both check that a way to report objectionable content and block its author
/// exists; a small, organiser-admitted audience makes abuse unlikely and does not make the
/// mechanism optional at review.
/// </para>
/// </summary>
public sealed class ModerationTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";
	private const string CommentsUrl = "/api/v1/comments";
	private const string MarkersUrl = "/api/v1/markers";
	private const string BlocksUrl = "/api/v1/blocks";

	/// <summary>
	/// The reason <c>ContentReport</c> carries a snapshot at all (§17.7).
	/// <para>
	/// Deleting an abusive comment is exactly what an organiser should do. It must not also destroy
	/// the evidence for the report just filed against it — which is what would happen if the report
	/// pointed at the row and read through it, and what a foreign key would have enforced.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Report_SnapshotSurvivesDeletionOfTheComment()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient author = await SignedInAsync(app, "SamJones");
		using HttpClient reporter = await SignedInAsync(app, "AlexLee");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, author, ride.Id);
		await JoinAsync(app, reporter, ride.Id);

		CommentDto offending = await PostAsync(author, ride.Id, "Something worth reporting");

		using (HttpResponseMessage filed = await reporter.PostAsJsonAsync(
			$"{CommentsUrl}/{offending.Id}/report",
			new ReportContentRequest("Abusive towards another traveller")))
		{
			filed.StatusCode.ShouldBe(HttpStatusCode.OK, await filed.Content.ReadAsStringAsync());
		}

		// The organiser does the right thing and removes it.
		using (HttpResponseMessage removed = await organiser.DeleteAsync($"{CommentsUrl}/{offending.Id}"))
		{
			removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		}

		ContentReport report = await app.WithDatabaseAsync(database =>
			database.Set<ContentReport>().SingleAsync());

		// The report is still there, and it still says what the comment said. A foreign key on
		// TargetId would have cascaded this away or refused the deletion — both wrong.
		report.TargetId.ShouldBe(offending.Id);
		report.TargetKind.ShouldBe(ReportTargetKind.Comment);
		report.ResolvedUtc.ShouldBeNull("it is still on the operator's queue");
		report.ContentSnapshot.ShouldContain("Something worth reporting");
		report.ContentSnapshot.ShouldContain("SamJones", Case.Insensitive);
		report.Reason.ShouldBe("Abusive towards another traveller");

		// And the comment really is gone, so the snapshot is the only copy.
		int comments = await app.WithDatabaseAsync(database =>
			database.Set<Data.Comments.RideComment>().CountAsync());

		comments.ShouldBe(0);
	}

	/// <summary>
	/// Blocking is one-directional and it hides the blocked rider's content from the blocker
	/// (§16.5) — markers as well as posts, because hiding one and not the other leaves the person
	/// you blocked writing on the map you are reading.
	/// </summary>
	[Fact]
	public async Task BlockedUser_CommentsAreHiddenFromTheBlocker()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient nuisance = await SignedInAsync(app, "SamJones");
		using HttpClient bystander = await SignedInAsync(app, "AlexLee");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, nuisance, ride.Id);
		await JoinAsync(app, bystander, ride.Id);

		CommentDto theirs = await PostAsync(nuisance, ride.Id, "The post nobody wants to read");
		CommentDto mine = await PostAsync(organiser, ride.Id, "Fuel at the servo in 8 km");

		MarkerDto theirMarker = await CreateMarkerAsync(nuisance, ride.Id);

		// Before the block, the organiser sees everything.
		CommentPage before = await ThreadAsync(organiser, ride.Id);

		before.Comments.Count.ShouldBe(2);
		(await MarkersAsync(organiser, ride.Id)).Length.ShouldBe(1);

		Guid nuisanceId = await IdOfAsync(app, "SamJones");

		using (HttpResponseMessage blocked = await organiser.PostAsJsonAsync(
			BlocksUrl,
			new BlockUserRequest(nuisanceId)))
		{
			blocked.StatusCode.ShouldBe(HttpStatusCode.NoContent, await blocked.Content.ReadAsStringAsync());
		}

		// Hidden from the blocker — posts and markers both.
		CommentPage after = await ThreadAsync(organiser, ride.Id);

		after.Comments.Select(comment => comment.Id).ShouldBe([mine.Id]);

		(await MarkersAsync(organiser, ride.Id)).ShouldBeEmpty();

		// Not deleted, and not hidden from anybody else. A block is one person's decision about
		// what they read, not a moderation action on the ride.
		CommentPage forBystander = await ThreadAsync(bystander, ride.Id);

		forBystander.Comments.Count.ShouldBe(2);
		(await MarkersAsync(bystander, ride.Id)).Length.ShouldBe(1);

		// And the blocked rider notices nothing at all — their own post is still theirs to see,
		// and nothing was sent to tell them.
		CommentPage forNuisance = await ThreadAsync(nuisance, ride.Id);

		forNuisance.Comments.Select(comment => comment.Id).ShouldContain(theirs.Id);
		app.Emails.Sent.ShouldNotContain(mail => mail.Subject.Contains("block", StringComparison.OrdinalIgnoreCase));

		// Unblocking puts it back — the row is the whole mechanism.
		using (HttpResponseMessage unblocked = await organiser.DeleteAsync($"{BlocksUrl}/{nuisanceId}"))
		{
			unblocked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		}

		(await ThreadAsync(organiser, ride.Id)).Comments.Count.ShouldBe(2);
		(await MarkersAsync(organiser, ride.Id)).Single().Id.ShouldBe(theirMarker.Id);
	}

	/// <summary>
	/// §17.7 says blocking hides a person's <em>reactions</em> too. A tally that still counted them
	/// would be the one place their presence leaked through, and a poll's names and its numbers
	/// must agree or the results read as a bug.
	/// </summary>
	[Fact]
	public async Task BlockedUser_ReactionsAndVotesAreHiddenToo()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient nuisance = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, nuisance, ride.Id);

		CommentDto post = await PostAsync(organiser, ride.Id, "Look at this corner");

		CommentDto poll = await PostPollAsync(organiser, ride.Id, "Who's coming?", ["Yes", "No"]);

		await ReactAsync(organiser, post.Id, "like");
		await ReactAsync(nuisance, post.Id, "like");

		await VoteAsync(organiser, poll.Id, [poll.Poll!.Options[0].Id]);
		await VoteAsync(nuisance, poll.Id, [poll.Poll.Options[0].Id]);

		CommentPage before = await ThreadAsync(organiser, ride.Id);

		Find(before, post.Id).Reactions!.Counts["like"].ShouldBe(2);
		Find(before, poll.Id).Poll!.Options[0].Votes.ShouldBe(2);

		Guid nuisanceId = await IdOfAsync(app, "SamJones");

		using (HttpResponseMessage blocked = await organiser.PostAsJsonAsync(
			BlocksUrl,
			new BlockUserRequest(nuisanceId)))
		{
			blocked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		}

		CommentPage after = await ThreadAsync(organiser, ride.Id);

		after.Comments.Count.ShouldBe(2, "the organiser's own posts are still there");

		Find(after, post.Id).Reactions!.Counts["like"].ShouldBe(1, "their reaction no longer counts");

		PollOptionResult option = Find(after, poll.Id).Poll!.Options[0];

		option.Votes.ShouldBe(1);
		option.Voters.Single().UserName.ShouldBe("DaveSmith");
	}

	[Fact]
	public async Task Report_ByNonMember_IsRefusedAndStoresNothing()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient stranger = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);

		CommentDto post = await PostAsync(organiser, ride.Id, "A post in an adventure they are not in");
		MarkerDto marker = await CreateMarkerAsync(organiser, ride.Id);

		using (HttpResponseMessage refused = await stranger.PostAsJsonAsync(
			$"{CommentsUrl}/{post.Id}/report",
			new ReportContentRequest("I do not like it")))
		{
			refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		}

		using (HttpResponseMessage refusedMarker = await stranger.PostAsJsonAsync(
			$"{MarkersUrl}/{marker.Id}/report",
			new ReportContentRequest("I do not like it")))
		{
			refusedMarker.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		}

		// An empty reason is refused too — a report nobody can act on is not a report.
		using (HttpResponseMessage blank = await organiser.PostAsJsonAsync(
			$"{CommentsUrl}/{post.Id}/report",
			new ReportContentRequest("   ")))
		{
			blank.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		}

		(await app.WithDatabaseAsync(database => database.Set<ContentReport>().CountAsync()))
			.ShouldBe(0);
	}

	/// <summary>
	/// Reporting twice is not two problems, and without the unique index a frustrated rider can
	/// manufacture a queue for the operator (§17.7).
	/// </summary>
	[Fact]
	public async Task Report_SameContentTwice_IsOneReport()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient reporter = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, reporter, ride.Id);

		CommentDto post = await PostAsync(organiser, ride.Id, "Something worth reporting");

		ContentReported first = await ReportAsync(reporter, post.Id, "Abusive");
		ContentReported again = await ReportAsync(reporter, post.Id, "Still abusive");

		// Answered as success rather than a conflict: a rider who taps report again wants to know
		// it was heard, not to be told off.
		again.ReportId.ShouldBe(first.ReportId);

		(await app.WithDatabaseAsync(database => database.Set<ContentReport>().CountAsync()))
			.ShouldBe(1);

		// A different rider reporting the same content is a second, separate report — two people
		// objecting is genuinely more signal than one.
		using HttpClient other = await SignedInAsync(app, "AlexLee");

		await JoinAsync(app, other, ride.Id);

		await ReportAsync(other, post.Id, "Agreed");

		(await app.WithDatabaseAsync(database => database.Set<ContentReport>().CountAsync()))
			.ShouldBe(2);
	}

	[Fact]
	public async Task Block_SelfOrUnknownRider_IsRefused()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		Guid ownId = await IdOfAsync(app, "DaveSmith");

		using (HttpResponseMessage self = await organiser.PostAsJsonAsync(
			BlocksUrl,
			new BlockUserRequest(ownId)))
		{
			self.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "that would hide your own posts from you");
		}

		using (HttpResponseMessage unknown = await organiser.PostAsJsonAsync(
			BlocksUrl,
			new BlockUserRequest(Guid.NewGuid())))
		{
			unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		}

		// Blocking twice is idempotent, not a conflict — a client re-sending from an outbox must
		// not see an error for something that is already true.
		using HttpClient other = await SignedInAsync(app, "SamJones");

		Guid otherId = await IdOfAsync(app, "SamJones");

		for (int i = 0; i < 2; i++)
		{
			using HttpResponseMessage blocked = await organiser.PostAsJsonAsync(
				BlocksUrl,
				new BlockUserRequest(otherId));

			blocked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		}

		BlockedRider[] list = (await organiser.GetFromJsonAsync<BlockedRider[]>(BlocksUrl))!;

		list.Single().UserName.ShouldBe("SamJones");

		// And the block list is the caller's own — it is not a public fact about anybody.
		(await other.GetFromJsonAsync<BlockedRider[]>(BlocksUrl))!.ShouldBeEmpty();
	}

	private static CommentDto Find(CommentPage page, Guid commentId) =>
		page.Comments.Concat(page.Pinned).First(comment => comment.Id == commentId);

	private static async Task<ContentReported> ReportAsync(HttpClient client, Guid commentId, string reason)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			$"{CommentsUrl}/{commentId}/report",
			new ReportContentRequest(reason));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<ContentReported>())!;
	}

	private static async Task<CommentDto> PostAsync(HttpClient client, Guid rideId, string body)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			$"{RidesUrl}/{rideId}/comments",
			new PostCommentRequest(Guid.NewGuid(), body));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<CommentDto>())!;
	}

	private static async Task<CommentDto> PostPollAsync(
		HttpClient client,
		Guid rideId,
		string question,
		IReadOnlyList<string> options)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			$"{RidesUrl}/{rideId}/comments",
			new PostCommentRequest(Guid.NewGuid(), question, Poll: new PollSpec(options)));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<CommentDto>())!;
	}

	private static async Task ReactAsync(HttpClient client, Guid commentId, string reaction)
	{
		using HttpResponseMessage response = await client.PutAsJsonAsync(
			$"{CommentsUrl}/{commentId}/reaction",
			new SetReactionRequest(reaction));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
	}

	private static async Task VoteAsync(HttpClient client, Guid commentId, IReadOnlyList<Guid> optionIds)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			$"{CommentsUrl}/{commentId}/votes",
			new CastVoteRequest(optionIds));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
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

	private static Task<Guid> IdOfAsync(DlrWebApplicationFactory app, string userName) =>
		app.WithDatabaseAsync(database =>
			database.Set<Data.Identity.AppUser>()
				.Where(user => user.UserName == userName)
				.Select(user => user.Id)
				.SingleAsync());

	private static async Task<HttpClient> SignedInAsync(DlrWebApplicationFactory app, string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}
}
