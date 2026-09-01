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
/// The home private area, now that the account holds it rather than the phone (§10.1, §7.14).
/// <para>
/// The move was made because device-only storage lost people their circle to app updates and
/// reinstalls, in silence. It buys the server a column that names where somebody lives, so the
/// tests that matter most here are the ones about who can read it: the owner, over their own
/// token, and nobody else by any route.
/// </para>
/// </summary>
public sealed class PrivateAreaTests(PostgresFixture postgres)
{
	private const string AreaUrl = "/api/v1/me/private-area";

	private static readonly PrivateAreaSettings Home = new(-33.868, 151.209, 1_000);

	[Fact]
	public async Task FreshAccount_HasNoPrivateArea()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");
		using HttpClient authed = app.CreateClient().Authenticated(session);

		PrivateAreaResponse area = (await authed.GetFromJsonAsync<PrivateAreaResponse>(AreaUrl))!;

		area.Area.ShouldBeNull("no area is the shipped state, and it is a 200 rather than a 404.");
	}

	[Fact]
	public async Task Saved_ThenReadBackByTheSameAccountOnAnotherDevice()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");
		using HttpClient phone = app.CreateClient().Authenticated(session);

		await SetAsync(phone, Home);

		// A second client on the same account stands for the rider's next handset - the whole
		// reason this stopped being a device setting.
		using HttpClient newPhone = app.CreateClient().Authenticated(session);

		PrivateAreaResponse area = (await newPhone.GetFromJsonAsync<PrivateAreaResponse>(AreaUrl))!;

		area.Area.ShouldNotBeNull();
		area.Area!.Latitude.ShouldBe(Home.Latitude, tolerance: 1e-6);
		area.Area.Longitude.ShouldBe(Home.Longitude, tolerance: 1e-6);
		area.Area.RadiusM.ShouldBe(Home.RadiusM);

		AppUser stored = await StoredAsync(app, session.User.Id);

		// In the row, not only in the projection. These three columns move together, and a
		// partial write is a row that reads as unset while still holding a coordinate.
		stored.HasPrivateArea.ShouldBeTrue();
		stored.PrivateAreaLat!.Value.ShouldBe(Home.Latitude, tolerance: 1e-6);
	}

	/// <summary>
	/// Six decimal places is about 0.1 m, and the rider is lining the circle up with their own
	/// roof. A round trip that moved it would send them back to the screen after every save.
	/// </summary>
	[Fact]
	public async Task Saved_KeepsTheCentreWhereTheRiderPutIt()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");
		using HttpClient authed = app.CreateClient().Authenticated(session);

		PrivateAreaSettings precise = new(-33.8688197, 151.2092955, 750);

		PrivateAreaResponse saved = await SetAsync(authed, precise);

		saved.Area!.Latitude.ShouldBe(precise.Latitude, tolerance: 1e-9);
		saved.Area.Longitude.ShouldBe(precise.Longitude, tolerance: 1e-9);
	}

	[Fact]
	public async Task Saved_ARadiusOutsideTheOfferedRange_IsClampedAndReportedBack()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");
		using HttpClient authed = app.CreateClient().Authenticated(session);

		PrivateAreaResponse tiny = await SetAsync(authed, Home with { RadiusM = 5 });
		PrivateAreaResponse huge = await SetAsync(authed, Home with { RadiusM = 500_000 });

		// Clamped rather than refused - the client clamps too, and the response is what the
		// screen reports, so the two must agree about what was kept.
		tiny.Area!.RadiusM.ShouldBe(PrivateAreaSettings.MinRadiusM);
		huge.Area!.RadiusM.ShouldBe(PrivateAreaSettings.MaxRadiusM);
	}

	[Fact]
	public async Task Saved_ACentreThatIsNotOnTheEarth_IsRefusedAndChangesNothing()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");
		using HttpClient authed = app.CreateClient().Authenticated(session);

		await SetAsync(authed, Home);

		using HttpResponseMessage response = await authed.PutAsJsonAsync(
			AreaUrl, new PrivateAreaSettings(200, 151.209, 1_000));

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		// A broken client must not be able to move somebody's circle to a place they never chose,
		// and must not be able to remove it either.
		AppUser stored = await StoredAsync(app, session.User.Id);
		stored.PrivateAreaLat!.Value.ShouldBe(Home.Latitude, tolerance: 1e-6);
	}

	[Fact]
	public async Task Removed_ClearsAllThreeColumns_AndIsIdempotent()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");
		using HttpClient authed = app.CreateClient().Authenticated(session);

		await SetAsync(authed, Home);

		using HttpResponseMessage first = await authed.DeleteAsync(AreaUrl);
		first.StatusCode.ShouldBe(HttpStatusCode.OK);

		AppUser stored = await StoredAsync(app, session.User.Id);
		stored.PrivateAreaLat.ShouldBeNull();
		stored.PrivateAreaLon.ShouldBeNull();
		stored.PrivateAreaRadiusM.ShouldBeNull();

		// Asking for a state rather than for a row: an account with no area is a 200, not a 404.
		using HttpResponseMessage again = await authed.DeleteAsync(AreaUrl);
		again.StatusCode.ShouldBe(HttpStatusCode.OK);
	}

	/// <summary>
	/// The reason this is its own sub-resource rather than three more fields on the profile:
	/// <c>PUT /me/profile</c> replaces the whole profile, so a client that has never heard of the
	/// private area must not be able to erase one by saving a display name (§7.14).
	/// </summary>
	[Fact]
	public async Task SavingTheProfile_DoesNotDisturbThePrivateArea()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");
		using HttpClient authed = app.CreateClient().Authenticated(session);

		await SetAsync(authed, Home);

		using HttpResponseMessage profile = await authed.PutAsJsonAsync(
			"/api/v1/me/profile", new UpdateProfileRequest(DisplayName: "Dave"));

		profile.StatusCode.ShouldBe(HttpStatusCode.OK);

		AppUser stored = await StoredAsync(app, session.User.Id);

		stored.DisplayName.ShouldBe("Dave");
		stored.HasPrivateArea.ShouldBeTrue("a privacy control must not be erasable as a side effect.");
	}

	/// <summary>
	/// The guarantee that did not change when the storage did: an area is readable by the account
	/// that set it and by nothing else. Both halves are asserted - the route has no user id to
	/// ask with, and the one route that does answer with another rider's fields has no field for
	/// it (§7.3).
	/// </summary>
	[Fact]
	public async Task AnotherRider_CannotSeeIt_OnAnyRoute()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse owner = await client.RegisterAsync("DaveSmith");
		TokenResponse other = await app.CreateClient().RegisterAsync("SamJones");

		using HttpClient ownerClient = app.CreateClient().Authenticated(owner);
		await SetAsync(ownerClient, Home);

		using HttpClient otherClient = app.CreateClient().Authenticated(other);

		// /me/private-area is the caller's own, whoever the caller is.
		PrivateAreaResponse theirs = (await otherClient.GetFromJsonAsync<PrivateAreaResponse>(AreaUrl))!;
		theirs.Area.ShouldBeNull();

		// And the shared profile carries no trace of it, as a payload rather than as a type: a
		// field added to SharedProfile later would fail here rather than ship.
		using HttpResponseMessage shared = await otherClient.GetAsync($"/api/v1/users/{owner.User.Id}/profile");
		shared.StatusCode.ShouldBe(HttpStatusCode.OK);

		string body = await shared.Content.ReadAsStringAsync();
		body.Contains("rivate", StringComparison.Ordinal).ShouldBeFalse(body);
		body.Contains("151.2", StringComparison.Ordinal).ShouldBeFalse(body);
	}

	[Fact]
	public async Task WithoutAToken_EveryVerbIsRejected()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage read = await client.GetAsync(AreaUrl);
		using HttpResponseMessage write = await client.PutAsJsonAsync(AreaUrl, Home);
		using HttpResponseMessage remove = await client.DeleteAsync(AreaUrl);

		read.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		write.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		remove.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	private static async Task<PrivateAreaResponse> SetAsync(HttpClient client, PrivateAreaSettings request)
	{
		using HttpResponseMessage response = await client.PutAsJsonAsync(AreaUrl, request);

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<PrivateAreaResponse>())!;
	}

	private static async Task<AppUser> StoredAsync(DlrWebApplicationFactory app, Guid userId) =>
		await app.WithDatabaseAsync(async database =>
			await database.Users.SingleAsync(user => user.Id == userId));
}
