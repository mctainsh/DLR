using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DLR.Core.Contracts.Identity;
using DLR.Server.Data.Identity;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Identity;

/// <summary>
/// Registration, and the username rules that make a handle safe to draw on a map (§7.2).
/// <para>
/// One field is the account: <c>UserName</c> is the login identifier and the label other
/// riders read, so every rule here is doing two jobs at once. That is why the casing, the
/// character set and the reserved list get tests rather than a paragraph - each one is a way
/// for a stranger's pin to be mistaken for someone you know.
/// </para>
/// </summary>
public sealed class RegistrationTests(PostgresFixture postgres)
{
	[Fact]
	public async Task Register_UsernameAndPasswordOnly_Succeeds()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response = await client.PostRegisterAsync("DaveSmith");

		response.StatusCode.ShouldBe(HttpStatusCode.Created);

		TokenResponse? account = await response.Content.ReadFromJsonAsync<TokenResponse>();

		account.ShouldNotBeNull();
		account.User.Id.ShouldNotBe(Guid.Empty);
		account.User.UserName.ShouldBe("DaveSmith");
		account.User.HasEmail.ShouldBeFalse();

		AppUser? stored = await app.WithDatabaseAsync(database =>
			database.Users.SingleOrDefaultAsync(user => user.Id == account.User.Id));

