using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Server.Data.Identity;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Identity;

/// <summary>
/// The three optional fields, their three switches, and the rules that keep the switches
/// meaning what they say (§7.3).
/// </summary>
public sealed class ProfileTests(PostgresFixture postgres)
{
	private const string ProfileUrl = "/api/v1/me/profile";

	[Fact]
	public async Task Profile_FreshAccount_AllThreeSharingSwitchesAreOff()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		OwnProfile profile = (await authed.GetFromJsonAsync<OwnProfile>(ProfileUrl))!;

		profile.ShareDisplayName.ShouldBeFalse();
		profile.SharePhoneNumber.ShouldBeFalse();
		profile.ShareEmail.ShouldBeFalse();

		profile.DisplayName.ShouldBeNull();
		profile.PhoneNumber.ShouldBeNull();

		// And in the database, not just in the projection — a default that only exists in a
		// DTO is a default a stray INSERT walks straight past.
		AppUser stored = await app.WithDatabaseAsync(async database =>
			await database.Users.SingleAsync(user => user.Id == session.User.Id));

		stored.ShareDisplayName.ShouldBeFalse();
		stored.SharePhoneNumber.ShouldBeFalse();
		stored.ShareEmail.ShouldBeFalse();
	}

	/// <summary>
	/// Recording and sharing are separate decisions (§7.3). Turning a switch off hides a value;
	/// it does not throw it away, and the rider did not ask for it to be thrown away.
	/// </summary>
	[Fact]
	public async Task Profile_TurningSharingOff_DoesNotDeleteTheValue()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		await UpdateAsync(authed, new UpdateProfileRequest(
			DisplayName: "Dave",
			PhoneNumber: "+61400000000",
			ShareDisplayName: true,
			SharePhoneNumber: true));

		OwnProfile off = await UpdateAsync(authed, new UpdateProfileRequest(
			DisplayName: "Dave",
			PhoneNumber: "+61400000000"));

		off.ShareDisplayName.ShouldBeFalse();
		off.SharePhoneNumber.ShouldBeFalse();

		off.DisplayName.ShouldBe("Dave");
		off.PhoneNumber.ShouldBe("+61400000000");

		SharedProfile shared = SharedProfile.For(
			await StoredAsync(app, session.User.Id),
			viewerSharesActiveRide: true);

		shared.ShouldBe(SharedProfile.Empty, "hidden from travellers, still on the account");
	}

	/// <summary>
	/// The one that matters most: an email hidden from other riders is still the address a
	/// password reset goes to (§7.7). Conflating the two would mean a privacy setting silently
	/// removing the only way back into an account.
	/// </summary>
	[Fact]
	public async Task Profile_TurningEmailSharingOff_LeavesRecoveryIntact()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith", email: "dave@example.com");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		await UpdateAsync(authed, new UpdateProfileRequest(ShareEmail: true));

		OwnProfile hidden = await UpdateAsync(authed, new UpdateProfileRequest(ShareEmail: false));

		hidden.ShareEmail.ShouldBeFalse();
		hidden.Email.ShouldBe("dave@example.com");

		AppUser stored = await StoredAsync(app, session.User.Id);

		stored.Email.ShouldBe("dave@example.com");
		stored.NormalizedEmail.ShouldBe("DAVE@EXAMPLE.COM",
			"the normalised copy is what a reset looks the account up by, and it has to survive too");
	}

	/// <summary>
	/// SMS verification needs a paid provider the €4 budget does not want, and an SMS reset
	/// path would add an account-takeover surface for no benefit. Identity's column is reused
	/// and this flag stays false forever — a future contributor who sees it will otherwise
	/// assume verification happened somewhere (§7.3).
	/// </summary>
	[Fact]
	public async Task Profile_PhoneNumberConfirmed_IsNeverSetTrue()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		await UpdateAsync(authed, new UpdateProfileRequest(
			PhoneNumber: "+61400000000",
			SharePhoneNumber: true));

		(await StoredAsync(app, session.User.Id)).PhoneNumberConfirmed.ShouldBeFalse();

		// Still false after clearing and re-setting it, which is the sequence most likely to
		// tempt somebody into "well, they typed it twice".
		await UpdateAsync(authed, new UpdateProfileRequest(PhoneNumber: null));
		await UpdateAsync(authed, new UpdateProfileRequest(PhoneNumber: "+61400000001"));

		(await StoredAsync(app, session.User.Id)).PhoneNumberConfirmed.ShouldBeFalse();
	}

	/// <summary>
	/// The map label does not change (§7.3). A shared display name appears in the member list
	/// beside the username, never instead of it — which is what stops a rider labelling
	/// themselves <c>RideLeader</c> on somebody else's map.
	/// </summary>
	[Fact]
	public async Task Profile_DisplayName_DoesNotBecomeTheUsername()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		await UpdateAsync(authed, new UpdateProfileRequest(
			DisplayName: "RideLeader",
			ShareDisplayName: true));

		AppUser stored = await StoredAsync(app, session.User.Id);

		stored.UserName.ShouldBe("DaveSmith");
		stored.DisplayName.ShouldBe("RideLeader");
	}

	// ---------- The map marker colour (§16.3) ----------

	/// <summary>
	/// Null rather than a stored default, so an account created before the column existed and one
	/// that never chose are the same row. The default lives in <c>MarkerColours.Or</c>, on the
	/// render path, which is the only place that can be wrong about it.
	/// </summary>
	[Fact]
	public async Task MarkerColour_FreshAccount_HasNoneStored()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		(await authed.GetFromJsonAsync<OwnProfile>(ProfileUrl))!.MarkerColour.ShouldBeNull();

		(await StoredAsync(app, session.User.Id)).MarkerColour.ShouldBeNull();
	}

	[Fact]
	public async Task MarkerColour_IsStoredLowerCased_AndReadBack()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		OwnProfile saved = await UpdateAsync(authed, new UpdateProfileRequest(MarkerColour: "#16A34A"));

		saved.MarkerColour.ShouldBe("#16a34a");
		(await StoredAsync(app, session.User.Id)).MarkerColour.ShouldBe("#16a34a",
			"one spelling in the database, so a swatch comparison never has to be case-insensitive.");
	}

	/// <summary>
	/// Blank is how a rider goes back to the default. Refusing it would make the setting one-way.
	/// </summary>
	[Fact]
	public async Task MarkerColour_ClearedByOmission_GoesBackToNone()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		await UpdateAsync(authed, new UpdateProfileRequest(MarkerColour: "#dc2626"));

		(await UpdateAsync(authed, new UpdateProfileRequest(MarkerColour: null))).MarkerColour.ShouldBeNull();
	}

	/// <summary>
	/// A colour that is not <c>#rrggbb</c> is a client bug. Defaulting it quietly would leave a
	/// rider retrying a setting that never had a chance of sticking.
	/// </summary>
	[Theory]
	[InlineData("chartreuse")]
	[InlineData("#fff")]
	[InlineData("16a34a")]
	public async Task MarkerColour_NotAColour_IsRefused(string colour)
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		using HttpResponseMessage response = await authed.PutAsJsonAsync(
			ProfileUrl,
			new UpdateProfileRequest(DisplayName: "Dave", MarkerColour: colour));

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		AppUser stored = await StoredAsync(app, session.User.Id);

		stored.MarkerColour.ShouldBeNull();
		stored.DisplayName.ShouldBeNull(
			"the colour is validated before anything is written — a rejected request changes nothing.");
	}

	[Fact]
	public async Task Profile_WithoutAToken_IsRejected()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage read = await client.GetAsync(ProfileUrl);

		read.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		using HttpResponseMessage written =
			await client.PutAsJsonAsync(ProfileUrl, new UpdateProfileRequest(DisplayName: "Dave"));

		written.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	/// <summary>
	/// There is deliberately no endpoint that resolves a username to a profile (§7.14), and
	/// this one only ever answers about the caller. Asserted because "my profile" and "a
	/// profile" are one careless route parameter apart.
	/// </summary>
	[Fact]
	public async Task Profile_AnswersOnlyAboutTheCaller()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse dave = await client.RegisterAsync("DaveSmith");
		TokenResponse sam = await client.RegisterAsync("SamJones");

		using HttpClient asDave = app.CreateClient().Authenticated(dave);
		using HttpClient asSam = app.CreateClient().Authenticated(sam);

		await UpdateAsync(asDave, new UpdateProfileRequest(DisplayName: "Dave", ShareDisplayName: true));

		OwnProfile samsView = (await asSam.GetFromJsonAsync<OwnProfile>(ProfileUrl))!;

		samsView.DisplayName.ShouldBeNull();
		samsView.ShareDisplayName.ShouldBeFalse();
	}

	private static async Task<OwnProfile> UpdateAsync(HttpClient client, UpdateProfileRequest request)
	{
		using HttpResponseMessage response = await client.PutAsJsonAsync(ProfileUrl, request);

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<OwnProfile>())!;
	}

	private static async Task<AppUser> StoredAsync(DlrWebApplicationFactory app, Guid userId) =>
		await app.WithDatabaseAsync(async database =>
			await database.Users.SingleAsync(user => user.Id == userId));
}
