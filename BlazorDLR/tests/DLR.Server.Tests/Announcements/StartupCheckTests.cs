using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DLR.Core.Client;
using DLR.Core.Contracts.Announcements;
using DLR.Core.Contracts.Identity;
using DLR.Server.Tests.Admin;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;

namespace DLR.Server.Tests.Announcements;

/// <summary>
/// The launch check (§20).
/// <para>
/// The version half of it exists for a build too old to talk to this server correctly - which
/// includes one too old to <em>authenticate</em>. So the reachability tests here are not
/// box-ticking: an endpoint that answered 401 would be unreachable in exactly the case it was
/// written for.
/// </para>
/// </summary>
public sealed class StartupCheckTests(PostgresFixture postgres)
{
	private const string StartupUrl = "/api/v1/startup";

	[Fact]
	public async Task TheCheck_IsReachableWithoutAuthentication()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response = await client.GetAsync(Url(ClientRelease.Minimum.ToString()));

		response.StatusCode.ShouldBe(
			HttpStatusCode.OK,
			"a build the server will not serve cannot sign in to be told so");
	}

	[Fact]
	public async Task TheCheck_IsStillReachableWithAnUnusableToken()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");

		using HttpResponseMessage response = await client.GetAsync(Url(ClientRelease.Minimum.ToString()));

		response.StatusCode.ShouldBe(HttpStatusCode.OK);
	}

	[Fact]
	public async Task ACurrentClient_IsSupported()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		StartupCheck check = await ReadAsync(client, ClientRelease.Recommended.ToString());

		check.Support.ShouldBe(ClientSupport.Supported);
		check.MinimumVersion.ShouldBe(ClientRelease.Minimum.ToString());
		check.RecommendedVersion.ShouldBe(ClientRelease.Recommended.ToString());
	}

	[Fact]
	public async Task AClientBelowTheFloor_IsUnsupported()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		StartupCheck check = await ReadAsync(client, "0.0.0.1");

		check.Support.ShouldBe(ClientSupport.Unsupported);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("not-a-version")]
	public async Task AClientThatCannotSayWhatItIs_IsUnsupported(string? version)
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		StartupCheck check = await ReadAsync(client, version);

		check.Support.ShouldBe(
			ClientSupport.Unsupported,
			"the other way round would make the check opt-in for the builds most likely to be broken");
	}

	[Fact]
	public async Task AnAnnouncementInsideItsWindow_IsServed()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		await Notices.WriteAsync(app, "Server restart", app.Clock.GetUtcNow().AddMinutes(-5), TimeSpan.FromHours(2));

		using HttpClient client = app.CreateClient();

		StartupCheck check = await ReadAsync(client, ClientRelease.Minimum.ToString());

		check.Live.Single().Title.ShouldBe("Server restart");
	}

	[Fact]
	public async Task AnAnnouncementOutsideItsWindow_IsNotServed()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		DateTimeOffset now = app.Clock.GetUtcNow();

		await Notices.WriteAsync(app, "Next Friday", now.AddDays(3), TimeSpan.FromHours(2));
		await Notices.WriteAsync(app, "Last Tuesday", now.AddDays(-9), TimeSpan.FromHours(2));

		using HttpClient client = app.CreateClient();

		StartupCheck check = await ReadAsync(client, ClientRelease.Minimum.ToString());

		check.Live.ShouldBeEmpty("the window is evaluated on read, against the clock, in both directions");
	}

	[Fact]
	public async Task TheWorstOnesComeFirst_AndOnlyFiveAreServed()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		DateTimeOffset from = app.Clock.GetUtcNow().AddMinutes(-5);

		for (int index = 0; index < AnnouncementLimits.MaxLive + 3; index++)
		{
			await Notices.WriteAsync(app, $"Notice {index}", from, TimeSpan.FromHours(2));
		}

		await Notices.WriteAsync(app, "The urgent one", from, TimeSpan.FromHours(2), severity: NoticeSeverity.Urgent);

		using HttpClient client = app.CreateClient();

		StartupCheck check = await ReadAsync(client, ClientRelease.Minimum.ToString());

		check.Live.Count.ShouldBe(
			AnnouncementLimits.MaxLive,
			"a launch that opened nine dialogs is a launch nobody finishes");

		check.Live[0].Title.ShouldBe(
			"The urgent one",
			"the cap has to spend itself on the one that matters most, not on whichever was written first");
	}

	[Fact]
	public async Task EveryAnnouncementRoute_IsRefusedToAnAccountNotOnTheRoster()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("TheAdmin"));

		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		using HttpResponseMessage listed = await authed.GetAsync(AdminUrl);
		listed.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		using HttpResponseMessage written = await authed.PostAsJsonAsync(AdminUrl, Request(app, "Nope"));
		written.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		using HttpResponseMessage amended = await authed.PutAsJsonAsync($"{AdminUrl}/{Guid.NewGuid()}", Request(app, "Nope"));
		amended.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		using HttpResponseMessage removed = await authed.DeleteAsync($"{AdminUrl}/{Guid.NewGuid()}");
		removed.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task AnAdministrator_WritesOneAndEverybodySeesIt()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("TheAdmin"));

		using HttpClient asAdmin = await AdminClientAsync(app);

		using HttpResponseMessage written = await asAdmin.PostAsJsonAsync(AdminUrl, Request(app, "Maintenance tonight"));
		written.StatusCode.ShouldBe(HttpStatusCode.OK, await written.Content.ReadAsStringAsync());

		AdminAnnouncement created = (await written.Content.ReadFromJsonAsync<AdminAnnouncement>())!;
		created.CreatedBy.ShouldBe("TheAdmin");

		using HttpClient anyone = app.CreateClient();

		StartupCheck check = await ReadAsync(anyone, ClientRelease.Minimum.ToString());
		check.Live.Single().Title.ShouldBe("Maintenance tonight");

		using HttpResponseMessage removed = await asAdmin.DeleteAsync($"{AdminUrl}/{created.Id}");
		removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		StartupCheck after = await ReadAsync(anyone, ClientRelease.Minimum.ToString());
		after.Live.ShouldBeEmpty();
	}

	[Fact]
	public async Task AWindowThatNeverOpens_IsRefused()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("TheAdmin"));

		using HttpClient asAdmin = await AdminClientAsync(app);

		DateTimeOffset now = app.Clock.GetUtcNow();

		using HttpResponseMessage response = await asAdmin.PostAsJsonAsync(
			AdminUrl,
			new AdminAnnouncementRequest(NoticeSeverity.Information, "Backwards", "…", now, now.AddHours(-1)));

		response.StatusCode.ShouldBe(
			HttpStatusCode.BadRequest,
			"a message that silently never appears would sit in the list looking fine");
	}

	[Fact]
	public async Task AnEmptyMessage_IsRefused()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("TheAdmin"));

		using HttpClient asAdmin = await AdminClientAsync(app);

		DateTimeOffset now = app.Clock.GetUtcNow();

		using HttpResponseMessage response = await asAdmin.PostAsJsonAsync(
			AdminUrl,
			new AdminAnnouncementRequest(NoticeSeverity.Information, "  ", "  ", now, now.AddHours(1)));

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task TheHistory_KeepsWhatHasExpired()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("TheAdmin"));

		await Notices.WriteAsync(app, "Last Tuesday", app.Clock.GetUtcNow().AddDays(-9), TimeSpan.FromHours(2));

		using HttpClient asAdmin = await AdminClientAsync(app);

		IReadOnlyList<AdminAnnouncement> rows =
			(await asAdmin.GetFromJsonAsync<List<AdminAnnouncement>>(AdminUrl))!;

		rows.Single().Title.ShouldBe(
			"Last Tuesday",
			"this is also the screen that answers what we told people, and when");
	}

	private const string AdminUrl = "/api/v1/admin/announcements";

	private static string Url(string? version) =>
		version is null ? StartupUrl : $"{StartupUrl}?client={Uri.EscapeDataString(version)}";

	private static async Task<StartupCheck> ReadAsync(HttpClient client, string? version)
	{
		using HttpResponseMessage response = await client.GetAsync(Url(version));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<StartupCheck>())!;
	}

	/// <summary>A valid request against the fake clock, starting a minute ago and running two hours.</summary>
	private static AdminAnnouncementRequest Request(DlrWebApplicationFactory app, string title)
	{
		DateTimeOffset now = app.Clock.GetUtcNow();

		return new AdminAnnouncementRequest(
			NoticeSeverity.Information,
			title,
			"Something is happening.",
			now.AddMinutes(-1),
			now.AddHours(2));
	}

	private static async Task<HttpClient> AdminClientAsync(DlrWebApplicationFactory app)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync("TheAdmin");

		return app.CreateClient().Authenticated(session);
	}
}
