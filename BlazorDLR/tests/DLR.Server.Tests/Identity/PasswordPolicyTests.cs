using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DLR.Core.Contracts.Identity;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Identity;

/// <summary>
/// Length and one-of-each composition (§7.2, revised at v0.22 and again at v0.23).
/// <para>
/// v0.22 reversed the "length over composition" stance §7.2 originally took, at operator
/// request: composition rules are on so that every rejection surfaces as a specific message
/// the caller can act on ("must have at least one uppercase letter").
/// </para>
/// <para>
/// v0.23 removed the Pwned Passwords breach lookup entirely, also at operator request — the
/// security impact of a weak password on this application is judged not to be significant, so
/// the composition rules above are now the whole policy and there is no third-party call in
/// the registration path.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PasswordPolicyTests(PostgresFixture postgres)
{
	[Theory]
	[InlineData("Aa1", "under the six-character minimum")]
	[InlineData("abcdef1", "no uppercase letter")]
	[InlineData("ABCDEF1", "no lowercase letter")]
	[InlineData("Abcdefg", "no digit")]
	public async Task Register_PasswordMissingARequirement_IsRejected(string password, string why)
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response = await client.PostRegisterAsync("DaveSmith", password);

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, why);
	}

	/// <summary>
	/// The one that closes the loop with the client: a rejected password's reasons must land
	/// in the response body under a field the caller renders. The Welcome screen reads this
	/// same shape and lists every message.
	/// </summary>
	[Fact]
	public async Task Register_PasswordRejection_CarriesPerFieldMessagesTheClientCanRender()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response = await client.PostRegisterAsync("DaveSmith", "abc");

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();

		JsonElement passwordErrors = problem
			.GetProperty("errors")
			.GetProperty(nameof(RegisterRequest.Password));

		passwordErrors.ValueKind.ShouldBe(JsonValueKind.Array);
		passwordErrors.GetArrayLength().ShouldBeGreaterThan(0,
			"Identity produces one message per rule broken; the client renders them verbatim.");
	}

	/// <summary>
	/// A rejected password leaves nothing behind. The successor to §7.15's
	/// <c>Register_WeakOrBreachedPassword_IsRejected</c>, minus the corpus half that v0.23
	/// removed: the caller has to be told which field was wrong, and no account may exist
	/// afterwards.
	/// </summary>
	[Fact]
	public async Task Register_WeakPassword_IsRejectedAndCreatesNoAccount()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response = await client.PostRegisterAsync("DaveSmith", "password");

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();

		problem.GetProperty("errors").TryGetProperty(nameof(RegisterRequest.Password), out _)
			.ShouldBeTrue("the caller has to be told it is the password, not the username");

		int accounts = await app.WithDatabaseAsync(database => database.Users.CountAsync());

		accounts.ShouldBe(0);
	}

	/// <summary>
	/// The v0.22 shape: six characters, one uppercase, one lowercase, one digit. No symbol
	/// required.
	/// </summary>
	[Fact]
	public async Task Register_PasswordMeetingEveryRequirement_IsAccepted()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response =
			await client.PostRegisterAsync("DaveSmith", "Ride4mountains");

		response.StatusCode.ShouldBe(HttpStatusCode.Created);
	}
}
