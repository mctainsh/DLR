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
[Collection(DatabaseCollection.Name)]
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

		result.Joined.ShouldBeFalse("nobody enters an approval ride until the organiser says so");
		result.RequestId.ShouldNotBeNull();

		// Not a member — which is the assertion that matters, because a pending request that
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

		asRider.JoinCode.ShouldBeNull(
			"the code is the ride's whole access control — a member's copy carrying it would " +
			"let anybody re-share the group the organiser curated");
	}

	/// <summary>
	/// The code is the ride's entire access control (§5.2), so a member's copy carrying it would
	/// let anybody in the ride re-share the group the organiser curated.
	/// <para>
	/// Asserted against the raw body rather than the <c>JoinCode</c> property, because the rule is
	/// "a member never receives the code" — not "one particular field is null". A future field
	/// that echoes the code back would satisfy the second and break the first.
	/// </para>
	/// </summary>
	[Fact]
	public async Task JoinCode_IsNeverSentToAnybodyButTheOrganiser()
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

		await ShouldNotCarryTheCodeAsync(byCode, open, "joined with the code");
		await ShouldNotCarryTheCodeAsync(byApproval, gated, "admitted by the organiser");

		// And the organiser still gets it, or there is no way to invite anybody.
		RideDetail mine = (await organiser.GetFromJsonAsync<RideDetail>($"{RidesUrl}/{open.Id}"))!;

		mine.JoinCode.ShouldBe(open.JoinCode);
	}

	private static async Task ShouldNotCarryTheCodeAsync(HttpClient member, RideDetail ride, string how)
	{
		string body = await member.GetStringAsync($"{RidesUrl}/{ride.Id}");

		body.Contains(ride.JoinCode!, StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
			$"a member who {how} can read the ride, and must not be able to re-share it");
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
	/// same one an unknown code gets — telling them they are blocked hands them the one fact
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

		requests.ShouldBe(1, "the blocked rider did not get to leave a second request behind");
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

		// The partial unique index permits both because only one is Pending — and the decided
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
	/// characters is about 1.07 billion combinations — impractical to guess at human speed and
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
			await signup.RegisterAsync($"Rider{account}");
		}

		TokenResponse restricted = await signup.RegisterAsync("Rider4", email: "rider4@example.com");

		using HttpClient client = app.CreateClient().Authenticated(restricted);

		using HttpResponseMessage refused = await client.PostAsJsonAsync(
			$"{RidesUrl}/join",
			new JoinByCodeRequest(ride.JoinCode!));

		refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		using HttpResponseMessage creating = await client.PostAsJsonAsync(
			RidesUrl,
			new CreateRideRequest("My own ride", DlrWebApplicationFactory.DefaultStart));

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
			await signup.RegisterAsync($"Rider{account}");
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
			"a member who never chose sends nothing — the default is applied where it is drawn.");
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
