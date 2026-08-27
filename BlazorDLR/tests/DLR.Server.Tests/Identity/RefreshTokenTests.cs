using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Server.Data.Identity;
using DLR.Server.Identity;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Identity;

/// <summary>
/// Rotation, reuse detection and the grace window (§7.4).
/// <para>
/// Sessions never expire, so the refresh chain is the entire reason "never sign in again" is
/// true — and the entire way it can go wrong. Every test here is about one of the two failures
/// that matter: a stolen token that keeps working, or a rider signed out for doing nothing
/// wrong.
/// </para>
/// </summary>
public sealed class RefreshTokenTests(PostgresFixture postgres)
{
	private const string TokenUrl = "/api/v1/auth/token";

	[Fact]
	public async Task Refresh_ValidToken_RotatesAndInvalidatesPredecessor()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse first = await client.RegisterAsync("DaveSmith");

		TokenResponse second = await RefreshAsync(client, first.RefreshToken);

		second.RefreshToken.ShouldNotBe(first.RefreshToken, "every refresh issues a new token");
		second.User.Id.ShouldBe(first.User.Id);

		// Past the grace window, so the predecessor is genuinely spent rather than replayable.
		app.Clock.Advance(RefreshTokenGraceCache.Window + TimeSpan.FromSeconds(1));

		using HttpResponseMessage spent = await PostRefreshAsync(client, first.RefreshToken);

		spent.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		IReadOnlyList<RefreshToken> chain = await ChainAsync(app);

		chain.Count.ShouldBe(2);

		RefreshToken predecessor = chain[0];

