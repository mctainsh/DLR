using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Server.Data.Identity;
using DLR.Server.Identity;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Identity;

/// <summary>
/// Signed-in devices, revocation, and the activity record that rides on the refresh (§7.10).
/// <para>
/// Revocation matters more here than in most applications, because sessions are permanent
/// (§7.4): this is the <em>only</em> thing that ends one. Nothing expires quietly behind a
/// phone that was sold, lost, or handed on.
/// </para>
/// </summary>
public sealed class SessionTests(PostgresFixture postgres)
{
	private const string SessionsUrl = "/api/v1/auth/sessions";
	private const string TokenUrl = "/api/v1/auth/token";

	/// <summary>
	/// The point of the whole screen: after this, the phone in somebody else's hands cannot
	/// get another access token, whatever it still has in storage.
	/// </summary>
	[Fact]
	public async Task RevokeSession_TargetDeviceCannotRefresh()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse lost = await client.RegisterAsync("DaveSmith");

		// A second sign-in with no device id, so it lands on a device of its own.
		TokenResponse kept = await SignInAsync(client, "DaveSmith", "The phone in my pocket");

		Guid lostDevice = DeviceIdOf(lost);

		using HttpClient authed = app.CreateClient().Authenticated(kept);

		using HttpResponseMessage revoked = await authed.DeleteAsync($"{SessionsUrl}/{lostDevice}");

		revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		using HttpResponseMessage refused = await PostRefreshAsync(client, lost.RefreshToken);

		refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
			"a revoked device holding a valid refresh token is the exact situation this exists for");

		// And the session doing the revoking is untouched, which is the other half of it.
		using HttpResponseMessage stillGood = await PostRefreshAsync(client, kept.RefreshToken);

		stillGood.StatusCode.ShouldBe(HttpStatusCode.OK);

		IReadOnlyList<RefreshToken> onLostDevice = await app.WithDatabaseAsync(async database =>
			(IReadOnlyList<RefreshToken>)await database
				.Set<RefreshToken>()
				.Where(token => token.DeviceId == lostDevice)
				.ToListAsync());

		onLostDevice.ShouldAllBe(token => token.RevokedReason == RevocationReasons.SessionRevoked);
	}

	[Fact]
	public async Task Sessions_ListsThisAccountsDevicesAndMarksTheCurrentOne()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		await client.RegisterAsync("DaveSmith");

		TokenResponse current = await SignInAsync(client, "DaveSmith", "Pixel 9");

		using HttpClient authed = app.CreateClient().Authenticated(current);

		List<DeviceSession> sessions =
			(await authed.GetFromJsonAsync<List<DeviceSession>>(SessionsUrl))!;

		sessions.Count.ShouldBe(2);
		sessions.Count(session => session.IsCurrent).ShouldBe(1,
			"exactly one row is the device asking, or somebody signs themselves out by mistake");

		sessions.Single(session => session.IsCurrent).DeviceId.ShouldBe(DeviceIdOf(current));
		sessions.ShouldContain(session => session.Name == "Pixel 9");
	}

	[Fact]
	public async Task Sessions_WithoutAToken_IsRejected()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response = await client.GetAsync(SessionsUrl);

		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	/// <summary>
	/// A device id is not a capability. 404 rather than 403, because a distinguishable answer
	/// would turn this endpoint into a way to ask whether a given device exists.
	/// </summary>
	[Fact]
	public async Task RevokeSession_AnotherAccountsDevice_IsNotFoundAndChangesNothing()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse dave = await client.RegisterAsync("DaveSmith");
		TokenResponse sam = await client.RegisterAsync("SamJones");

		using HttpClient authed = app.CreateClient().Authenticated(sam);

		using HttpResponseMessage response =
			await authed.DeleteAsync($"{SessionsUrl}/{DeviceIdOf(dave)}");

		response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

		using HttpResponseMessage daveStillWorks = await PostRefreshAsync(client, dave.RefreshToken);

		daveStillWorks.StatusCode.ShouldBe(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Refresh_UpdatesLastActiveUtc()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith", email: null);

		DateTimeOffset atRegistration = await LastActiveAsync(app, session.User.Id);

		atRegistration.ShouldBe(DlrWebApplicationFactory.DefaultStart);

		app.Clock.Advance(TimeSpan.FromDays(3));

		await RefreshAsync(client, session.RefreshToken);

		DateTimeOffset afterRefresh = await LastActiveAsync(app, session.User.Id);

		afterRefresh.ShouldBe(DlrWebApplicationFactory.DefaultStart.AddDays(3));

		// The device row moves with it — the session list reads that one, not the account's.
		Device device = await app.WithDatabaseAsync(async database =>
			await database.Set<Device>().SingleAsync(row => row.Id == DeviceIdOf(session)));

		device.LastSeenUtc.ShouldBe(DlrWebApplicationFactory.DefaultStart.AddDays(3));
	}

	/// <summary>
	/// Opening the app five times in a morning is one <c>UPDATE</c>, not five (§7.10). An hour
	/// is far below the resolution anything reads this at: the inactivity sweep counts in days
	/// and the session list says "2 hours ago".
	/// </summary>
	[Fact]
	public async Task Refresh_WithinThrottleWindow_DoesNotRewriteLastActive()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		app.Clock.Advance(TimeSpan.FromDays(3));

		TokenResponse second = await RefreshAsync(client, session.RefreshToken);

		DateTimeOffset recorded = await LastActiveAsync(app, session.User.Id);

		// Well inside the hour, and past the grace window so this is a real rotation rather
		// than an idempotent replay.
		app.Clock.Advance(TimeSpan.FromMinutes(20));

		TokenResponse third = await RefreshAsync(client, second.RefreshToken);

		(await LastActiveAsync(app, session.User.Id)).ShouldBe(recorded,
			"a second launch twenty minutes later must not cost a write");

		// And once the window has passed, it moves again — the throttle is a delay, not a stop.
		app.Clock.Advance(TimeSpan.FromMinutes(41));

		await RefreshAsync(client, third.RefreshToken);

		(await LastActiveAsync(app, session.User.Id)).ShouldBeGreaterThan(recorded);
	}

	/// <summary>
	/// A sign-in from a device the account has not seen before is worth an email — and cannot
	/// be sent when there is no address, which is another line in §7.2's trade-off.
	/// </summary>
	[Fact]
	public async Task NewDevice_EmailsASecurityAlertWhenAnAddressIsKnown()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse first = await client.RegisterAsync("DaveSmith", email: "dave@example.com");

		app.Emails.Clear();

		await SignInAsync(client, "DaveSmith", "A phone nobody has seen");

		app.Emails.To("dave@example.com").ShouldHaveSingleItem()
			.PlainTextBody.ShouldContain("A phone nobody has seen");

		app.Emails.Clear();

		// Signing in again on the same device is not news.
		await SignInAsync(client, "DaveSmith", deviceId: DeviceIdOf(first));

		app.Emails.Sent.ShouldBeEmpty();
	}

	[Fact]
	public async Task NewDevice_WithNoAddressOnFile_SendsNothingAndStillSignsIn()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		await client.RegisterAsync("NoEmailNed");

		app.Emails.Clear();

		TokenResponse session = await SignInAsync(client, "NoEmailNed", "Some phone");

		session.AccessToken.ShouldNotBeNullOrWhiteSpace();
		app.Emails.Sent.ShouldBeEmpty();
	}

	/// <summary>
	/// A mail server being down does not undo a sign-in that already succeeded (§7.12).
	/// </summary>
	[Fact]
	public async Task NewDevice_AlertFails_SignInStillSucceeds()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		await client.RegisterAsync("DaveSmith", email: "dave@example.com");

		app.Emails.FailWith = new InvalidOperationException("transport is down");

		TokenResponse session = await SignInAsync(client, "DaveSmith", "Another phone");

		session.AccessToken.ShouldNotBeNullOrWhiteSpace();
	}

	/// <summary>
	/// The loop the client actually runs: sign in, keep the id the answer carried, send it back
	/// next time. Without it every sign-in mints a row and the screen fills with devices the
	/// rider has never owned.
	/// </summary>
	[Fact]
	public async Task SigningInAgain_WithTheIdItWasGiven_StaysOneDevice()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse first = await client.RegisterAsync("DaveSmith");

		TokenResponse second = await SignInAsync(client, "DaveSmith", "Pixel 9", deviceId: first.DeviceId);
		TokenResponse third = await SignInAsync(client, "DaveSmith", "Pixel 9", deviceId: second.DeviceId);

		third.DeviceId.ShouldBe(first.DeviceId);

		using HttpClient authed = app.CreateClient().Authenticated(third);

		List<DeviceSession> sessions =
			(await authed.GetFromJsonAsync<List<DeviceSession>>(SessionsUrl))!;

		sessions.ShouldHaveSingleItem().Name.ShouldBe("Pixel 9",
			"the name arrives on the second sign-in and renames the row rather than adding one");
	}

	/// <summary>
	/// The id is in the <c>dev</c> claim either way, but a client should not have to read its own
	/// JWT to find out what to send back — so the body says it too, on every grant.
	/// </summary>
	[Fact]
	public async Task EverySession_SaysWhichDeviceItBelongsTo()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse registered = await client.RegisterAsync("DaveSmith");
		registered.DeviceId.ShouldBe(DeviceIdOf(registered));

		TokenResponse signedIn = await SignInAsync(client, "DaveSmith", deviceId: registered.DeviceId);
		signedIn.DeviceId.ShouldBe(registered.DeviceId);

		TokenResponse refreshed = await RefreshAsync(client, signedIn.RefreshToken);
		refreshed.DeviceId.ShouldBe(registered.DeviceId,
			"a rotation continues the session it was handed, so the device cannot change under it");
	}

	/// <summary>
	/// A handset volunteers its own name and nothing verifies it. Longer than the column is a
	/// chatty phone, not an attack, and it must not fail the sign-in it was attached to.
	/// </summary>
	[Fact]
	public async Task DeviceName_LongerThanTheColumn_IsTrimmedRatherThanRefused()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		await client.RegisterAsync("DaveSmith");

		TokenResponse session = await SignInAsync(client, "DaveSmith", new string('x', 200));

		using HttpClient authed = app.CreateClient().Authenticated(session);

		List<DeviceSession> sessions =
			(await authed.GetFromJsonAsync<List<DeviceSession>>(SessionsUrl))!;

		sessions.Single(row => row.DeviceId == session.DeviceId).Name!.Length.ShouldBe(60);
	}

	private static Guid DeviceIdOf(TokenResponse session) =>
		Guid.Parse(new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler()
			.ReadJsonWebToken(session.AccessToken)
			.GetPayloadValue<string>(DlrClaims.Device));

	private static async Task<DateTimeOffset> LastActiveAsync(
		DlrWebApplicationFactory app,
		Guid userId) =>
		await app.WithDatabaseAsync(async database =>
			await database.Users
				.Where(user => user.Id == userId)
				.Select(user => user.LastActiveUtc)
				.SingleAsync());

	private static Task<HttpResponseMessage> PostRefreshAsync(HttpClient client, string refreshToken) =>
		client.PostAsJsonAsync(TokenUrl, new TokenRequest(GrantTypes.Refresh, RefreshToken: refreshToken));

	private static async Task<TokenResponse> RefreshAsync(HttpClient client, string refreshToken)
	{
		using HttpResponseMessage response = await PostRefreshAsync(client, refreshToken);

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
	}

	private static async Task<TokenResponse> SignInAsync(
		HttpClient client,
		string userName,
		string? deviceName = null,
		Guid? deviceId = null)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			TokenUrl,
			new TokenRequest(
				GrantTypes.Password,
				userName,
				TestRegistration.ValidPassword,
				DeviceId: deviceId,
				DeviceName: deviceName));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
	}
}
