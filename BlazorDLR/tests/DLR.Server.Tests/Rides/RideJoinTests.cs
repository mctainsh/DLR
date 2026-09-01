using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
using DLR.Core.Rides;
using DLR.Server.Data.Rides;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Rides;

/// <summary>
/// The organiser always decides (§5.2).
/// <para>
/// This is the whole abuse story for group rides, and a stronger one than the confirmed-email
/// gate it replaced: a rider reaches another person's live location <em>only</em> because the
/// organiser handed out a code or pressed Admit. Email verification only ever proved somebody
/// could read a mailbox.
/// </para>
/// </summary>
public sealed class RideJoinTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";

	[Fact]
	public async Task JoinByCode_ApprovalRide_CreatesPendingRequestOnly()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Approval);

		JoinResult result = await JoinAsync(rider, ride.JoinCode!);

		result.Joined.ShouldBeFalse("nobody enters an approval adventure until the organiser says so");
		result.RequestId.ShouldNotBeNull();

		// Not a member - which is the assertion that matters, because a pending request that
		// also let somebody in would defeat the entire access model.
		int members = await app.WithDatabaseAsync(database =>
			database.Set<GroupRideMember>().CountAsync(m => m.GroupRideId == ride.Id));

		members.ShouldBe(1, "only the organiser");

		// And they cannot read the ride yet.
		using HttpResponseMessage detail = await rider.GetAsync($"{RidesUrl}/{ride.Id}");

		detail.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task JoinByCode_OpenRide_JoinsImmediately()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Open);

		JoinResult result = await JoinAsync(rider, ride.JoinCode!);

		result.Joined.ShouldBeTrue("the organiser chose who to give the code to");
		result.RequestId.ShouldBeNull();

		RideDetail asRider = (await rider.GetFromJsonAsync<RideDetail>($"{RidesUrl}/{ride.Id}"))!;

		asRider.MemberCount.ShouldBe(2);
		asRider.IsOrganiser.ShouldBeFalse();

		asRider.JoinCode.ShouldBe(ride.JoinCode,
			"a rider who joined can read the code back off their own copy and pass it on");
	}

	/// <summary>
	/// Every member receives the code (§5.2), not only the organiser - a rider who is already in
	/// wants to tell a friend how to follow along, and the two ways into a ride have to agree.
	/// <para>
	/// Both membership paths are checked because they reach membership differently: joining an
	/// open ride with the code, and being admitted to an approval ride by the organiser. On the
	/// approval ride the code still does not admit anybody by itself - the organiser decides.
	/// </para>
	/// </summary>
	[Fact]
	public async Task JoinCode_IsSentToEveryMember()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient byCode = await SignedInAsync(app, "SamJones");
		using HttpClient byApproval = await SignedInAsync(app, "PatBrown");

		RideDetail open = await CreateRideAsync(organiser, JoinPolicyDto.Open);
		RideDetail gated = await CreateRideAsync(organiser, JoinPolicyDto.Approval);

		await JoinAsync(byCode, open.JoinCode!);

		// The admitted-by-approval path reaches membership a different way, and has to arrive at
		// the same answer.
		JoinResult asked = await JoinAsync(byApproval, gated.JoinCode!);

		await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{gated.Id}/join-requests/{asked.RequestId}",
			new DecideJoinRequest(Admit: true));

		await ShouldCarryTheCodeAsync(byCode, open, "joined with the code");
		await ShouldCarryTheCodeAsync(byApproval, gated, "was admitted by the organiser");

		// And the organiser still gets it, or there is no way to invite anybody.
		RideDetail mine = (await organiser.GetFromJsonAsync<RideDetail>($"{RidesUrl}/{open.Id}"))!;

		mine.JoinCode.ShouldBe(open.JoinCode);
	}

	private static async Task ShouldCarryTheCodeAsync(HttpClient member, RideDetail ride, string how)
	{
		RideDetail asMember = (await member.GetFromJsonAsync<RideDetail>($"{RidesUrl}/{ride.Id}"))!;

		asMember.IsOrganiser.ShouldBeFalse($"the member who {how} does not run the adventure");

		asMember.JoinCode.ShouldBe(ride.JoinCode,
			$"a member who {how} sees the same code the organiser handed out");
	}

	/// <summary>
	/// Case, spaces and hyphens are how people write a code down; <c>I</c>, <c>L</c> and
	/// <c>O</c> are the characters Crockford leaves out precisely because they are misread
	/// (§5.2).
	/// </summary>
	[Theory]
	[InlineData("lower")]
	[InlineData("spaced")]
	[InlineData("hyphenated")]
	public async Task JoinByCode_ForgivesHowPeopleTypeIt(string style)
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Open);

		string code = ride.JoinCode!;

		string typed = style switch
		{
			"lower" => code.ToLowerInvariant(),
			"spaced" => $" {code[..3]} {code[3..]} ",
			_ => $"{code[..3]}-{code[3..]}",
		};

		(await JoinAsync(rider, typed)).Joined.ShouldBeTrue();
	}

	[Fact]
	public async Task JoinRequest_Approved_AddsMemberAndNotifiesRider()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith", "dave@example.com");
		using HttpClient rider = await SignedInAsync(app, "SamJones", "sam@example.com");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Approval);

		JoinResult request = await JoinAsync(rider, ride.JoinCode!);

		// The organiser hears about it.
		app.Emails.To("dave@example.com").ShouldContain(
			message => message.Subject.Contains("wants to join", StringComparison.Ordinal));

		List<JoinRequestSummary> pending =
			(await organiser.GetFromJsonAsync<List<JoinRequestSummary>>(
				$"{RidesUrl}/{ride.Id}/join-requests"))!;

		pending.ShouldHaveSingleItem().UserName.ShouldBe("SamJones");

		app.Emails.Clear();

		using HttpResponseMessage decided = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/join-requests/{request.RequestId}",
			new DecideJoinRequest(Admit: true));

		decided.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		RideDetail asRider = (await rider.GetFromJsonAsync<RideDetail>($"{RidesUrl}/{ride.Id}"))!;

		asRider.MemberCount.ShouldBe(2);
		asRider.Members.ShouldContain(member => member.UserName == "SamJones");

		app.Emails.To("sam@example.com").ShouldHaveSingleItem()
			.Subject.ShouldContain("You are in");
	}

	/// <summary>
	/// The organiser's answer to somebody who will not take a no (§5.2). And the refusal is the
	/// same one an unknown code gets - telling them they are blocked hands them the one fact
	/// the organiser was trying not to have a conversation about.
	/// </summary>
	[Fact]
	public async Task JoinRequest_Declined_WithBlock_CannotRequestAgain()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Approval);

		JoinResult request = await JoinAsync(rider, ride.JoinCode!);

		using HttpResponseMessage declined = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/join-requests/{request.RequestId}",
			new DecideJoinRequest(Admit: false, Block: true));

		declined.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		using HttpResponseMessage again = await rider.PostAsJsonAsync(
			$"{RidesUrl}/join",
			new JoinByCodeRequest(ride.JoinCode!));

		again.StatusCode.ShouldBe(
			HttpStatusCode.NotFound,
			"indistinguishable from a code that does not exist");

		int requests = await app.WithDatabaseAsync(database =>
			database.Set<GroupRideJoinRequest>().CountAsync(row => row.GroupRideId == ride.Id));

		requests.ShouldBe(1, "the blocked traveller did not get to leave a second request behind");
	}

	[Fact]
	public async Task JoinRequest_DeclinedWithoutBlock_MayAskAgain()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Approval);

		JoinResult first = await JoinAsync(rider, ride.JoinCode!);

		await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/join-requests/{first.RequestId}",
			new DecideJoinRequest(Admit: false));

		JoinResult second = await JoinAsync(rider, ride.JoinCode!);

		second.RequestId.ShouldNotBe(first.RequestId, "a fresh ask, not the old one revived");

		// The partial unique index permits both because only one is Pending - and the decided
		// row stays, because a declined history is what a block is made of.
		int requests = await app.WithDatabaseAsync(database =>
			database.Set<GroupRideJoinRequest>().CountAsync(row => row.GroupRideId == ride.Id));

		requests.ShouldBe(2);
	}

	/// <summary>
	/// Without this, "request to join" is an invitation to pester every ride in the system
	/// (§5.2).
	/// </summary>
	[Fact]
	public async Task JoinRequest_SixthPending_IsRejected()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		List<string> codes = [];

		for (int index = 0; index < 6; index++)
		{
			codes.Add((await CreateRideAsync(organiser, JoinPolicyDto.Approval)).JoinCode!);
		}

		for (int index = 0; index < 5; index++)
		{
			(await JoinAsync(rider, codes[index])).RequestId.ShouldNotBeNull($"request {index + 1}");
		}

		using HttpResponseMessage sixth = await rider.PostAsJsonAsync(
			$"{RidesUrl}/join",
			new JoinByCodeRequest(codes[5]));

		sixth.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

		(await sixth.Content.ReadAsStringAsync()).ShouldContain("waiting");
	}

	[Fact]
	public async Task JoinByCode_RideAtMemberCap_IsRefused()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Open, memberCap: 2);

		using HttpClient first = await SignedInAsync(app, "RiderOne");

		(await JoinAsync(first, ride.JoinCode!)).Joined.ShouldBeTrue();

		using HttpClient second = await SignedInAsync(app, "RiderTwo");

		using HttpResponseMessage full = await second.PostAsJsonAsync(
			$"{RidesUrl}/join",
			new JoinByCodeRequest(ride.JoinCode!));

		full.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await full.Content.ReadAsStringAsync()).ShouldContain("limit of 2");
	}

	/// <summary>
	/// The gap §14.5 found, and said not to ship this endpoint without (§5.2). Six Crockford
	/// characters is about 1.07 billion combinations - impractical to guess at human speed and
	/// entirely practical for a script.
	/// </summary>
	[Fact]
	public async Task JoinByCode_RepeatedWrongCodes_AreRateLimited()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: new Dictionary<string, string?>
			{
				["Rides:FailedCodeAttemptsPerMinutePerAddress"] = "5",
				["Rides:FailedCodeAttemptsPerHourPerUser"] = "1000",
			});

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient guesser = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Open);

		for (int attempt = 1; attempt <= 5; attempt++)
		{
			using HttpResponseMessage miss = await guesser.PostAsJsonAsync(
				$"{RidesUrl}/join",
				new JoinByCodeRequest(JoinCode.Generate()));

			miss.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"guess {attempt} is inside the limit");
		}

		using HttpResponseMessage limited = await guesser.PostAsJsonAsync(
			$"{RidesUrl}/join",
			new JoinByCodeRequest(JoinCode.Generate()));

		limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
	}

	/// <summary>
	/// Failures are counted, not attempts. A rider joining ten rides in a minute is using the
	/// product; a client trying ten codes is enumerating them.
	/// </summary>
	[Fact]
	public async Task JoinByCode_SuccessfulJoins_AreNotCountedAgainstTheLimit()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: new Dictionary<string, string?>
			{
				["Rides:FailedCodeAttemptsPerMinutePerAddress"] = "2",
			});

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		for (int index = 0; index < 5; index++)
		{
			RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Open);

			(await JoinAsync(rider, ride.JoinCode!)).Joined.ShouldBeTrue($"join {index + 1}");
		}
	}

	/// <summary>
	/// §7.15's `Restricted_UnconfirmedLadderAccount_CanRecordButNotJoinRide`, now that there is
	/// a ride to be refused from. The ladder shuts the social surface and leaves recording open.
	/// </summary>
	[Fact]
	public async Task Restricted_UnconfirmedLadderAccount_CanRecordButNotJoinRide()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Open);

		using HttpClient signup = app.CreateClient().From("203.0.113.240");

		for (int account = 1; account <= 3; account++)
		{
			await signup.RegisterAsync($"Traveller{account}");
		}

		TokenResponse restricted = await signup.RegisterAsync("Rider4", email: "rider4@example.com");

		using HttpClient client = app.CreateClient().Authenticated(restricted);

		using HttpResponseMessage refused = await client.PostAsJsonAsync(
			$"{RidesUrl}/join",
			new JoinByCodeRequest(ride.JoinCode!));

		refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		using HttpResponseMessage creating = await client.PostAsJsonAsync(
			RidesUrl,
			new CreateRideRequest("My own adventure", DlrWebApplicationFactory.DefaultStart));

		creating.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
	}

	/// <summary>§7.15's `Restricted_AfterConfirming_CanJoinRide`. One click lifts it.</summary>
	[Fact]
	public async Task Restricted_AfterConfirming_CanJoinRide()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Open);

		using HttpClient signup = app.CreateClient().From("203.0.113.241");

		for (int account = 1; account <= 3; account++)
		{
			await signup.RegisterAsync($"Traveller{account}");
		}

		TokenResponse restricted = await signup.RegisterAsync("Rider4", email: "rider4@example.com");

		string token = TokenFromLink(app.Emails
			.To("rider4@example.com")
			.Single(message => message.Subject.Contains("Confirm", StringComparison.Ordinal))
			.PlainTextBody);

		using HttpResponseMessage confirmed = await signup.PostAsJsonAsync(
			"/api/v1/auth/confirm-email",
			new ConfirmEmailRequest(restricted.User.Id, token));

		TokenResponse now = (await confirmed.Content.ReadFromJsonAsync<TokenResponse>())!;

		using HttpClient client = app.CreateClient().Authenticated(now);

		(await JoinAsync(client, ride.JoinCode!)).Joined.ShouldBeTrue();
	}

	[Fact]
	public async Task JoinRequests_AreVisibleOnlyToTheOrganiser()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient member = await SignedInAsync(app, "SamJones");
		using HttpClient outsider = await SignedInAsync(app, "NosyNed");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Open);

		await JoinAsync(member, ride.JoinCode!);

		(await member.GetAsync($"{RidesUrl}/{ride.Id}/join-requests")).StatusCode
			.ShouldBe(HttpStatusCode.NotFound, "an ordinary member does not decide who is in");

		(await outsider.GetAsync($"{RidesUrl}/{ride.Id}/join-requests")).StatusCode
			.ShouldBe(HttpStatusCode.NotFound);
	}

	/// <summary>
	/// A member's marker colour reaches the ride's other members (§16.3).
	/// <para>
	/// It rides on the member row rather than on the position batch, which is sent to everybody
	/// once per tick: a colour changes about as often as a username does, and repeating it five
	/// times a second would be bytes spent on something that does not move. That makes this
	/// projection the only path by which the live map learns how to paint anybody.
	/// </para>
	/// </summary>
	[Fact]
	public async Task RideDetail_CarriesEachMembersMarkerColour()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		using HttpResponseMessage chosen = await rider.PutAsJsonAsync(
			"/api/v1/me/profile",
			new UpdateProfileRequest(MarkerColour: "#16a34a"));

		chosen.StatusCode.ShouldBe(HttpStatusCode.OK, await chosen.Content.ReadAsStringAsync());

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Open);

		await JoinAsync(rider, ride.JoinCode!);

		RideDetail seen = (await organiser.GetFromJsonAsync<RideDetail>($"{RidesUrl}/{ride.Id}"))!;

		seen.Members.Single(member => member.UserName == "SamJones").MarkerColour.ShouldBe("#16a34a",
			"the map paints somebody else's marker, so somebody else's client has to be told the colour.");

		seen.Members.Single(member => member.UserName == "DaveSmith").MarkerColour.ShouldBeNull(
			"a member who never chose sends nothing - the default is applied where it is drawn.");
	}

	/// <summary>
	/// A rider waiting on an approval can see that they are waiting, and on what (§5.2).
	/// <para>
	/// They are in neither of the other two lists, because both are built from membership rows and
	/// a pending requester has none - which is the fact every other ride screen leans on. Without a
	/// third list, asking to join an adventure produced no visible trace anywhere at all.
	/// </para>
	/// </summary>
	[Fact]
	public async Task MyRides_ListsWhatTheCallerIsStillWaitingOn()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Approval);

		JoinResult asked = await JoinAsync(rider, ride.JoinCode!);

		MyRides mine = (await rider.GetFromJsonAsync<MyRides>(RidesUrl))!;

		mine.Organised.ShouldBeEmpty();
		mine.Joined.ShouldBeEmpty("asking is not joining - nobody has admitted them");

		WaitingRide waiting = mine.Waiting.ShouldHaveSingleItem();

		waiting.RideId.ShouldBe(ride.Id);
		waiting.RequestId.ShouldBe(asked.RequestId!.Value);
		waiting.Name.ShouldBe(ride.Name, "a list that cannot name what it is waiting on says nothing");
	}

	/// <summary>
	/// <strong>The join code is a member's.</strong> It is the credential that gets a third person
	/// into the adventure, and somebody the organiser has not admitted must not be handed one on a
	/// list - which is why the waiting rows are their own contract with no field to put it in.
	/// </summary>
	[Fact]
	public async Task MyRides_WaitingRows_CarryNothingThatBelongsToAMember()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Approval);

		await JoinAsync(rider, ride.JoinCode!);

		using HttpResponseMessage response = await rider.GetAsync(RidesUrl);

		response.StatusCode.ShouldBe(HttpStatusCode.OK);

		// Asserted on the JSON rather than the deserialised object, because the point is what left
		// the server. A field that WaitingRide does not have cannot be read back off a typed model.
		string body = await response.Content.ReadAsStringAsync();

		body.ShouldNotContain(
			ride.JoinCode!,
			Case.Insensitive,
			"the code gets somebody else in; a rider nobody has admitted has not earned it");
	}

	/// <summary>An answered request stops being a wait, whichever way it was answered.</summary>
	[Fact]
	public async Task MyRides_OnceAdmitted_TheRideMovesFromWaitingToJoined()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Approval);

		JoinResult asked = await JoinAsync(rider, ride.JoinCode!);

		using HttpResponseMessage decided = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/join-requests/{asked.RequestId}",
			new DecideJoinRequest(Admit: true, Block: false));

		decided.StatusCode.ShouldBe(HttpStatusCode.NoContent, await decided.Content.ReadAsStringAsync());

		MyRides mine = (await rider.GetFromJsonAsync<MyRides>(RidesUrl))!;

		mine.Waiting.ShouldBeEmpty("the request is answered - there is nothing left to wait for");
		mine.Joined.ShouldHaveSingleItem().Id.ShouldBe(ride.Id);
	}

	/// <summary>A decline is an answer too, and leaves the same empty list.</summary>
	[Fact]
	public async Task MyRides_OnceDeclined_TheRideIsInNeitherList()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Approval);

		JoinResult asked = await JoinAsync(rider, ride.JoinCode!);

		using HttpResponseMessage decided = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/join-requests/{asked.RequestId}",
			new DecideJoinRequest(Admit: false, Block: false));

		decided.StatusCode.ShouldBe(HttpStatusCode.NoContent, await decided.Content.ReadAsStringAsync());

		MyRides mine = (await rider.GetFromJsonAsync<MyRides>(RidesUrl))!;

		mine.Waiting.ShouldBeEmpty("a declined request is not a pending one, and the list is pending only");
		mine.Joined.ShouldBeEmpty();
	}

	// -- Withdrawing a request (§5.2) -----------------------------------------------------------

	[Fact]
	public async Task Withdraw_TakesTheRequestOffTheOrganisersList()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Approval);

		JoinResult asked = await JoinAsync(rider, ride.JoinCode!);

		using HttpResponseMessage withdrawn = await rider.DeleteAsync(
			$"{RidesUrl}/{ride.Id}/join-requests/{asked.RequestId}");

		withdrawn.StatusCode.ShouldBe(HttpStatusCode.NoContent, await withdrawn.Content.ReadAsStringAsync());

		IReadOnlyList<JoinRequestSummary> pending =
			(await organiser.GetFromJsonAsync<List<JoinRequestSummary>>($"{RidesUrl}/{ride.Id}/join-requests"))!;

		pending.ShouldBeEmpty("the organiser has nothing left to decide");

		MyRides mine = (await rider.GetFromJsonAsync<MyRides>(RidesUrl))!;

		mine.Waiting.ShouldBeEmpty();
		mine.Joined.ShouldBeEmpty("withdrawing is not a way in");
	}

	/// <summary>
	/// Somebody else's request is as invisible here as one that does not exist. A ride id travels in
	/// links (§5.2), so a distinguishable answer would make this an oracle for who is asking to join
	/// what.
	/// </summary>
	[Fact]
	public async Task Withdraw_CannotTakeBackSomebodyElsesRequest()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");
		using HttpClient meddler = await SignedInAsync(app, "NosyNed");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Approval);

		JoinResult asked = await JoinAsync(rider, ride.JoinCode!);

		using HttpResponseMessage attempt = await meddler.DeleteAsync(
			$"{RidesUrl}/{ride.Id}/join-requests/{asked.RequestId}");

		attempt.StatusCode.ShouldBe(HttpStatusCode.NoContent, "it answers alike either way");

		// The row is untouched, which is the assertion that matters - the status code is only the
		// half of it a caller can see.
		IReadOnlyList<JoinRequestSummary> pending =
			(await organiser.GetFromJsonAsync<List<JoinRequestSummary>>($"{RidesUrl}/{ride.Id}/join-requests"))!;

		pending.ShouldHaveSingleItem().Id.ShouldBe(asked.RequestId!.Value);
	}

	/// <summary>
	/// <strong>Withdrawing is not a way out of a block.</strong> A block lives on a Declined row and
	/// only a Pending one can be withdrawn, so the row that stops somebody asking again survives this
	/// call - and the ride goes on answering them the way it answers a stranger.
	/// </summary>
	[Fact]
	public async Task Withdraw_CannotClearABlock()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Approval);

		JoinResult asked = await JoinAsync(rider, ride.JoinCode!);

		using HttpResponseMessage blocked = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/join-requests/{asked.RequestId}",
			new DecideJoinRequest(Admit: false, Block: true));

		blocked.StatusCode.ShouldBe(HttpStatusCode.NoContent, await blocked.Content.ReadAsStringAsync());

		// The obvious attack: delete the row that carries the block.
		using HttpResponseMessage attempt = await rider.DeleteAsync(
			$"{RidesUrl}/{ride.Id}/join-requests/{asked.RequestId}");

		attempt.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		using HttpResponseMessage retry = await rider.PostAsJsonAsync(
			$"{RidesUrl}/join",
			new JoinByCodeRequest(ride.JoinCode!));

		retry.StatusCode.ShouldBe(
			HttpStatusCode.NotFound,
			"a blocked rider gets the answer an unknown code gets, before and after this call");
	}

	/// <summary>
	/// Idempotent by design: the caller asked for the request to be gone, and it is. A rider who taps
	/// Withdraw a half-second after the organiser taps Admit must not be shown a failure for a race
	/// they cannot see - their next load says which way it went.
	/// </summary>
	[Fact]
	public async Task Withdraw_AfterItWasAlreadyAnswered_SucceedsQuietly()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Approval);

		JoinResult asked = await JoinAsync(rider, ride.JoinCode!);

		using HttpResponseMessage admitted = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/join-requests/{asked.RequestId}",
			new DecideJoinRequest(Admit: true, Block: false));

		admitted.StatusCode.ShouldBe(HttpStatusCode.NoContent, await admitted.Content.ReadAsStringAsync());

		using HttpResponseMessage late = await rider.DeleteAsync(
			$"{RidesUrl}/{ride.Id}/join-requests/{asked.RequestId}");

		late.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		// And the membership the organiser granted is untouched - a late withdrawal must not undo it.
		MyRides mine = (await rider.GetFromJsonAsync<MyRides>(RidesUrl))!;

		mine.Joined.ShouldHaveSingleItem().Id.ShouldBe(ride.Id);
	}

	/// <summary>Withdrawing frees the pending slot, which is the point of deleting the row.</summary>
	[Fact]
	public async Task Withdraw_LetsThemAskTheSameRideAgain()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser, JoinPolicyDto.Approval);

		JoinResult first = await JoinAsync(rider, ride.JoinCode!);

		using HttpResponseMessage withdrawn = await rider.DeleteAsync(
			$"{RidesUrl}/{ride.Id}/join-requests/{first.RequestId}");

		withdrawn.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		JoinResult second = await JoinAsync(rider, ride.JoinCode!);

		second.RequestId.ShouldNotBe(first.RequestId, "a new ask is a new request, at the back of the list");

		MyRides mine = (await rider.GetFromJsonAsync<MyRides>(RidesUrl))!;

		mine.Waiting.ShouldHaveSingleItem().RequestId.ShouldBe(second.RequestId!.Value);
	}

	private static async Task<RideDetail> CreateRideAsync(
		HttpClient organiser,
		JoinPolicyDto policy,
		int? memberCap = null)
	{
		using HttpResponseMessage response = await organiser.PostAsJsonAsync(
			RidesUrl,
			new CreateRideRequest(
				"Saturday hills",
				DlrWebApplicationFactory.DefaultStart.AddDays(3),
				"Meet at the bakery",
				policy,
				memberCap));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		RideDetail ride = (await response.Content.ReadFromJsonAsync<RideDetail>())!;

		ride.JoinCode.ShouldNotBeNull("the organiser gets the code; nobody else does");
		ride.JoinCode!.Length.ShouldBe(JoinCode.Length);

		ride.JoinCode.ShouldAllBe(character => JoinCode.Alphabet.Contains(character),
			"a code with an I, L, O or U in it is one somebody will mistype across a car park");

		return ride;
	}

	private static async Task<JoinResult> JoinAsync(HttpClient client, string code)
	{
		using HttpResponseMessage response =
			await client.PostAsJsonAsync($"{RidesUrl}/join", new JoinByCodeRequest(code));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<JoinResult>())!;
	}

	private static async Task<HttpClient> SignedInAsync(
		DlrWebApplicationFactory app,
		string userName,
		string? email = null)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName, email: email);

		return app.CreateClient().Authenticated(session);
	}

	private static string TokenFromLink(string body)
	{
		int start = body.IndexOf("token=", StringComparison.Ordinal) + "token=".Length;
		int end = body.IndexOfAny([' ', '\n', '\r'], start);

		return Uri.UnescapeDataString(body[start..(end < 0 ? body.Length : end)]);
	}
}
