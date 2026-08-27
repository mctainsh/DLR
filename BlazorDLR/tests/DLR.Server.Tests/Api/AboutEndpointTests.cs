using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;

namespace DLR.Server.Tests.Api;

/// <summary>
/// AGPL §13 obliges anyone who lets users interact with a modified version remotely to
/// offer those users the Corresponding Source of <em>that running version</em> (§14.6.2).
/// Publishing the repository is not enough on its own, and neither is a link on a
/// marketing page if the deployed build is ahead of it — so the server has to be able to
/// say exactly which commit it is.
/// </summary>
public sealed class AboutEndpointTests(PostgresFixture postgres)
{
	private const string AboutUrl = "/api/v1/about";

	[Fact]
	public async Task About_ReturnsSourceUrlAndCommitOfRunningBuild()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		JsonElement about = await GetAboutAsync(client);

		about.GetProperty("licence").GetString().ShouldBe("AGPL-3.0-only");

		about.GetProperty("sourceUrl").GetString()
			.ShouldNotBeNullOrWhiteSpace("a source offer with no address is not an offer");

		about.GetProperty("commit").GetString()
			.ShouldNotBeNullOrWhiteSpace("§13 is about the commit, not the product name");

		about.GetProperty("version").GetString().ShouldNotBeNullOrWhiteSpace();
	}

	[Fact]
	public async Task About_IsReachableWithoutAuthentication()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response = await client.GetAsync(AboutUrl);

		response.StatusCode.ShouldBe(HttpStatusCode.OK,
			"the offer is owed to everyone who interacts with the server, which includes " +
			"people who have not signed in and people who never will");
	}

	[Fact]
	public async Task About_IsStillReachableWithAnUnusableToken()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");

		using HttpResponseMessage response = await client.GetAsync(AboutUrl);

		response.StatusCode.ShouldBe(HttpStatusCode.OK,
			"an expired or malformed token must not be able to withhold a licence obligation");
	}

	/// <summary>
	/// The pointer is generated from the build, never hand-maintained: a constant somebody
	/// has to remember to bump is wrong within a week, and a wrong source pointer is worse
	/// than no source pointer.
	/// </summary>
	[Fact]
	public async Task About_CommitMatchesAssemblyInformationalVersion()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		JsonElement about = await GetAboutAsync(client);

		string informational = typeof(Program).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
			.InformationalVersion;

		about.GetProperty("version").GetString().ShouldBe(informational);

		string commit = about.GetProperty("commit").GetString()!;
		string sha = commit.Replace("+dirty", string.Empty, StringComparison.Ordinal);

		informational.ShouldContain(sha);
		sha.Length.ShouldBe(40, $"a git SHA is 40 hex characters; got '{commit}'");
	}

	/// <summary>
	/// A build from a modified working tree is a §13 breach in progress if it is deployed.
	/// The server is not asked to refuse — CI refusing to publish such an image is the real
	/// fix — but it must not pretend to be the commit it nearly is.
	/// </summary>
	[Fact]
	public async Task About_MarksABuildFromAModifiedWorkingTreeAsDirty()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		JsonElement about = await GetAboutAsync(client);

		string informational = typeof(Program).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
			.InformationalVersion;

		bool builtDirty = informational.EndsWith(".dirty", StringComparison.Ordinal);

		about.GetProperty("commit").GetString()!
			.EndsWith("+dirty", StringComparison.Ordinal)
			.ShouldBe(builtDirty);
	}

	private static async Task<JsonElement> GetAboutAsync(HttpClient client)
	{
		using HttpResponseMessage response = await client.GetAsync(AboutUrl);
		response.EnsureSuccessStatusCode();

		return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
	}
}
