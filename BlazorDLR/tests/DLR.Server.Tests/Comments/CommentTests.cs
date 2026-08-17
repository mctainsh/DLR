using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Photos;
using DLR.Core.Contracts.Rides;
using DLR.Server.Data.Comments;
using DLR.Server.Data.Rides;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using DLR.TestSupport.Photos;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Comments;

/// <summary>
/// The ride thread (§17).
/// <para>
/// The thread spans the whole ride, and most of its value is outside the live window — before
/// (what time, which route, who is actually coming) and after (photos, and argument about who was
/// slowest). During the ride, traffic should be near zero, because the people this reaches are
/// operating vehicles (§17.1).
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CommentTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";
	private const string CommentsUrl = "/api/v1/comments";

	/// <summary>
	/// The question a live thread cannot dodge: a rider writes at 10:04 in a valley with no signal
	/// and it uploads at 14:32 — where does it go?
	/// <para>
	/// At the point of receipt. Ordering on a client-supplied time makes the thread only as
	/// trustworthy as the least accurate clock in the group, and drops four-hour-old text into the
	/// middle of a conversation that has moved on (§17.3).
	/// </para>
	/// </summary>
	[Fact]
	public async Task Comment_PostedOffline_OrdersByServerReceiptNotAuthoredTime()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient member = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, member, ride.Id);

		DateTimeOffset valley = app.Clock.GetUtcNow();

		// 10:04 in the valley. Written first, and it will arrive last.
		app.Clock.Advance(TimeSpan.FromHours(4));

		// Four hours is past the fifteen-minute access token, so both riders sign in again —
		// which is exactly what their apps would have done on the way back into coverage.
		await ReauthenticateAsync(app, organiser, "DaveSmith");
		await ReauthenticateAsync(app, member, "SamJones");

		CommentDto live = await PostAsync(organiser, ride.Id, "Anyone else stopping at the servo?");

		app.Clock.Advance(TimeSpan.FromMinutes(8));

		CommentDto drained = await PostAsync(
			member,
			ride.Id,
			"Pulling over, back in five",
			authoredAt: valley);

		drained.CreatedUtc.ShouldBe(valley, "the traveller's intent is preserved");
		drained.PostedUtc.ShouldBeGreaterThan(live.PostedUtc, "but receipt is what happened");

		CommentPage page = await ThreadAsync(member, ride.Id);

		// Newest first by receipt, so the four-hour-old text sits at the top where it arrived —
		// not buried under the conversation it missed.
		page.Comments.Select(comment => comment.Id).ShouldBe([drained.Id, live.Id]);
	}

	[Fact]
	public async Task Comment_ClientClockInFuture_IsClampedToReceiptTime()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		DateTimeOffset receipt = app.Clock.GetUtcNow();

		CommentDto posted = await PostAsync(
			organiser,
			ride.Id,
			"My phone thinks it is next year",
			authoredAt: receipt.AddYears(1));

		// Clamped, not rejected. A wrong clock is a broken device, not a hostile rider, and
		// refusing the post would lose the text. What must not survive is the claim.
		posted.CreatedUtc.ShouldBe(receipt);
		posted.AuthoredEarlier.ShouldBeFalse();

		DateTimeOffset stored = await app.WithDatabaseAsync(database =>
			database.Set<RideComment>()
				.Where(comment => comment.Id == posted.Id)
				.Select(comment => comment.CreatedUtc)
				.SingleAsync());

		stored.ShouldBe(receipt, "the clamp is on the write, not on the way out");
	}

	/// <summary>
	/// The UI shows <em>"14:32 — written 10:04"</em>, and the threshold that decides when is
	/// configuration rather than a number three clients each pick for themselves (§17.3, §14.5).
	/// </summary>
	[Fact]
	public async Task Comment_StaleAuthoredTime_IsSurfacedAlongsidePostedTime()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		DateTimeOffset now = app.Clock.GetUtcNow();

		// Inside the ten-minute default: a post that took a moment to send is not a stale post,
		// and flagging it would put "written at" on almost everything.
		CommentDto prompt = await PostAsync(
			organiser,
			ride.Id,
			"Just now",
			authoredAt: now.AddMinutes(-9));

		prompt.AuthoredEarlier.ShouldBeFalse();

		CommentDto stale = await PostAsync(
			organiser,
			ride.Id,
			"Written in the valley",
			authoredAt: now.AddMinutes(-11));

		stale.AuthoredEarlier.ShouldBeTrue();

		// Both timestamps survive to the client, which is what lets it render the pair.
		stale.CreatedUtc.ShouldBe(now.AddMinutes(-11));
		stale.PostedUtc.ShouldBe(now);
	}

	/// <summary>
	/// A photograph with no text is legitimate — most post-ride posts are exactly that — so the
	/// rule is "at least one of the two", not "body required" (§17.2).
	/// </summary>
	[Fact]
	public async Task Comment_WithNeitherBodyNorPhoto_Returns400()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		foreach (string? empty in new[] { null, "", "   " })
		{
			using HttpResponseMessage refused = await organiser.PostAsJsonAsync(
				$"{RidesUrl}/{ride.Id}/comments",
				new PostCommentRequest(Guid.NewGuid(), empty));

			refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"body was '{empty ?? "null"}'");
		}

		// The photo-only post, which is the case the rule exists to permit.
		PhotoUploaded photo = await UploadPhotoAsync(organiser);

		using HttpResponseMessage accepted = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/comments",
			new PostCommentRequest(Guid.NewGuid(), Body: null, PhotoId: photo.PhotoId));

		accepted.StatusCode.ShouldBe(HttpStatusCode.Created, await accepted.Content.ReadAsStringAsync());

		// And the database says the same, so the endpoint's check and the CHECK agree.
		DbUpdateException failure = await Should.ThrowAsync<DbUpdateException>(() =>
			app.WithDatabaseAsync(async database =>
			{
				database.Add(new RideComment
				{
					Id = Guid.NewGuid(),
					GroupRideId = ride.Id,
					AuthorId = await IdOfAsync(app, "DaveSmith"),
					ClientGuid = Guid.NewGuid(),
					CreatedUtc = DlrWebApplicationFactory.DefaultStart,
					PostedUtc = DlrWebApplicationFactory.DefaultStart,
				});

				await database.SaveChangesAsync();
			}));

		failure.InnerException!.Message.ShouldContain(RideCommentConfiguration.HasContentConstraint);
	}

	[Fact]
	public async Task Comment_ByNonMember_Returns403()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient stranger = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);

		using HttpResponseMessage refused = await stranger.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/comments",
			new PostCommentRequest(Guid.NewGuid(), "Let me in"));

		refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		// And they cannot read it either. The thread is visible to admitted members and nobody
		// else — that is the whole abuse model §17.1 declines to give up (§5.2).
		using HttpResponseMessage unread = await stranger.GetAsync($"{RidesUrl}/{ride.Id}/comments");

		unread.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Comment_EditAfterWindow_Returns409()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		CommentDto posted = await PostAsync(organiser, ride.Id, "Meet at eight");

		app.Clock.Advance(TimeSpan.FromMinutes(14));

		using (HttpResponseMessage inside = await organiser.PatchAsJsonAsync(
			$"{CommentsUrl}/{posted.Id}",
			new EditCommentRequest("Meet at nine")))
		{
			inside.StatusCode.ShouldBe(HttpStatusCode.OK, await inside.Content.ReadAsStringAsync());

			CommentDto edited = (await inside.Content.ReadFromJsonAsync<CommentDto>())!;

			edited.Body.ShouldBe("Meet at nine");
			edited.EditedUtc.ShouldNotBeNull("an edit that left no trace lets somebody rewrite history");
		}

		app.Clock.Advance(TimeSpan.FromMinutes(2));

		// Past fifteen minutes from receipt — which is also exactly the access token's lifetime,
		// so this test cannot reach the far side of the window without signing in again. Any test
		// that moves the clock more than fifteen minutes needs this (§7.4).
		await ReauthenticateAsync(app, organiser, "DaveSmith");
		using HttpResponseMessage outside = await organiser.PatchAsJsonAsync(
			$"{CommentsUrl}/{posted.Id}",
			new EditCommentRequest("Meet at ten"));

		outside.StatusCode.ShouldBe(HttpStatusCode.Conflict);

		CommentPage page = await ThreadAsync(organiser, ride.Id);

		page.Comments.Single().Body.ShouldBe("Meet at nine", "the refused edit changed nothing");
	}

	[Fact]
	public async Task Comment_DeleteByOrganiser_Succeeds()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient member = await SignedInAsync(app, "SamJones");
		using HttpClient other = await SignedInAsync(app, "AlexLee");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, member, ride.Id);
		await JoinAsync(app, other, ride.Id);

		CommentDto theirs = await PostAsync(member, ride.Id, "Something the organiser will remove");

		// An ordinary member cannot delete somebody else's post — otherwise "the organiser can
		// moderate" would mean "anybody can".
		using (HttpResponseMessage refused = await other.DeleteAsync($"{CommentsUrl}/{theirs.Id}"))
		{
			refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		}

		using HttpResponseMessage removed = await organiser.DeleteAsync($"{CommentsUrl}/{theirs.Id}");

		removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		CommentPage page = await ThreadAsync(organiser, ride.Id);

		page.Comments.ShouldBeEmpty();
	}

	[Fact]
	public async Task Comment_ExceedingRideCap_Returns409()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: new Dictionary<string, string?>
			{
				["Comments:MaxPerRide"] = "3",
				["Comments:PostsPerHourPerUserPerRide"] = "100",
			});

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		for (int i = 0; i < 3; i++)
		{
			await PostAsync(organiser, ride.Id, $"Post {i}");
		}

		using HttpResponseMessage refused = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/comments",
			new PostCommentRequest(Guid.NewGuid(), "One too many"));

		refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);

		// Deleting one makes room again — the cap bounds what a thread holds, not how many posts
		// it has ever seen.
		CommentPage page = await ThreadAsync(organiser, ride.Id);

		using (HttpResponseMessage removed = await organiser.DeleteAsync(
			$"{CommentsUrl}/{page.Comments[0].Id}"))
		{
			removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		}

		await PostAsync(organiser, ride.Id, "Room again");
	}

	/// <summary>
	/// Pinned posts render above everything <em>regardless of age</em>, which is the property a
	/// single ordered page cannot express (§17.6).
	/// </summary>
	[Fact]
	public async Task Pin_ByOrganiser_MovesCommentToTopOfThread()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		CommentDto old = await PostAsync(organiser, ride.Id, "Fuel at the servo in 8 km");

		app.Clock.Advance(TimeSpan.FromMinutes(5));

		await PostAsync(organiser, ride.Id, "Chatter");

		app.Clock.Advance(TimeSpan.FromMinutes(5));

		await PostAsync(organiser, ride.Id, "More chatter");

		// Unpinned, the oldest post is at the bottom.
		CommentPage before = await ThreadAsync(organiser, ride.Id);

		before.Pinned.ShouldBeEmpty();
		before.Comments[^1].Id.ShouldBe(old.Id);

		await PinAsync(organiser, old.Id, pinned: true);

		CommentPage after = await ThreadAsync(organiser, ride.Id);

		after.Pinned.Single().Id.ShouldBe(old.Id);
		after.Pinned.Single().IsPinned.ShouldBeTrue();

		// Unpinning puts it back.
		await PinAsync(organiser, old.Id, pinned: false);

		(await ThreadAsync(organiser, ride.Id)).Pinned.ShouldBeEmpty();
	}

	/// <summary>
	/// Pinning is the deliberate act that says "this is worth a phone buzzing at 100 km/h"
	/// (§17.1), so it belongs to the people who run the ride.
	/// </summary>
	[Fact]
	public async Task Pin_ByOrdinaryMember_Returns403()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient member = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, member, ride.Id);

		// Their own post, so this is about the act rather than about ownership.
		CommentDto theirs = await PostAsync(member, ride.Id, "Pin me");

		using HttpResponseMessage refused = await member.PostAsJsonAsync(
			$"{CommentsUrl}/{theirs.Id}/pin",
			new PinCommentRequest(true));

		refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		bool pinned = await app.WithDatabaseAsync(database =>
			database.Set<RideComment>()
				.Where(comment => comment.Id == theirs.Id)
				.Select(comment => comment.IsPinned)
				.SingleAsync());

		pinned.ShouldBeFalse("the refusal did not pin it anyway");
	}

	[Fact]
	public async Task Pin_ExceedingMaxPinned_Returns409()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		List<CommentDto> posts = [];

		for (int i = 0; i < 4; i++)
		{
			posts.Add(await PostAsync(organiser, ride.Id, $"Notice {i}"));

			app.Clock.Advance(TimeSpan.FromMinutes(1));
		}

		// Three is the default, and it is a cap on *what is pinned now*, not on pinnings ever.
		for (int i = 0; i < 3; i++)
		{
			await PinAsync(organiser, posts[i].Id, pinned: true);
		}

		using (HttpResponseMessage refused = await organiser.PostAsJsonAsync(
			$"{CommentsUrl}/{posts[3].Id}/pin",
			new PinCommentRequest(true)))
		{
			refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		}

		(await ThreadAsync(organiser, ride.Id)).Pinned.Count.ShouldBe(3);

		// Unpin one and the fourth fits — otherwise the cap would be a permanent ceiling on a
		// noticeboard that is supposed to change through the ride.
		await PinAsync(organiser, posts[0].Id, pinned: false);
		await PinAsync(organiser, posts[3].Id, pinned: true);

		CommentPage page = await ThreadAsync(organiser, ride.Id);

		page.Pinned.Count.ShouldBe(3);
		page.Pinned.Select(comment => comment.Id).ShouldContain(posts[3].Id);

		// Re-pinning something already pinned is not a fourth pin.
		await PinAsync(organiser, posts[3].Id, pinned: true);

		(await ThreadAsync(organiser, ride.Id)).Pinned.Count.ShouldBe(3);
	}

	/// <summary>
	/// The §5.1 lifecycle already had this state and nothing to say about it; this is what it
	/// means (§17.6).
	/// </summary>
	[Fact]
	public async Task ArchivedRide_ThreadIsReadOnly()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		CommentDto existing = await PostAsync(organiser, ride.Id, "Before the archive");

		// Straight to the table: the thirty-day archival sweep is SRV-32's, and this test is about
		// what the state means rather than about how a ride reaches it.
		await SetStateAsync(app, ride.Id, GroupRideState.Archived);

		using (HttpResponseMessage posting = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/comments",
			new PostCommentRequest(Guid.NewGuid(), "After the archive")))
		{
			posting.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		}

		using (HttpResponseMessage editing = await organiser.PatchAsJsonAsync(
			$"{CommentsUrl}/{existing.Id}",
			new EditCommentRequest("Rewritten")))
		{
			editing.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		}

		using (HttpResponseMessage pinning = await organiser.PostAsJsonAsync(
			$"{CommentsUrl}/{existing.Id}/pin",
			new PinCommentRequest(true)))
		{
			pinning.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		}

		using (HttpResponseMessage deleting = await organiser.DeleteAsync($"{CommentsUrl}/{existing.Id}"))
		{
			deleting.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		}

		// Read-only, not gone. All four writes refused and the thread still reads.
		CommentPage page = await ThreadAsync(organiser, ride.Id);

		page.Comments.Single().Body.ShouldBe("Before the archive");
	}

	/// <summary>
	/// The authored-versus-measured line §16.1 draws, applied to the thread: positions are exhaust
	/// and go; posts are the record of what happened and stay (§17.6).
	/// </summary>
	[Fact]
	public async Task RideCompleted_DeletesPositionsButKeepsThread()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		CommentDto posted = await PostAsync(organiser, ride.Id, "Great adventure, photos to follow");

		using (HttpResponseMessage started = await organiser.PostAsync($"{RidesUrl}/{ride.Id}/start", null))
		{
			started.StatusCode.ShouldBe(HttpStatusCode.NoContent, await started.Content.ReadAsStringAsync());
		}

		using (HttpResponseMessage sharing = await organiser.PutAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/sharing/me",
			new SetSharingRequest(true)))
		{
			sharing.StatusCode.ShouldBe(HttpStatusCode.OK, await sharing.Content.ReadAsStringAsync());
		}

		using (HttpResponseMessage published = await organiser.PostAsJsonAsync(
			"/api/v1/positions",
			new PositionUpdate(
				PositionScale.FromDegrees(-33.86),
				PositionScale.FromDegrees(151.20),
				app.Clock.GetUtcNow())))
		{
			published.StatusCode.ShouldBe(HttpStatusCode.OK, await published.Content.ReadAsStringAsync());
		}

		await app.FlushPositionsAsync();

		(await PositionRowsAsync(app, ride.Id)).ShouldBe(1, "there is something to delete");

		using (HttpResponseMessage ended = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/ending",
			new EndRideRequest(RideEndingDto.Immediate)))
		{
			ended.StatusCode.ShouldBe(HttpStatusCode.NoContent, await ended.Content.ReadAsStringAsync());
		}

		(await PositionRowsAsync(app, ride.Id)).ShouldBe(0, "measured data goes with the adventure (§5.5)");

		// The thread is kept and stays open — the best photos land after everybody gets home.
		CommentPage page = await ThreadAsync(organiser, ride.Id);

		page.Comments.Single().Id.ShouldBe(posted.Id);

		await PostAsync(organiser, ride.Id, "Here they are");

		(await ThreadAsync(organiser, ride.Id)).Comments.Count.ShouldBe(2);
	}

	/// <summary>
	/// Deleting half a conversation makes the other half nonsense, so a member who leaves keeps
	/// their posts and loses the thread (§17.6).
	/// </summary>
	[Fact]
	public async Task MemberRemoved_KeepsPostsButRevokesAccess()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient member = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, member, ride.Id);

		CommentDto theirs = await PostAsync(member, ride.Id, "See you all Saturday");

		Guid memberId = await IdOfAsync(app, "SamJones");

		using (HttpResponseMessage removed = await organiser.DeleteAsync(
			$"{RidesUrl}/{ride.Id}/members/{memberId}"))
		{
			removed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await removed.Content.ReadAsStringAsync());
		}

		// The post is still there for everybody else, still attributed.
		CommentPage page = await ThreadAsync(organiser, ride.Id);

		page.Comments.Single().Id.ShouldBe(theirs.Id);
		page.Comments.Single().AuthorUserName.ShouldBe("SamJones");

		// And the author has lost the thread entirely — reading, posting and editing their own.
		using (HttpResponseMessage reading = await member.GetAsync($"{RidesUrl}/{ride.Id}/comments"))
		{
			reading.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		}

		using (HttpResponseMessage posting = await member.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/comments",
			new PostCommentRequest(Guid.NewGuid(), "Still here?")))
		{
			posting.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		}

		using HttpResponseMessage editing = await member.PatchAsJsonAsync(
			$"{CommentsUrl}/{theirs.Id}",
			new EditCommentRequest("Actually, never mind"));

		editing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	/// <summary>
	/// Deferred out of SRV-28, which had the switch but no thread to try it against (§5.8).
	/// Photos are their own switch precisely so that turning them off leaves conversation alone.
	/// </summary>
	[Fact]
	public async Task Permissions_PhotosOff_TextCommentStillSucceeds()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient member = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, member, ride.Id);

		using (HttpResponseMessage set = await organiser.PutAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/permissions",
			new RidePermissions(AllowMemberPhotos: false)))
		{
			set.StatusCode.ShouldBe(HttpStatusCode.OK, await set.Content.ReadAsStringAsync());
		}

		// Text still works — this is the assertion the switch exists to make true.
		CommentDto text = await PostAsync(member, ride.Id, "No pictures then");

		text.PhotoId.ShouldBeNull();

		// The same post with an image does not.
		PhotoUploaded photo = await UploadPhotoAsync(member);

		using HttpResponseMessage refused = await member.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/comments",
			new PostCommentRequest(Guid.NewGuid(), "Look at this", photo.PhotoId));

		refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		(await ThreadAsync(member, ride.Id)).Comments.Count.ShouldBe(1);
	}

	/// <summary>
	/// A phone draining its outbox re-sends what it never saw acknowledged; without this the
	/// thread grows a duplicate every time somebody rides through a tunnel (§17.3, §4.4).
	/// </summary>
	[Fact]
	public async Task Comment_SameClientGuidTwice_IsIdempotent()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		Guid clientGuid = Guid.NewGuid();

		CommentDto first = await PostAsync(organiser, ride.Id, "Sent from a tunnel", clientGuid: clientGuid);

		app.Clock.Advance(TimeSpan.FromMinutes(2));

		using HttpResponseMessage again = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/comments",
			new PostCommentRequest(clientGuid, "Sent from a tunnel"));

		again.StatusCode.ShouldBe(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());

		CommentDto repeat = (await again.Content.ReadFromJsonAsync<CommentDto>())!;

		repeat.Id.ShouldBe(first.Id);
		repeat.PostedUtc.ShouldBe(first.PostedUtc, "a re-send is not a new post and does not move it");

		(await ThreadAsync(organiser, ride.Id)).Comments.Count.ShouldBe(1);
	}

	/// <summary>
	/// A fresh access token on an existing client. An access token lives fifteen minutes, so any
	/// test that advances the clock past that and then calls an authed endpoint gets a 401 for a
	/// reason it is not investigating (§7.4).
	/// </summary>
	private static async Task ReauthenticateAsync(
		DlrWebApplicationFactory app,
		HttpClient client,
		string userName)
	{
		using HttpClient anonymous = app.CreateClient();

		client.Authenticated(await anonymous.SignInAsync(userName));
	}

	private static async Task<CommentDto> PostAsync(
		HttpClient client,
		Guid rideId,
		string body,
		DateTimeOffset? authoredAt = null,
		Guid? clientGuid = null)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			$"{RidesUrl}/{rideId}/comments",
			new PostCommentRequest(clientGuid ?? Guid.NewGuid(), body, PhotoId: null, authoredAt));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<CommentDto>())!;
	}

	private static async Task<CommentPage> ThreadAsync(HttpClient client, Guid rideId)
	{
		using HttpResponseMessage response = await client.GetAsync($"{RidesUrl}/{rideId}/comments");

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<CommentPage>())!;
	}

	private static async Task PinAsync(HttpClient client, Guid commentId, bool pinned)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			$"{CommentsUrl}/{commentId}/pin",
			new PinCommentRequest(pinned));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
	}

	private static async Task<PhotoUploaded> UploadPhotoAsync(HttpClient client)
	{
		using MultipartFormDataContent form = [];
		using ByteArrayContent file = new(ImageFixtures.Jpeg(240, 180));

		file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");

		form.Add(file, "file", "photo.jpg");

		using HttpResponseMessage response = await client.PostAsync("/api/v1/photos", form);

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<PhotoUploaded>())!;
	}

	private static async Task<int> PositionRowsAsync(DlrWebApplicationFactory app, Guid rideId) =>
		await app.WithDatabaseAsync(database =>
			database.Set<Data.Positions.RiderPosition>().CountAsync(row => row.GroupRideId == rideId));

	private static Task SetStateAsync(DlrWebApplicationFactory app, Guid rideId, GroupRideState state) =>
		app.WithDatabaseAsync(async database =>
		{
			GroupRide ride = await database.Set<GroupRide>().SingleAsync(row => row.Id == rideId);

			ride.State = state;

			await database.SaveChangesAsync();
		});

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
