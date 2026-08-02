using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Health;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;

namespace DLR.Server.Tests.Api;

/// <summary>
/// <c>GET /healthz</c> (§9).
/// <para>
/// The deploy check, and the disk alert. A container that answers HTTP while its schema is a
/// migration behind is the failure mode of a half-finished deploy and it does not announce itself:
/// every request works until one touches the column that is not there yet. A container on a full
/// disk answers HTTP too, right up until PostgreSQL cannot write (§9.1).
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HealthTests(PostgresFixture postgres)
{
	private const string HealthUrl = "/healthz";

	/// <summary>The one that says the deploy finished.</summary>
	[Fact]
	public async Task Health_ReturnsOkWithMigrationsApplied()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response = await client.GetAsync(HealthUrl);

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		HealthReport report = (await response.Content.ReadFromJsonAsync<HealthReport>())!;

		report.Status.ShouldBe("healthy");
		report.Database.ShouldBeTrue();
		report.MigrationsApplied.ShouldBeTrue();
		report.PendingMigrations.ShouldBe(0);
		report.BlobVolume.Ok.ShouldBeTrue();
	}

	/// <summary>
	/// Whatever is watching this reads the status code and nothing else, so a health endpoint that
	/// always answers 200 is a health endpoint nobody is watching. The disk floor is the cheapest
	/// available form of §9's "alert on disk usage": raise it above what the machine has and the
	/// pinger that already exists starts failing.
	/// </summary>
	[Fact]
	public async Task Health_WhenTheBlobVolumeIsBelowItsFloor_Answers503()
	{
		// A petabyte, which no machine running this suite has free.
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: new Dictionary<string, string?> { ["Health:MinimumFreeMb"] = "1000000000" });

		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response = await client.GetAsync(HealthUrl);

		response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

		HealthReport report = (await response.Content.ReadFromJsonAsync<HealthReport>())!;

		report.Status.ShouldBe("unhealthy");
		report.BlobVolume.Ok.ShouldBeFalse();

		report.Database.ShouldBeTrue(
			"the database is fine, and saying so is how somebody reading this knows where to look");
	}

	/// <summary>
	/// Anonymous, because the thing watching it is a free uptime pinger with no credential to
	/// present — which is also the reason the body says as little as it does.
	/// </summary>
	[Fact]
	public async Task Health_IsReachableWithoutAuthentication()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response = await client.GetAsync(HealthUrl);

		response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);

		string body = await response.Content.ReadAsStringAsync();

		// It is public, so it must not carry anything an attacker would want. A connection failure's
		// message names the host, the port and often the user.
		body.ShouldNotContain("Host=", Case.Insensitive);
		body.ShouldNotContain("Password", Case.Insensitive);

		// A *count* of pending migrations is fine; their identifiers describe the schema and the
		// order it was built in. The field names legitimately contain "migration", so the assertion
		// is against the shape of an identifier — a timestamp followed by a name.
		System.Text.RegularExpressions.Regex
			.IsMatch(body, @"\d{14}_\w+")
			.ShouldBeFalse("a migration identifier names a table this server has");
	}
}