		stored.ShouldNotBeNull();
	}

	/// <summary>
	/// An account with no email address is not a lesser account. Nothing about it is pending,
	/// restricted or awaiting a step - the address buys recovery (§7.7) and satisfies the IP
	/// ladder (§7.8), and it buys nothing else.
	/// </summary>
	[Fact]
	public async Task Register_NoEmail_AccountIsFullyUsable()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse account = await client.RegisterAsync("NoEmailNed");

		account.User.HasEmail.ShouldBeFalse();

		AppUser stored = await app.WithDatabaseAsync(async database =>
			await database.Users.SingleAsync(user => user.Id == account.User.Id));

		stored.Email.ShouldBeNull();
		stored.NormalizedEmail.ShouldBeNull(
			"an empty string here would be a value, and the unique index would then let " +
			"exactly one account exist without an email address");

		stored.PasswordHash.ShouldNotBeNullOrWhiteSpace(
			"the password is the only credential this account will ever have");
		stored.LockoutEnd.ShouldBeNull();
		stored.EmailConfirmed.ShouldBeFalse(
			"there is nothing to confirm, and confirmation is never a gate on signing in");
	}

	[Fact]
	public async Task Register_DuplicateUsername_IsRejectedCaseInsensitively()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		await client.RegisterAsync("DaveSmith");

		using HttpResponseMessage second = await client.PostRegisterAsync("davesmith");

		second.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		// Enumeration is accepted here and nowhere else (§7.2). Uniqueness means
		// registration cannot avoid saying whether a name is taken, and the name is a public
		// handle drawn on a map rather than a private identifier.
		JsonElement problem = await second.Content.ReadFromJsonAsync<JsonElement>();

		problem.GetProperty("errors").TryGetProperty(nameof(RegisterRequest.UserName), out _)
			.ShouldBeTrue("the caller has to be told which field to change");
	}

	[Theory]
	[InlineData("DAVESMITH")]
	[InlineData("davesmith")]
	[InlineData("davESmith")]
	[InlineData("DaveSmitH")]
	public async Task Register_UsernameDifferingOnlyByCase_IsRejected(string variant)
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		await client.RegisterAsync("DaveSmith");

		using HttpResponseMessage response = await client.PostRegisterAsync(variant);

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
			$"'{variant}' and 'DaveSmith' are the same name to anyone glancing at a pin");

		int accounts = await app.WithDatabaseAsync(database => database.Users.CountAsync());

		accounts.ShouldBe(1);
	}

	/// <summary>
	/// Case is preserved; uniqueness is not case-sensitive. The stored string is what the
	/// rider typed, because it is the string everyone else reads.
	/// </summary>
	[Fact]
	public async Task Register_MixedCaseUsername_IsStoredAndReturnedAsTyped()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		TokenResponse account = await client.RegisterAsync("DaveSmith");

		account.User.UserName.ShouldBe("DaveSmith");

		AppUser stored = await app.WithDatabaseAsync(async database =>
			await database.Users.SingleAsync(user => user.Id == account.User.Id));

		stored.UserName.ShouldBe("DaveSmith");
		stored.NormalizedUserName.ShouldBe("DAVESMITH",
			"the normalised form is what the unique index sees, and it is not what is rendered");
	}

	/// <summary>
	/// ASCII-only is a security rule, not a simplification: the handle is unique <em>and</em>
	/// visible, so a homoglyph is an impersonation that survives a glance at 60 km/h.
	/// </summary>
	[Theory]
	[InlineData("DaveSmıth", "a dotless i reads as an i on a map pin")]
	[InlineData("Dаve", "the a here is Cyrillic")]
	[InlineData("Дейв", "outside ASCII entirely")]
	[InlineData("Dave Smith", "a space is not in the permitted set")]
	[InlineData("Dave@Smith", "@ is in Identity's default charset and not in §7.2's")]
	[InlineData("Dave+Smith", "+ is in Identity's default charset and not in §7.2's")]
	[InlineData("Dave/Smith", "would have to be escaped everywhere it is rendered")]
	public async Task Register_NonAsciiUsername_IsRejected(string userName, string why)
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response = await client.PostRegisterAsync(userName);

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, why);

		int accounts = await app.WithDatabaseAsync(database => database.Users.CountAsync());

		accounts.ShouldBe(0);
	}

	/// <summary>
	/// Nothing may pose as the service on somebody else's map - the same problem as a
	/// homoglyph, arriving by a different route.
	/// </summary>
	[Theory]
	[InlineData("admin")]
	[InlineData("Support")]
	[InlineData("no-reply")]
	[InlineData("dlr")]
	[InlineData("SYSTEM")]
	public async Task Register_ReservedUsername_IsRejected(string userName)
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response = await client.PostRegisterAsync(userName);

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
			"the reserved list is matched case-insensitively, or it is decoration");

		int accounts = await app.WithDatabaseAsync(database => database.Users.CountAsync());

		accounts.ShouldBe(0);
	}

	/// <summary>
	/// The one rule §7.2 states that §7.15 does not name a test for. It is here rather than
	/// absent because a rule the build does not check is a rule that leaves with the next
	/// refactor.
	/// </summary>
	[Theory]
	[InlineData("D")]
	[InlineData("Da")]
	[InlineData("ThisNameIsTwentyOneChars")]
	public async Task Register_UsernameOutsideLengthBounds_IsRejected(string userName)
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		using HttpResponseMessage response = await client.PostRegisterAsync(userName);

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}

	/// <summary>
	/// The reason <c>RequireUniqueEmail</c> has to stay off (§7.13). Identity's validator
	/// rejects a null address when it is on, and an account without one is the normal case -
	/// so uniqueness is a partial index, over the rows that have something to be unique about.
	/// </summary>
	[Fact]
	public async Task Register_NullEmails_DoNotCollideOnUniqueIndex()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = app.CreateClient();

		await client.RegisterAsync("RiderOne");
		await client.RegisterAsync("RiderTwo");
		await client.RegisterAsync("RiderThree");

		int withoutEmail = await app.WithDatabaseAsync(database =>
			database.Users.CountAsync(user => user.NormalizedEmail == null));

		withoutEmail.ShouldBe(3);

		// Three nulls coexisting is not by itself evidence of a correct index - PostgreSQL
		// treats NULLs as distinct even in an unfiltered unique index, and would allow all
		// three if the constraint had never been created at all. So the index is asserted
		// directly. What happens to a caller who submits an address someone else already
		// holds is a policy question with an enumeration trap in it, and §7.8 answers it in
		// SRV-12; this is only the constraint.
		string definition = await app.WithDatabaseAsync(async database =>
			await database.Database
				.SqlQueryRaw<string>(
					"""SELECT indexdef AS "Value" FROM pg_indexes WHERE indexname = 'ux_users_email'""")
				.SingleAsync());

		definition.ShouldContain("UNIQUE");
		definition.Contains("normalized_email IS NOT NULL", StringComparison.Ordinal).ShouldBeTrue(
			"the index covers the rows that have an address to be unique about, and PostgreSQL's " +
			"NULLs-are-distinct default is per-index rather than something to rely on");
	}
}
