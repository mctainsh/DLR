using System.Net.Http.Json;
using DLR.Core.Contracts.Admin;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using DLR.TestSupport.Tracks;

namespace DLR.Server.Tests.Admin;

/// <summary>
/// The figures beside each account (§14.6).
/// <para>
/// The counts are correlated sub-selects inside one projection, which is exactly the kind of query
/// that compiles, runs, and quietly counts the wrong thing - a predicate on the wrong owner column
/// returns a number rather than an error. So each column is asserted against content one account
/// made and another did not.
/// </para>
/// </summary>
public sealed class AdminUserFiguresTests(PostgresFixture postgres)
{
	private const string UsersUrl = "/api/v1/admin/users";

	[Fact]
	public async Task TheCountsAreThisAccountsOwn_NotEverybodys()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("TheAdmin"));

		using HttpClient client = app.CreateClient();

		TokenResponse adminSession = await client.RegisterAsync("TheAdmin");
		TokenResponse riderSession = await app.CreateClient().RegisterAsync("DaveSmith");

		using HttpClient asAdmin = app.CreateClient().Authenticated(adminSession);
		using HttpClient asRider = app.CreateClient().Authenticated(riderSession);

		// One rider makes one of everything the list counts; the administrator makes none of it.
		using HttpResponseMessage ride = await asRider.PostAsJsonAsync(
			"/api/v1/group-rides",
			new CreateRideRequest("Sunday loop", app.Clock.GetUtcNow().AddDays(1)));

		ride.EnsureSuccessStatusCode();

		TrackGeometry geometry = new(
		[
			.. Enumerable.Range(0, 20).Select(index => new TrackPoint(
				GpxFixtures.BaseLatitude + (index * GpxFixtures.MetresToDegreesLatitude(20)),
				GpxFixtures.BaseLongitude,
				50 + (index % 7),
				GpxFixtures.Start.AddSeconds(index * 10))),
		]);

		using HttpResponseMessage track = await asRider.PostAsJsonAsync(
			"/api/v1/tracks",
			new UploadTrackRequest(Guid.NewGuid(), geometry.Points, null, "A ride out"));

		track.EnsureSuccessStatusCode();

		IReadOnlyList<AdminUserRow> rows =
			(await asAdmin.GetFromJsonAsync<List<AdminUserRow>>(UsersUrl))!;

		AdminUserRow rider = rows.Single(row => row.UserName == "DaveSmith");
		AdminUserRow admin = rows.Single(row => row.UserName == "TheAdmin");

		rider.Adventures.ShouldBe(1);
		rider.Routes.ShouldBe(1);

		// The other side of the same assertion, and the half that catches a predicate counting
		// every row in the table rather than this account's.
		admin.Adventures.ShouldBe(0);
		admin.Routes.ShouldBe(0);
	}

	/// <summary>
	/// A fresh account is all zeros and a real created date - not nulls, and not a lifetime GPS
	/// count inherited from whoever registered before it.
	/// </summary>
	[Fact]
	public async Task AFreshAccount_ReadsAsZeroEverywhere()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("TheAdmin"));

		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("TheAdmin");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		AdminUserRow row = (await authed.GetFromJsonAsync<List<AdminUserRow>>(UsersUrl))!.Single();

		row.PositionsRecorded.ShouldBe(0);
		row.PositionsHeld.ShouldBe(0);
		row.Adventures.ShouldBe(0);
		row.Routes.ShouldBe(0);
		row.Posts.ShouldBe(0);
		row.Photos.ShouldBe(0);
		row.Markers.ShouldBe(0);
		row.TrackedHours.ShouldBe(0);

		row.CreatedUtc.ShouldBe(app.Clock.GetUtcNow(), TimeSpan.FromMinutes(1));
	}

	[Fact]
	public async Task TheSearch_FiltersByUserName()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("TheAdmin"));

		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("TheAdmin");
		await app.CreateClient().RegisterAsync("DaveSmith");
		await app.CreateClient().RegisterAsync("SarahJones");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		IReadOnlyList<AdminUserRow> matched =
			(await authed.GetFromJsonAsync<List<AdminUserRow>>($"{UsersUrl}?search=dave"))!;

		// Case-insensitive and a substring, because it is typed into a box a character at a time.
		matched.Select(row => row.UserName).ShouldBe(["DaveSmith"]);
	}

	/// <summary>
	/// An underscore is a legal username character (§7.2), so it has to reach <c>ILIKE</c> as text.
	/// Unescaped it is a wildcard, and this search returns every account on the server.
	/// </summary>
	[Fact]
	public async Task TheSearch_TreatsAWildcardAsACharacter()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("TheAdmin"));

		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("TheAdmin");
		await app.CreateClient().RegisterAsync("Dave_Smith");
		await app.CreateClient().RegisterAsync("SarahJones");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		IReadOnlyList<AdminUserRow> matched =
			(await authed.GetFromJsonAsync<List<AdminUserRow>>($"{UsersUrl}?search=_"))!;

		matched.Select(row => row.UserName).ShouldBe(["Dave_Smith"]);
	}

	/// <summary>
	/// The email address is on this screen on purpose - it is how somebody is reached when
	/// something has gone wrong - but nothing else off the account row may travel with it.
	/// </summary>
	[Fact]
	public async Task TheRow_CarriesNoCredentialFields()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: AdminRosterSettings.Roster("TheAdmin"));

		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("TheAdmin");

		using HttpClient authed = app.CreateClient().Authenticated(session);

		string json = await authed.GetStringAsync(UsersUrl);

		// Read as text rather than through the contract: the contract cannot carry these, so a
		// typed assertion would prove nothing. This one fails if somebody ever swaps the hand
		// projection for the entity.
		json.ShouldNotContain("passwordHash", Case.Insensitive);
		json.ShouldNotContain("securityStamp", Case.Insensitive);
		json.ShouldNotContain("concurrencyStamp", Case.Insensitive);
	}
}
