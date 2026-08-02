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
/// Length and a breach lookup, in place of composition rules (§7.2).
/// <para>
/// This matters more here than in most applications: an account registered without an email
/// address has no reset path at all, so the password is not the first of several credentials —
/// it is the only one there will ever be.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PasswordPolicyTests(PostgresFixture postgres)
{
	[Theory]
	[InlineData("short", "under the ten-character minimum")]
	[InlineData("123456789", "nine characters is still nine characters")]
	public async Task Register_WeakPassword_IsRejected(string password, string why)
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response = await client.PostRegisterAsync("DaveSmith", password);

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, why);
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
	/// Composition rules are off, deliberately (§7.2). A long passphrase with no digit, no
	/// capital and no symbol is a better password than <c>Passw0rd!</c>, and the policy has to
	/// agree or the policy is measuring compliance rather than strength.
	/// </summary>
	[Fact]
	public async Task Register_LongPassphraseWithoutCompositionVariety_IsAccepted()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response =
			await client.PostRegisterAsync("DaveSmith", "the wind was against us all the way home");

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
