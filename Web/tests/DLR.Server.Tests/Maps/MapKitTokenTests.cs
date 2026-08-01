using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Maps;
using DLR.Server.Maps;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.IdentityModel.JsonWebTokens;

namespace DLR.Server.Tests.Maps;

/// <summary>
/// <c>GET /api/v1/maps/token</c> — MapKit JS makes the map a server dependency (§4.5).
/// <para>
/// The private key is in the same class as an APNs key: leaking it means somebody else bills their
/// map usage here. So the tests are about two things — that the key never leaves, and that a server
/// which cannot mint says so rather than handing back an empty grey rectangle.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MapKitTokenTests(PostgresFixture postgres)
{
	private const string TokenUrl = "/api/v1/maps/token";
	private const string TeamId = "TEAM123456";
	private const string KeyId = "KEY7654321";

	/// <summary>
	/// The token has to be exactly what Apple expects or the map fails in a way nobody can debug
	/// from the browser: ES256, the key id in the <em>header</em>, the team as <c>iss</c>, and an
	/// expiry that is actually short.
	/// </summary>
	[Fact]
	public async Task MapsToken_IsSignedES256AndCarriesTheKeyIdAndTeam()
	{
		string pem = NewKeyPem();

		await using DlrWebApplicationFactory app = await ConfiguredAsync(postgres, pem);

		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		MapToken minted = await MintAsync(rider);

		JsonWebToken token = new(minted.Token);

		token.Alg.ShouldBe("ES256");
		token.Kid.ShouldBe(KeyId, "Apple picks the verification key by this, and it lives in the header");
		token.Issuer.ShouldBe(TeamId);

		// Short, and reported rather than left for the client to decode.
		minted.ExpiresUtc.ShouldBe(app.Clock.GetUtcNow().AddMinutes(30), TimeSpan.FromSeconds(2));

		// And it really verifies against the key we configured — a token signed with anything else
		// would satisfy every assertion above and be rejected by Apple.
		using ECDsa key = ECDsa.Create();
		key.ImportFromPem(pem);

		VerifiesWith(minted.Token, key).ShouldBeTrue();
	}

	/// <summary>
	/// The rule this endpoint exists for. A `.p8` that reaches a client is a key somebody else can
	/// bill map usage against, and it is on §14.2's never-commit list for the same reason.
	/// </summary>
	[Fact]
	public async Task MapsToken_NeverReturnsAnythingFromThePrivateKey()
	{
		string pem = NewKeyPem();

		await using DlrWebApplicationFactory app = await ConfiguredAsync(postgres, pem);

		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		using HttpResponseMessage response = await rider.GetAsync(TokenUrl);

		string body = await response.Content.ReadAsStringAsync();

		body.ShouldNotContain("PRIVATE KEY");

		// Not just the armour: the base64 body of the key itself, in case something ever tried to
		// be helpful by stripping the header lines.
		string material = string.Concat(pem
			.Split('\n')
			.Where(line => !line.StartsWith("-----", StringComparison.Ordinal))
			.Select(line => line.Trim()));

		material.Length.ShouldBeGreaterThan(40);
		body.ShouldNotContain(material[..40]);
	}

	/// <summary>
	/// §4.5, verbatim: <em>a map that cannot get a token shows a stated error, not an empty grey
	/// rectangle</em>. A client cannot draw the honest failure from a 500, so an unconfigured
	/// server has to say what is wrong in a shape the client can branch on.
	/// </summary>
	[Fact]
	public async Task MapsToken_WhenNoKeyIsConfigured_ReturnsAStatedError()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		using HttpResponseMessage response = await rider.GetAsync(TokenUrl);

		response.StatusCode.ShouldBe(
			HttpStatusCode.ServiceUnavailable,
			"503 says this deployment has no key; 500 would say it is broken");

		JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

		problem.RootElement.GetProperty("title").GetString()
			.ShouldBe(MapKitEndpoints.NotConfiguredTitle);
	}

	/// <summary>
	/// A key that is present and unreadable is a deployment mistake, and it must not be echoed to
	/// the caller — but from the client's side it is the same situation as no key at all, so it
	/// gets the same stated answer.
	/// </summary>
	[Fact]
	public async Task MapsToken_WhenTheKeyIsUnreadable_SaysSoWithoutEchoingIt()
	{
		await using DlrWebApplicationFactory app =
			await ConfiguredAsync(postgres, "-----BEGIN PRIVATE KEY-----\nnot-a-key\n-----END PRIVATE KEY-----");

		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		using HttpResponseMessage response = await rider.GetAsync(TokenUrl);

		response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

		(await response.Content.ReadAsStringAsync()).ShouldNotContain("not-a-key");
	}

	/// <summary>§4.5: a public token endpoint is a free map quota for the internet, billed here.</summary>
	[Fact]
	public async Task MapsToken_WithoutAuthentication_IsRefused()
	{
		await using DlrWebApplicationFactory app = await ConfiguredAsync(postgres, NewKeyPem());

		using HttpClient anonymous = app.CreateClient();

		using HttpResponseMessage refused = await anonymous.GetAsync(TokenUrl);

		refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	/// <summary>
	/// Authentication is the main gate and not the only one. A token lasts half an hour and is
	/// cached client-side, so a real browser needs a handful a day — anything asking far more often
	/// is minting them for somewhere else.
	/// </summary>
	[Fact]
	public async Task MapsToken_MintedFarTooOften_IsRateLimited()
	{
		await using DlrWebApplicationFactory app = await ConfiguredAsync(
			postgres,
			NewKeyPem(),
			extra: new Dictionary<string, string?> { ["RateLimits:MapTokenPerHourPerAddress"] = "3" });

		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		for (int attempt = 0; attempt < 3; attempt++)
		{
			using HttpResponseMessage allowed = await rider.GetAsync(TokenUrl);

			allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		using HttpResponseMessage throttled = await rider.GetAsync(TokenUrl);

		throttled.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
	}

	private static Task<DlrWebApplicationFactory> ConfiguredAsync(
		PostgresFixture postgres,
		string privateKeyPem,
		Dictionary<string, string?>? extra = null)
	{
		Dictionary<string, string?> settings = new()
		{
			["Maps:MapKit:TeamId"] = TeamId,
			["Maps:MapKit:KeyId"] = KeyId,
			["Maps:MapKit:PrivateKeyPem"] = privateKeyPem,
			["Maps:MapKit:Origin"] = "https://dumbluckrides.example",
		};

		foreach ((string key, string? value) in extra ?? [])
		{
			settings[key] = value;
		}

		return DlrWebApplicationFactory.CreateAsync(postgres, settings: settings);
	}

	/// <summary>
	/// Generated, never a recorded one — the same rule as the GPX and image corpora, and with more
	/// force: a real MapKit key committed to a repository going public is exactly the leak §14.2
	/// exists to prevent.
	/// </summary>
	private static string NewKeyPem()
	{
		using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

		return key.ExportPkcs8PrivateKeyPem();
	}

	private static bool VerifiesWith(string token, ECDsa key)
	{
		string[] parts = token.Split('.');

		byte[] signed = System.Text.Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
		byte[] signature = Base64UrlDecode(parts[2]);

		return key.VerifyData(signed, signature, HashAlgorithmName.SHA256);
	}

	private static byte[] Base64UrlDecode(string value)
	{
		string padded = value.Replace('-', '+').Replace('_', '/');

		return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
	}

	private static async Task<MapToken> MintAsync(HttpClient client)
	{
		using HttpResponseMessage response = await client.GetAsync(TokenUrl);

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<MapToken>())!;
	}

	private static async Task<HttpClient> SignedInAsync(DlrWebApplicationFactory app, string userName)
	{
		using HttpClient registrar = app.CreateClient().From("203.0.113.40");

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}
}
