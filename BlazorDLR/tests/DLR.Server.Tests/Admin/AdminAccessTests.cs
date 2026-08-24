using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Admin;
using DLR.Core.Contracts.Identity;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;

namespace DLR.Server.Tests.Admin;

/// <summary>
/// Who may reach the administration screens (§14.6).
/// <para>
/// The roster is a list of usernames in the server's own configuration rather than a column or a
/// role, so these tests set it the way a deployment would — through configuration — and the thing
/// they are checking is that <em>nothing else</em> opens the door.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AdminAccessTests(PostgresFixture postgres)
{
	private const string UsersUrl = "/api/v1/admin/users";
	private const string StatsUrl = "/api/v1/admin/stats";
	private const string LogsUrl = "/api/v1/admin/logs";

	[Fact]
	public async Task EveryAdminRoute_IsRefusedToAnAccountNotOnTheRoster()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("TheAdmin"));

		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		// All three, not a representative one: they are three separate routes and the guard is an
		// attribute somebody could forget on the next one added.
		foreach (string url in new[] { UsersUrl, StatsUrl, LogsUrl })
		{
			using HttpResponseMessage response = await authed.GetAsync(url);

			response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, url);
		}
	}

	[Fact]
	public async Task EveryAdminRoute_IsRefusedToAnAnonymousCaller()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("TheAdmin"));

		using HttpClient client = app.CreateClient();

		foreach (string url in new[] { UsersUrl, StatsUrl, LogsUrl })
		{
			using HttpResponseMessage response = await client.GetAsync(url);

			response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, url);
		}
	}

	[Fact]
	public async Task AnAccountOnTheRoster_IsLetThrough()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("TheAdmin"));

		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("TheAdmin");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		using HttpResponseMessage response = await authed.GetAsync(UsersUrl);

		response.StatusCode.ShouldBe(HttpStatusCode.OK);
	}

	/// <summary>
	/// Identity normalises usernames and whoever edits the config file is typing from memory, so
	/// the two have to meet case-insensitively or the setting silently does nothing.
	/// </summary>
	[Fact]
	public async Task TheRoster_MatchesRegardlessOfCase()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("THEADMIN"));

		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("TheAdmin");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		using HttpResponseMessage response = await authed.GetAsync(StatsUrl);

		response.StatusCode.ShouldBe(HttpStatusCode.OK);
	}

	/// <summary>
	/// A blank entry must not promote everybody. It is the shape a half-edited config file takes,
	/// and the failure would be silent and total.
	/// </summary>
	[Fact]
	public async Task ABlankRosterEntry_PromotesNobody()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("", "   "));

		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		using HttpResponseMessage response = await authed.GetAsync(UsersUrl);

		response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
	}

	/// <summary>
	/// The flag the client reads to decide whether to offer the menu has to be the same answer the
	/// policy gives, or an administrator is shown a door that does not open — or worse, is not
	/// shown one that does.
	/// </summary>
	[Fact]
	public async Task TheProfileFlag_AgreesWithThePolicy()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("TheAdmin"));

		using HttpClient client = app.CreateClient();

		TokenResponse adminSession = await client.RegisterAsync("TheAdmin");
		TokenResponse riderSession = await app.CreateClient().RegisterAsync("DaveSmith");

		using HttpClient asAdmin = app.CreateClient().Authenticated(adminSession);
		using HttpClient asRider = app.CreateClient().Authenticated(riderSession);

		OwnProfile admin = (await asAdmin.GetFromJsonAsync<OwnProfile>("/api/v1/me/profile"))!;
		OwnProfile rider = (await asRider.GetFromJsonAsync<OwnProfile>("/api/v1/me/profile"))!;

		admin.IsAdmin.ShouldBeTrue();
		rider.IsAdmin.ShouldBeFalse();

		// And the same fact reaches the list, so an administrator can see who else is one.
		IReadOnlyList<AdminUserRow> rows =
			(await asAdmin.GetFromJsonAsync<List<AdminUserRow>>(UsersUrl))!;

		rows.Single(row => row.UserName == "TheAdmin").IsAdmin.ShouldBeTrue();
		rows.Single(row => row.UserName == "DaveSmith").IsAdmin.ShouldBeFalse();
	}
}