		predecessor.UsedUtc.ShouldNotBeNull();
		predecessor.SuccessorId.ShouldBe(chain[1].Id);
		chain[1].FamilyId.ShouldBe(predecessor.FamilyId, "a rotation stays inside its family");
	}

	/// <summary>
	/// The raw token exists in exactly one place — the client. A database copy would turn a
	/// backup, a dump or a stray query into a set of working credentials.
	/// </summary>
	[Fact]
	public async Task Refresh_TokenIsStoredOnlyAsASha256Hash()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		RefreshToken stored = (await ChainAsync(app)).ShouldHaveSingleItem();

		stored.TokenHash.Length.ShouldBe(32, "SHA-256 is 32 bytes");

		System.Text.Encoding.UTF8.GetString(stored.TokenHash)
			.ShouldNotBe(session.RefreshToken);

		stored.ExpiresUtc.ShouldBe(
			stored.IssuedUtc.AddYears(10),
			tolerance: TimeSpan.FromSeconds(1),
			"§7.4 sets expiry rather than making it nullable, and ten years is how it writes " +
			"'never' without a nullable column");
	}

	/// <summary>
	/// Outside the window a reused token really does mean the token exists in two places, and
	/// revoking the whole chain is the correct aggressive response: whichever copy belongs to
	/// the thief, neither works now.
	/// </summary>
	[Fact]
	public async Task Refresh_ReusedToken_RevokesEntireFamily()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse first = await client.RegisterAsync("DaveSmith");
		TokenResponse second = await RefreshAsync(client, first.RefreshToken);

		app.Clock.Advance(RefreshTokenGraceCache.Window + TimeSpan.FromSeconds(1));

		using HttpResponseMessage replay = await PostRefreshAsync(client, first.RefreshToken);

		replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		// The successor is dead too. That is the point: the thief holds one of these and
		// there is no way to tell which, so both stop working.
		using HttpResponseMessage successor = await PostRefreshAsync(client, second.RefreshToken);

		successor.StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
			"revoking only the replayed token would leave the thief's copy working");

		IReadOnlyList<RefreshToken> chain = await ChainAsync(app);

		chain.ShouldAllBe(token => token.RevokedUtc != null);
		chain.ShouldAllBe(token => token.RevokedReason == RevocationReasons.ReuseDetected);
	}

	/// <summary>
	/// The grace window is not optional (§7.4).
	/// <para>
	/// A client that fires two requests, takes two 401s and refreshes twice has done nothing
	/// wrong. Without this, naive rotation reads that as theft and dumps the rider at a login
	/// screen mid-ride — and with permanent sessions it is the single most likely way anyone
	/// is ever signed out.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Refresh_ReusedWithinGraceWindow_ReturnsSameSuccessor()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse first = await client.RegisterAsync("DaveSmith");

		TokenResponse second = await RefreshAsync(client, first.RefreshToken);

		app.Clock.Advance(TimeSpan.FromSeconds(5));

		TokenResponse replayed = await RefreshAsync(client, first.RefreshToken);

		replayed.RefreshToken.ShouldBe(second.RefreshToken,
			"a second identical answer, not a second successor — otherwise the client stores " +
			"one token and the chain has forked");

		IReadOnlyList<RefreshToken> chain = await ChainAsync(app);

		chain.Count.ShouldBe(2, "a replay inside the window issues nothing new");
		chain.ShouldAllBe(token => token.RevokedUtc == null);

		// And the successor still works afterwards, which is the thing the client depends on.
		await RefreshAsync(client, second.RefreshToken);
	}

	[Fact]
	public async Task Refresh_ReplayedJustAfterTheWindowCloses_IsTreatedAsTheft()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse first = await client.RegisterAsync("DaveSmith");

		await RefreshAsync(client, first.RefreshToken);

		app.Clock.Advance(RefreshTokenGraceCache.Window + TimeSpan.FromMilliseconds(1));

		using HttpResponseMessage replay = await PostRefreshAsync(client, first.RefreshToken);

		replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		(await ChainAsync(app)).ShouldAllBe(token => token.RevokedUtc != null);
	}

	/// <summary>
	/// "Never sign in again" means what it says: there is no sliding window, no
	/// re-authentication prompt, and nothing that quietly expires while somebody is not
	/// looking (§7.4).
	/// </summary>
	[Fact]
	public async Task Refresh_AfterOneYearIdle_StillSucceeds()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse first = await client.RegisterAsync("DaveSmith");

		app.Clock.Advance(TimeSpan.FromDays(365));

		TokenResponse second = await RefreshAsync(client, first.RefreshToken);

		second.User.Id.ShouldBe(first.User.Id);
		second.AccessToken.ShouldNotBeNullOrWhiteSpace();
	}

	[Fact]
	public async Task Refresh_UnknownToken_IsRejectedWithoutTouchingAnySession()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		await client.RegisterAsync("DaveSmith");

		using HttpResponseMessage response =
			await PostRefreshAsync(client, "a-token-that-was-never-issued-by-anyone");

		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		(await ChainAsync(app)).ShouldAllBe(token => token.RevokedUtc == null,
			"an unknown token is not evidence about any family that does exist");
	}

	/// <summary>
	/// Two accounts, two chains. Obvious, and worth a test because <c>family_id</c> is a plain
	/// column rather than something the database constrains.
	/// </summary>
	[Fact]
	public async Task Refresh_ReuseOnOneAccount_LeavesOtherAccountsAlone()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse dave = await client.RegisterAsync("DaveSmith");
		TokenResponse sam = await client.RegisterAsync("SamJones");

		await RefreshAsync(client, dave.RefreshToken);

		app.Clock.Advance(RefreshTokenGraceCache.Window + TimeSpan.FromSeconds(1));

		(await PostRefreshAsync(client, dave.RefreshToken)).Dispose();

		await RefreshAsync(client, sam.RefreshToken);
	}

	/// <summary>
	/// Registering starts a session (§7.2), and a session needs somewhere to hang its token
	/// family. A device id from another account is not an error to report — it simply does not
	/// match, and this installation gets one of its own.
	/// </summary>
	[Fact]
	public async Task Session_DeviceIdBelongingToAnotherAccount_IsNotAdopted()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse dave = await client.RegisterAsync("DaveSmith");

		Guid daveDevice = await app.WithDatabaseAsync(async database =>
			await database.Set<Device>()
				.Where(device => device.UserId == dave.User.Id)
				.Select(device => device.Id)
				.SingleAsync());

		using HttpResponseMessage response = await client.PostAsJsonAsync(
			"/api/v1/auth/register",
			new RegisterRequest("SamJones", TestRegistration.ValidPassword, DeviceId: daveDevice));

		response.StatusCode.ShouldBe(HttpStatusCode.Created);

		TokenResponse sam = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;

		Guid samDevice = await app.WithDatabaseAsync(async database =>
			await database.Set<Device>()
				.Where(device => device.UserId == sam.User.Id)
				.Select(device => device.Id)
				.SingleAsync());

		samDevice.ShouldNotBe(daveDevice,
			"claiming a device id must never attach a session to somebody else's installation");
	}

	private static Task<HttpResponseMessage> PostRefreshAsync(HttpClient client, string refreshToken) =>
		client.PostAsJsonAsync(TokenUrl, new TokenRequest(GrantTypes.Refresh, RefreshToken: refreshToken));

	private static async Task<TokenResponse> RefreshAsync(HttpClient client, string refreshToken)
	{
		using HttpResponseMessage response = await PostRefreshAsync(client, refreshToken);

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
	}

	/// <summary>
	/// Every refresh token, in chain order: each family's head first, then along its
	/// successors.
	/// <para>
	/// Ordered by the links rather than by <c>issued_utc</c>, which is not a total order here.
	/// The fake clock does not tick unless a test moves it, so a token and the token it
	/// rotated into are issued at the same instant — and sorting on that leaves the tiebreak
	/// to a random primary key, which is a test that passes about half the time.
	/// </para>
	/// </summary>
	private static async Task<IReadOnlyList<RefreshToken>> ChainAsync(DlrWebApplicationFactory app)
	{
		List<RefreshToken> all = await app.WithDatabaseAsync(async database =>
			await database.Set<RefreshToken>().ToListAsync());

		HashSet<Guid> successors =
			[.. all.Where(token => token.SuccessorId is not null).Select(token => token.SuccessorId!.Value)];

		List<RefreshToken> ordered = [];

		foreach (RefreshToken head in all.Where(token => !successors.Contains(token.Id)))
		{
			for (RefreshToken? link = head; link is not null;)
			{
				ordered.Add(link);

				link = link.SuccessorId is { } next
					? all.SingleOrDefault(token => token.Id == next)
					: null;
			}
		}

		return ordered;
	}
}
