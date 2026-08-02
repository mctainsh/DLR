using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DLR.Core.Contracts.Identity;
using DLR.Server.Identity;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DLR.Server.Tests.Identity;

/// <summary>
/// Length, one-of-each composition, and a breach lookup (§7.2 revised at v0.22).
/// <para>
/// This matters more here than in most applications: an account registered without an email
/// address has no reset path at all, so the password is not the first of several credentials —
/// it is the only one there will ever be.
/// </para>
/// <para>
/// v0.22 reversed the "length over composition" stance §7.2 originally took, at operator
/// request: composition rules are on so that every rejection surfaces as a specific message
/// the caller can act on ("must have at least one uppercase letter"). The Pwned Passwords
/// check still catches the shape §7.2 was worried about, and it stays.
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
	/// A password that meets every rule but is in the breach corpus is still rejected. This is
	/// v0.22's compromise: composition on, corpus on. Either alone lets one of the two shapes
	/// through, and this account has no reset path.
	/// </summary>
	[Fact]
	public async Task Register_MeetsCompositionButIsBreached_IsStillRejected()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		const string leaked = "Password1";
		app.Breaches.Breach(leaked);

		using HttpResponseMessage response = await client.PostRegisterAsync("DaveSmith", leaked);

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}

	/// <summary>
	/// The named §7.15 test. A breached password is refused however long it is — which is the
	/// whole argument for replacing composition rules with a corpus lookup, since the string
	/// below satisfies every rule Identity ships with.
	/// </summary>
	[Fact]
	public async Task Register_WeakOrBreachedPassword_IsRejected()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		const string leaked = "Passw0rd!2024";

		app.Breaches.Breach(leaked);

		using HttpResponseMessage response = await client.PostRegisterAsync("DaveSmith", leaked);

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();

		problem.GetProperty("errors").TryGetProperty(nameof(RegisterRequest.Password), out _)
			.ShouldBeTrue("the caller has to be told it is the password, not the username");

		app.Breaches.Queried.ShouldContain(leaked);

		int accounts = await app.WithDatabaseAsync(database => database.Users.CountAsync());

		accounts.ShouldBe(0);
	}

	/// <summary>
	/// The v0.22 shape: six characters, one uppercase, one lowercase, one digit. No symbol
	/// required. A password satisfying every rule and not in the breach corpus registers.
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

	/// <summary>
	/// A third-party outage must not stop signups.
	/// <para>
	/// The password used here is one the corpus <em>does</em> hold, so the test is not
	/// "registration works when nothing is wrong" — it is the deliberate decision to accept a
	/// known-bad password rather than turn away every new rider because someone else's service
	/// is down. Availability of the signup path wins; the alternative is an outage at Troy
	/// Hunt's expense becoming an outage at ours.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Register_BreachServiceUnavailable_StillAllowsRegistration()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		const string leaked = "Passw0rd!2024";

		app.Breaches.Breach(leaked).GoOffline();

		using HttpResponseMessage response = await client.PostRegisterAsync("DaveSmith", leaked);

		response.StatusCode.ShouldBe(HttpStatusCode.Created);

		int accounts = await app.WithDatabaseAsync(database => database.Users.CountAsync());

		accounts.ShouldBe(1);
	}

	/// <summary>
	/// Only the first five hexadecimal characters of the SHA-1 ever leave the server, and the
	/// password itself never does. Asserted against the real client's request rather than its
	/// result, because k-anonymity is a property of what was sent.
	/// </summary>
	[Fact]
	public async Task BreachCheck_SendsOnlyTheFirstFiveHashCharacters()
	{
		RecordingHandler transport = new("0018A45C4D1DEF81644B54AB7F969B88D65:1");

		using HttpClient http = new(transport)
		{
			BaseAddress = new Uri(PwnedPasswordsClient.BaseAddress, UriKind.Absolute),
		};

		PwnedPasswordsClient sut = new(http, NullLogger<PwnedPasswordsClient>.Instance);

		// SHA-1("password") = 5BAA61E4C9B93F3F0682250B6CF8331B7EE68FD8.
		BreachCheckResult result = await sut.CheckAsync("password");

		// Exactly the prefix, and nothing after it. Sending the whole hash would hand the
		// operator the password itself for any password worth asking about.
		transport.RequestedPath.ShouldBe("/range/5BAA6");

		result.ShouldBe(BreachCheckResult.NotBreached,
			"the range response did not contain this password's suffix");
	}

	[Fact]
	public async Task BreachCheck_MatchesTheSuffixWithinTheRangeResponse()
	{
		// The second line is SHA-1("password") with its first five characters removed —
		// what the range API actually returns, since the prefix was the question.
		RecordingHandler transport = new(
			"0018A45C4D1DEF81644B54AB7F969B88D65:1\r\n1E4C9B93F3F0682250B6CF8331B7EE68FD8:12345");

		using HttpClient http = new(transport)
		{
			BaseAddress = new Uri(PwnedPasswordsClient.BaseAddress, UriKind.Absolute),
		};

		PwnedPasswordsClient sut = new(http, NullLogger<PwnedPasswordsClient>.Instance);

		(await sut.CheckAsync("password")).ShouldBe(BreachCheckResult.Breached);
	}

	[Fact]
	public async Task BreachCheck_TransportFailure_ReportsUnavailableRatherThanThrowing()
	{
		RecordingHandler transport = new(new HttpRequestException("no route to host"));

		using HttpClient http = new(transport)
		{
			BaseAddress = new Uri(PwnedPasswordsClient.BaseAddress, UriKind.Absolute),
		};

		PwnedPasswordsClient sut = new(http, NullLogger<PwnedPasswordsClient>.Instance);

		(await sut.CheckAsync("password")).ShouldBe(BreachCheckResult.Unavailable,
			"an exception escaping here would turn a third-party outage into a failed signup");
	}

	/// <summary>Answers one canned range response and remembers what was asked for.</summary>
	private sealed class RecordingHandler : HttpMessageHandler
	{
		private readonly string? _body;
		private readonly Exception? _failure;

		public RecordingHandler(string body) => _body = body;

		public RecordingHandler(Exception failure) => _failure = failure;

		public string? RequestedPath { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			RequestedPath = request.RequestUri?.AbsolutePath;

			if (_failure is not null)
			{
				return Task.FromException<HttpResponseMessage>(_failure);
			}

			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(_body!),
			});
		}
	}
}
