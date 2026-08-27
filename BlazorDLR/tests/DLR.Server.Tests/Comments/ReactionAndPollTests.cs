using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
using DLR.Server.Data.Comments;
using DLR.Server.Data.Rides;
using DLR.Server.Hubs;
using DLR.Server.Tests.Hubs;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Comments;

/// <summary>
/// Reactions and polls (§17.4, §17.5).
/// <para>
/// A poll is a comment — <c>Kind = Poll</c> with a record hanging off the same row — which is why
/// there is no separate posting path, no separate permission check and no separate pinning code to
/// test here. <c>Poll_IsPinnableAndReactableLikeAnyComment</c> is the assertion that says so.
/// </para>
/// </summary>
public sealed class ReactionAndPollTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";
	private const string CommentsUrl = "/api/v1/comments";

	[Fact]
	public async Task Reaction_SecondReactionBySameUser_ReplacesTheFirst()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		CommentDto post = await PostAsync(organiser, ride.Id, "Look at this corner");

		ReactionCounts first = await ReactAsync(organiser, post.Id, "like");

		first.Counts["like"].ShouldBe(1);
		first.Mine.ShouldBe("like");

		ReactionCounts second = await ReactAsync(organiser, post.Id, "love");

		// Replaced, not accumulated — the primary key on (comment, user) is what makes that a
		// property rather than a rule somebody has to remember (§17.4).
		second.Mine.ShouldBe("love");
		second.Counts["love"].ShouldBe(1);
		second.Counts.ShouldNotContainKey("like");

		int rows = await app.WithDatabaseAsync(database =>
			database.Set<CommentReaction>().CountAsync(reaction => reaction.CommentId == post.Id));

		rows.ShouldBe(1, "one narrow row per person per comment they cared about");
	}

	[Fact]
	public async Task Reaction_Cleared_RemovesTheRow()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		CommentDto post = await PostAsync(organiser, ride.Id, "Look at this corner");

		await ReactAsync(organiser, post.Id, "like");

		ReactionCounts cleared = await ReactAsync(organiser, post.Id, null);

		cleared.Mine.ShouldBeNull();
		cleared.Counts.ShouldBeEmpty();

		// The row goes. A "none" reaction stored as a row would be one per person per comment they
		// had ever looked at, which is the storage the fixed-set design exists to avoid.
		int rows = await app.WithDatabaseAsync(database =>
			database.Set<CommentReaction>().CountAsync(reaction => reaction.CommentId == post.Id));

		rows.ShouldBe(0);
	}

	/// <summary>
	/// A thread page is fifty posts in a ride of twelve. Enumerating who reacted to each would be
	/// most of the payload, for something almost nothing renders (§17.4).
	/// </summary>
	[Fact]
	public async Task Reaction_Response_CarriesAggregateCountsNotIndividualRows()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient sam = await SignedInAsync(app, "SamJones");
		using HttpClient alex = await SignedInAsync(app, "AlexLee");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, sam, ride.Id);
		await JoinAsync(app, alex, ride.Id);

		CommentDto post = await PostAsync(organiser, ride.Id, "Look at this corner");

		await ReactAsync(organiser, post.Id, "like");
		await ReactAsync(sam, post.Id, "like");
		await ReactAsync(alex, post.Id, "thanks");

		// Counts, plus the caller's own — and "own" is genuinely per caller.
		CommentPage asAlex = await ThreadAsync(alex, ride.Id);

		ReactionCounts counts = asAlex.Comments.Single().Reactions!;

		counts.Counts["like"].ShouldBe(2);
		counts.Counts["thanks"].ShouldBe(1);
		counts.Mine.ShouldBe("thanks");

		CommentPage asSam = await ThreadAsync(sam, ride.Id);

		asSam.Comments.Single().Reactions!.Mine.ShouldBe("like", "'mine' is the reader's, not the author's");

		// The wire carries no user ids for reactions at all. Asserted against the raw body, because
		// the rule is "who reacted is not in this response", not "one property is absent".
		using HttpResponseMessage raw = await alex.GetAsync($"{RidesUrl}/{ride.Id}/comments");

		string body = await raw.Content.ReadAsStringAsync();

		Guid samId = await IdOfAsync(app, "SamJones");

		body.ShouldNotContain(samId.ToString(), Case.Insensitive);
	}

	/// <summary>
	/// Reactions are the highest-frequency, lowest-value event in the product. A message per tap
	/// per connection is the O(n²) fan-out §5.3 already refused for positions (§17.4).
	/// </summary>
	[Fact]
	public async Task Reaction_ManyInQuickSuccession_CoalescesIntoOneHubMessage()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient registrar = app.CreateClient();

		TokenResponse organiserSession = await registrar.RegisterAsync("DaveSmith");

		using HttpClient organiser = app.CreateClient().Authenticated(organiserSession);
		using HttpClient sam = await SignedInAsync(app, "SamJones");
		using HttpClient alex = await SignedInAsync(app, "AlexLee");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, sam, ride.Id);
		await JoinAsync(app, alex, ride.Id);

		CommentDto post = await PostAsync(organiser, ride.Id, "Look at this corner");

		await using HubConnection connection = await HubClient.ConnectAsync(app, organiserSession);

		await connection.InvokeAsync("JoinRide", ride.Id);

		List<ReactionCounts> received = [];

		connection.On<Guid, ReactionCounts>(
			nameof(IRideClient.ReactionsUpdated),
			(_, counts) =>
			{
				lock (received)
				{
					received.Add(counts);
				}
			});

		// Five changes on the same comment, as fast as three riders can tap.
		await ReactAsync(organiser, post.Id, "like");
		await ReactAsync(sam, post.Id, "like");
		await ReactAsync(alex, post.Id, "love");
		await ReactAsync(organiser, post.Id, "wow");
		await ReactAsync(sam, post.Id, "thanks");

		lock (received)
		{
			received.ShouldBeEmpty("nothing goes out until the coalescing window closes");
		}

		// Driven directly rather than by advancing the clock and waiting on the PeriodicTimer.
		// That is a race twice over and SRV-22 already paid for learning it.
		await app.FlushReactionsAsync();

		await WaitForAsync(() =>
		{
			lock (received)
			{
				return received.Count > 0;
			}
		});

		lock (received)
		{
			received.Count.ShouldBe(1, "five changes to one comment are one message");

			// The tally as it stands at flush, not five deltas replayed. Both riders who reacted
			// twice had their first reaction *replaced*, so 'like' is not in this message at all —
			// which is the difference between coalescing and batching, and the reason the flush
			// re-reads the table instead of accumulating events.
			received[0].Counts.ShouldNotContainKey("like");
			received[0].Counts["love"].ShouldBe(1);
			received[0].Counts["wow"].ShouldBe(1);
			received[0].Counts["thanks"].ShouldBe(1);
			received[0].Counts.Values.Sum().ShouldBe(3, "three people reacted, whatever they tapped");

			// No "mine": a group message has one body and "mine" differs per connection.
			received[0].Mine.ShouldBeNull();
		}

		// A second flush with nothing dirty sends nothing.
		await app.FlushReactionsAsync();

		await Task.Delay(200);

		lock (received)
		{
			received.Count.ShouldBe(1);
		}
	}

	[Fact]
	public async Task Poll_WithFewerThanTwoOptions_Returns400()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		foreach (string[] options in new[] { Array.Empty<string>(), ["Yes"], new[] { "Yes", "  " } })
		{
			using HttpResponseMessage refused = await organiser.PostAsJsonAsync(
				$"{RidesUrl}/{ride.Id}/comments",
				new PostCommentRequest(
					Guid.NewGuid(),
					"Who's coming Saturday?",
					Poll: new PollSpec(options)));

			refused.StatusCode.ShouldBe(
				HttpStatusCode.BadRequest,
				$"options were [{string.Join(", ", options)}]");
		}

		// A poll needs a question, and the question is the body — there is no second field for it.
		using (HttpResponseMessage noQuestion = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/comments",
			new PostCommentRequest(Guid.NewGuid(), Body: null, Poll: new PollSpec(["Yes", "No"]))))
		{
			noQuestion.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		}

		// Two is enough.
		CommentDto poll = await PostPollAsync(organiser, ride.Id, "Who's coming Saturday?", ["Yes", "No"]);

		poll.Kind.ShouldBe(CommentKindDto.Poll);
		poll.Poll!.Options.Count.ShouldBe(2);
		poll.Poll.Options[0].Text.ShouldBe("Yes");
		poll.Poll.Options[0].Ordinal.ShouldBe(0);
	}

	[Fact]
	public async Task Poll_SingleSelect_ChangingVoteReplacesIt()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		CommentDto poll = await PostPollAsync(
			organiser,
			ride.Id,
			"Who's coming Saturday?",
			["Yes", "No", "Maybe"]);

		Guid yes = poll.Poll!.Options[0].Id;
		Guid maybe = poll.Poll.Options[2].Id;

		PollResults afterYes = await VoteAsync(organiser, poll.Id, [yes]);

		afterYes.MyOptionIds.ShouldBe([yes]);
		afterYes.Options[0].Votes.ShouldBe(1);

		PollResults afterMaybe = await VoteAsync(organiser, poll.Id, [maybe]);

		// Replaced. A single-select poll where changing your mind added a second vote would
		// produce a tally larger than the ride.
		afterMaybe.MyOptionIds.ShouldBe([maybe]);
		afterMaybe.Options[0].Votes.ShouldBe(0);
		afterMaybe.Options[2].Votes.ShouldBe(1);

		// And the endpoint refuses two at once, because the key on (option, user) cannot express
		// "one option across a poll" and would happily store both.
		using HttpResponseMessage two = await organiser.PostAsJsonAsync(
			$"{CommentsUrl}/{poll.Id}/votes",
			new CastVoteRequest([yes, maybe]));

		two.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		int rows = await app.WithDatabaseAsync(database =>
			database.Set<PollVote>().CountAsync());

		rows.ShouldBe(1);
	}

	[Fact]
	public async Task Poll_MultiSelect_TogglesOptionsIndependently()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		CommentDto poll = await PostPollAsync(
			organiser,
			ride.Id,
			"Which days work?",
			["Friday", "Saturday", "Sunday"],
			allowMultiple: true);

		Guid friday = poll.Poll!.Options[0].Id;
		Guid saturday = poll.Poll.Options[1].Id;
		Guid sunday = poll.Poll.Options[2].Id;

		PollResults two = await VoteAsync(organiser, poll.Id, [friday, sunday]);

		two.MyOptionIds.Order().ShouldBe(new[] { friday, sunday }.Order());

		// The request is the full set the voter now holds, so dropping one and adding another is
		// one call and the untouched option is genuinely untouched.
		PollResults changed = await VoteAsync(organiser, poll.Id, [friday, saturday]);

		changed.MyOptionIds.Order().ShouldBe(new[] { friday, saturday }.Order());
		changed.Options[0].Votes.ShouldBe(1, "Friday was held and stayed held");
		changed.Options[1].Votes.ShouldBe(1);
		changed.Options[2].Votes.ShouldBe(0, "Sunday was dropped");

		// An empty list clears, which is the only way to un-vote.
		PollResults none = await VoteAsync(organiser, poll.Id, []);

		none.MyOptionIds.ShouldBeEmpty();
		none.Options.Sum(option => option.Votes).ShouldBe(0);
	}

	[Fact]
	public async Task Poll_VoteAfterClose_Returns409()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient member = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, member, ride.Id);

		CommentDto poll = await PostPollAsync(organiser, ride.Id, "Who's coming?", ["Yes", "No"]);

		Guid yes = poll.Poll!.Options[0].Id;

		await VoteAsync(member, poll.Id, [yes]);

		// An ordinary member cannot close somebody else's poll.
		using (HttpResponseMessage refused = await member.PostAsync($"{CommentsUrl}/{poll.Id}/close-poll", null))
		{
			refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		}

		using (HttpResponseMessage closed = await organiser.PostAsync($"{CommentsUrl}/{poll.Id}/close-poll", null))
		{
			closed.StatusCode.ShouldBe(HttpStatusCode.OK, await closed.Content.ReadAsStringAsync());

			PollResults results = (await closed.Content.ReadFromJsonAsync<PollResults>())!;

			results.IsClosed.ShouldBeTrue();
			results.ClosedUtc.ShouldNotBeNull();
		}

		using HttpResponseMessage late = await member.PostAsJsonAsync(
			$"{CommentsUrl}/{poll.Id}/votes",
			new CastVoteRequest([poll.Poll.Options[1].Id]));

		late.StatusCode.ShouldBe(HttpStatusCode.Conflict);

		// Distinguishable, so a client says "this poll has closed" rather than "something went
		// wrong" (§17.5).
		(await late.Content.ReadAsStringAsync()).ShouldContain("PollClosed");

		// The vote already cast is still counted — closing shows results, it does not discard them.
		PollResults after = await ResultsAsync(member, ride.Id, poll.Id);

		after.Options[0].Votes.ShouldBe(1);
	}

	/// <summary>
	/// A background job flipping a flag would leave a window in which an elapsed poll still took
	/// votes — as wide as the job's interval, and widest exactly when the job is behind (§17.5).
	/// </summary>
	[Fact]
	public async Task Poll_ClosesUtcElapsed_RejectsVotesWithoutABackgroundJob()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		DateTimeOffset closes = app.Clock.GetUtcNow().AddMinutes(10);

		CommentDto poll = await PostPollAsync(
			organiser,
			ride.Id,
			"Who's coming?",
			["Yes", "No"],
			closesUtc: closes);

		poll.Poll!.IsClosed.ShouldBeFalse();

		// Just inside.
		app.Clock.Advance(TimeSpan.FromMinutes(9));

		await VoteAsync(organiser, poll.Id, [poll.Poll.Options[0].Id]);

		// Past the deadline. Nothing has run — no sweep, no job, no hosted service tick — and the
		// poll is nonetheless shut, because being shut is a comparison rather than a stored flag.
		app.Clock.Advance(TimeSpan.FromMinutes(2));

		await ReauthenticateAsync(app, organiser, "DaveSmith");

		using HttpResponseMessage late = await organiser.PostAsJsonAsync(
			$"{CommentsUrl}/{poll.Id}/votes",
			new CastVoteRequest([poll.Poll.Options[1].Id]));

		late.StatusCode.ShouldBe(HttpStatusCode.Conflict);

		PollResults results = await ResultsAsync(organiser, ride.Id, poll.Id);

		results.IsClosed.ShouldBeTrue();
		results.ClosedUtc.ShouldBeNull("nothing closed it — its deadline simply passed");

		// And the column was never written, which is what "without a background job" means.
		DateTimeOffset? stored = await app.WithDatabaseAsync(database =>
			database.Set<Poll>()
				.Where(row => row.CommentId == poll.Id)
				.Select(row => row.ClosedUtc)
				.SingleAsync());

		stored.ShouldBeNull();
	}

	/// <summary>
	/// The question people actually ask is "who's coming on Saturday?", and an anonymous tally
	/// answers a different, less useful one (§17.5).
	/// </summary>
	[Fact]
	public async Task Poll_Results_AreAttributedToVoters()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient sam = await SignedInAsync(app, "SamJones");
		using HttpClient alex = await SignedInAsync(app, "AlexLee");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, sam, ride.Id);
		await JoinAsync(app, alex, ride.Id);

		CommentDto poll = await PostPollAsync(organiser, ride.Id, "Who's coming Saturday?", ["Yes", "No"]);

		Guid yes = poll.Poll!.Options[0].Id;
		Guid no = poll.Poll.Options[1].Id;

		await VoteAsync(organiser, poll.Id, [yes]);
		await VoteAsync(sam, poll.Id, [yes]);

		// Results are visible before voting — this is a noticeboard, not a secret ballot, and a
		// rider deciding whether to come wants to know who else already is.
		PollResults beforeVoting = await ResultsAsync(alex, ride.Id, poll.Id);

		beforeVoting.MyOptionIds.ShouldBeEmpty();
		beforeVoting.Options[0].Votes.ShouldBe(2);
		beforeVoting.Options[0].Voters.Count.ShouldBe(2);

		await VoteAsync(alex, poll.Id, [no]);

		// Read by somebody else, because the point is that the whole ride can see it.
		PollResults results = await ResultsAsync(sam, ride.Id, poll.Id);

		results.Options[0].Voters.Select(voter => voter.UserName).Order()
			.ShouldBe(new[] { "DaveSmith", "SamJones" }.Order());

		results.Options[1].Voters.Single().UserName.ShouldBe("AlexLee");
	}

	/// <summary>
	/// The whole point of §17.5: a poll is a comment, so it inherits threading, pinning, reactions,
	/// permissions, reporting, deletion and the realtime path without a line of new code.
	/// </summary>
	[Fact]
	public async Task Poll_IsPinnableAndReactableLikeAnyComment()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		CommentDto poll = await PostPollAsync(organiser, ride.Id, "Who's coming Saturday?", ["Yes", "No"]);

		// Pinned by the same endpoint as any other post.
		using (HttpResponseMessage pinned = await organiser.PostAsJsonAsync(
			$"{CommentsUrl}/{poll.Id}/pin",
			new PinCommentRequest(true)))
		{
			pinned.StatusCode.ShouldBe(HttpStatusCode.OK, await pinned.Content.ReadAsStringAsync());
		}

		// Reacted to by the same endpoint as any other post.
		ReactionCounts counts = await ReactAsync(organiser, poll.Id, "thanks");

		counts.Counts["thanks"].ShouldBe(1);

		CommentPage page = await ThreadAsync(organiser, ride.Id);

		CommentDto pinnedPoll = page.Pinned.Single();

		pinnedPoll.Id.ShouldBe(poll.Id);
		pinnedPoll.Kind.ShouldBe(CommentKindDto.Poll);
		pinnedPoll.IsPinned.ShouldBeTrue();
		pinnedPoll.Reactions!.Counts["thanks"].ShouldBe(1);

		// It carries its poll everywhere a comment goes, so a client renders the thread with one
		// component rather than two.
		pinnedPoll.Poll!.Options.Count.ShouldBe(2);
		pinnedPoll.Poll.Question.ShouldBe("Who's coming Saturday?");

		// Deleted by the same endpoint, and the poll goes with it.
		using (HttpResponseMessage removed = await organiser.DeleteAsync($"{CommentsUrl}/{poll.Id}"))
		{
			removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		}

		int polls = await app.WithDatabaseAsync(database => database.Set<Poll>().CountAsync());
		int options = await app.WithDatabaseAsync(database => database.Set<PollOption>().CountAsync());

		polls.ShouldBe(0, "the poll cascaded with its comment");
		options.ShouldBe(0);
	}

	/// <summary>
	/// Deferred out of SRV-28, which had the switch but no reactions or votes to try it against.
	/// Neither is ever gated by a content switch: a reaction carries no free text and no storage
	/// worth moderating, and switching off the ability to answer a poll would break the poll
	/// rather than moderate it (§5.8, §17.7).
	/// </summary>
	[Fact]
	public async Task Permissions_CommentsOff_MemberMayStillReactAndVote()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient member = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, member, ride.Id);

		CommentDto post = await PostAsync(organiser, ride.Id, "Fuel at the servo in 8 km");
		CommentDto poll = await PostPollAsync(organiser, ride.Id, "Who's coming?", ["Yes", "No"]);

		using (HttpResponseMessage set = await organiser.PutAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/permissions",
			new RidePermissions(AllowMemberComments: false)))
		{
			set.StatusCode.ShouldBe(HttpStatusCode.OK, await set.Content.ReadAsStringAsync());
		}

		// Posting is off.
		using (HttpResponseMessage refused = await member.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/comments",
			new PostCommentRequest(Guid.NewGuid(), "Can I say something?")))
		{
			refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		}

		// Reading, reacting and voting are not.
		CommentPage page = await ThreadAsync(member, ride.Id);

		page.Comments.Count.ShouldBe(2);

		ReactionCounts counts = await ReactAsync(member, post.Id, "thanks");

		counts.Counts["thanks"].ShouldBe(1);

		PollResults results = await VoteAsync(member, poll.Id, [poll.Poll!.Options[0].Id]);

		results.Options[0].Votes.ShouldBe(1);
		results.Options[0].Voters.Single().UserName.ShouldBe("SamJones");
	}

	/// <summary>
	/// An unknown key is stored rather than rejected, so a newer client's reaction survives a round
	/// trip through this server and renders generically on an older one (§17.4, §16.2).
	/// </summary>
	[Fact]
	public async Task Reaction_UnknownKey_IsStoredAndRoundTrips()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		CommentDto post = await PostAsync(organiser, ride.Id, "Look at this corner");

		ReactionCounts counts = await ReactAsync(organiser, post.Id, "applause");

		counts.Counts["applause"].ShouldBe(1);
		counts.Mine.ShouldBe("applause");

		// But something that could never be a key is still refused, so a free-text reaction cannot
		// arrive through the back door and become a second unmoderated UGC field.
		using HttpResponseMessage refused = await organiser.PutAsJsonAsync(
			$"{CommentsUrl}/{post.Id}/reaction",
			new SetReactionRequest("Nice one, mate! 🎉"));

		refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}

	private static async Task<ReactionCounts> ReactAsync(HttpClient client, Guid commentId, string? reaction)
	{
		using HttpResponseMessage response = await client.PutAsJsonAsync(
			$"{CommentsUrl}/{commentId}/reaction",
			new SetReactionRequest(reaction));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<ReactionCounts>())!;
	}

	private static async Task<PollResults> VoteAsync(
		HttpClient client,
		Guid commentId,
		IReadOnlyList<Guid> optionIds)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			$"{CommentsUrl}/{commentId}/votes",
			new CastVoteRequest(optionIds));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<PollResults>())!;
	}

	/// <summary>
	/// Reads a poll back through the thread, which is where a client actually sees it — and which
	/// therefore also asserts that a poll travels with its comment rather than needing a fetch of
	/// its own (§17.5).
	/// </summary>
	private static async Task<PollResults> ResultsAsync(HttpClient client, Guid rideId, Guid commentId)
	{
		CommentPage page = await ThreadAsync(client, rideId);

		CommentDto comment = page.Comments.Concat(page.Pinned).First(row => row.Id == commentId);

		return comment.Poll.ShouldNotBeNull();
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
		IReadOnlyList<string> options,
		bool allowMultiple = false,
		DateTimeOffset? closesUtc = null)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			$"{RidesUrl}/{rideId}/comments",
			new PostCommentRequest(
				Guid.NewGuid(),
				question,
				Poll: new PollSpec(options, allowMultiple, closesUtc)));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<CommentDto>())!;
	}

	private static async Task<CommentPage> ThreadAsync(HttpClient client, Guid rideId)
	{
		using HttpResponseMessage response = await client.GetAsync($"{RidesUrl}/{rideId}/comments");

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<CommentPage>())!;
	}

	/// <summary>Polls until a condition holds, so a hub assertion is not a fixed sleep.</summary>
	private static async Task WaitForAsync(Func<bool> condition)
	{
		for (int attempt = 0; attempt < 100 && !condition(); attempt++)
		{
			await Task.Delay(20);
		}

		condition().ShouldBeTrue("the hub message never arrived");
	}

	private static async Task ReauthenticateAsync(
		DlrWebApplicationFactory app,
		HttpClient client,
		string userName)
	{
		using HttpClient anonymous = app.CreateClient();

		client.Authenticated(await anonymous.SignInAsync(userName));
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
