using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Server.Data;
using DLR.Server.Data.Identity;
using DLR.Server.Data.Rides;
using DLR.Server.Data.Tracks;
using DLR.Server.Maintenance;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Maintenance;

/// <summary>
/// The 180-day inactivity sweep (§7.11).
/// <para>
/// This is the only code in the project that deletes an account, and it does it on a timer with
/// nobody watching. The conjunction in §7.11 is the safety property — an account holding a single
/// saved ride is never touched — so every clause of it gets its own test, and the dry run gets the
/// first one.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CleanupTests(PostgresFixture postgres)
{
	/// <summary>The settings that make a run actually delete. Nothing in the suite gets them by default.</summary>
	private static Dictionary<string, string?> Live => new()
	{
		["Maintenance:DryRun"] = "false",
	};

	/// <summary>
	/// The first brake, and the first test. §7.11 says to run this way for a week and read the
	/// output before enabling deletion for real, so a dry run that deleted nothing and also said
	/// nothing would satisfy the letter of the switch and none of its purpose.
	/// </summary>
	[Fact]
	public async Task Cleanup_DryRunEnabled_DeletesNothingButLogsCandidates()
	{
		// The shipped default. Passed explicitly so the test still means something if the
		// default ever moves.
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: new Dictionary<string, string?> { ["Maintenance:DryRun"] = "true" });

		await DormantAccountAsync(app, "DaveSmith", idleDays: 200);

		MaintenanceReport report = await app.RunMaintenanceAsync();

		report.WasDryRun.ShouldBeTrue();
		report.AccountsDeleted.ShouldBe(0);

		(await ExistsAsync(app, "DaveSmith")).ShouldBeTrue("a dry run changes nothing");

		// Named, not counted. "Seven accounts would be deleted" is not something anybody can check.
		report.InactiveCandidates.Select(candidate => candidate.UserName).ShouldContain("DaveSmith");

		app.Logs.Mentions("DaveSmith").ShouldBeTrue(
			"the output is the whole point of the switch — an operator reads it for a week");
	}

	/// <summary>The sweep itself: nothing held, nothing heard, 180 days (§7.11).</summary>
	[Fact]
	public async Task Cleanup_EmptyAccountIdle180Days_IsDeleted()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: Live);

		await DormantAccountAsync(app, "DaveSmith", idleDays: 180);

		MaintenanceReport report = await app.RunMaintenanceAsync();

		report.AccountsDeleted.ShouldBe(1);

		(await ExistsAsync(app, "DaveSmith")).ShouldBeFalse();
	}

	/// <summary>
	/// Clause two, and the one §7.11 calls out by name: an account with a single saved ride is
	/// never touched, however long it has been quiet.
	/// </summary>
	[Fact]
	public async Task Cleanup_AccountWithOneTrack_IsNeverDeleted()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: Live);

		Guid userId = await DormantAccountAsync(app, "DaveSmith", idleDays: 3_650);

		await app.WithDatabaseAsync(async database =>
		{
			database.Add(new Track
			{
				Id = Guid.NewGuid(),
				OwnerId = userId,
				ClientGuid = Guid.NewGuid(),
				CreatedUtc = app.Clock.GetUtcNow(),
				BlobRef = new string('a', 32),
				ContentHash = [1, 2, 3],
			});

			await database.SaveChangesAsync();
		});

		MaintenanceReport report = await app.RunMaintenanceAsync();

		report.AccountsDeleted.ShouldBe(0);
		report.InactiveCandidates.ShouldBeEmpty("ten years idle does not outweigh one saved ride");

		(await ExistsAsync(app, "DaveSmith")).ShouldBeTrue();
	}

	/// <summary>Clause three. Being in somebody's ride means other riders have seen this name.</summary>
	[Fact]
	public async Task Cleanup_AccountWithRideMembership_IsNeverDeleted()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: Live);

		Guid organiserId = await DormantAccountAsync(app, "Organiser", idleDays: 1);
		Guid memberId = await DormantAccountAsync(app, "DaveSmith", idleDays: 200);

		Guid rideId = await RideAsync(app, organiserId);

		await app.WithDatabaseAsync(async database =>
		{
			database.Add(new GroupRideMember
			{
				GroupRideId = rideId,
				UserId = memberId,
				Role = GroupRideRole.Rider,
				JoinedUtc = app.Clock.GetUtcNow(),
			});

			await database.SaveChangesAsync();
		});

		await app.RunMaintenanceAsync();

		(await ExistsAsync(app, "DaveSmith")).ShouldBeTrue();
	}

	/// <summary>Clause four — and not covered by clause three, because an organiser could have left.</summary>
	[Fact]
	public async Task Cleanup_AccountOwningRide_IsNeverDeleted()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: Live);

		Guid organiserId = await DormantAccountAsync(app, "DaveSmith", idleDays: 200);

		Guid rideId = await RideAsync(app, organiserId);

		// The member row is removed, so the only thing keeping this account alive is that it owns
		// the ride. Without that clause, deleting the account would cascade the whole ride away
		// underneath every other rider in it.
		await app.WithDatabaseAsync(database =>
			database.Set<GroupRideMember>()
				.Where(member => member.GroupRideId == rideId)
				.ExecuteDeleteAsync());

		await app.RunMaintenanceAsync();

		(await ExistsAsync(app, "DaveSmith")).ShouldBeTrue();
	}

	/// <summary>
	/// Clause five, and the narrowest: <em>pending</em> only. A declined request is history the
	/// organiser keeps (§7.13), not a reason to keep an account nobody is using.
	/// </summary>
	[Fact]
	public async Task Cleanup_AccountWithPendingJoinRequest_IsNeverDeleted()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: Live);

		Guid organiserId = await DormantAccountAsync(app, "Organiser", idleDays: 1);
		Guid pendingId = await DormantAccountAsync(app, "Pending", idleDays: 200);
		Guid declinedId = await DormantAccountAsync(app, "Declined", idleDays: 200);

		Guid rideId = await RideAsync(app, organiserId);

		await app.WithDatabaseAsync(async database =>
		{
			database.Add(new GroupRideJoinRequest
			{
				Id = Guid.NewGuid(),
				GroupRideId = rideId,
				UserId = pendingId,
				Status = JoinRequestStatus.Pending,
				RequestedUtc = app.Clock.GetUtcNow(),
			});

			database.Add(new GroupRideJoinRequest
			{
				Id = Guid.NewGuid(),
				GroupRideId = rideId,
				UserId = declinedId,
				Status = JoinRequestStatus.Declined,
				RequestedUtc = app.Clock.GetUtcNow(),
				DecidedUtc = app.Clock.GetUtcNow(),
			});

			await database.SaveChangesAsync();
		});

		await app.RunMaintenanceAsync();

		(await ExistsAsync(app, "Pending")).ShouldBeTrue("somebody is still waiting on an answer");

		(await ExistsAsync(app, "Declined")).ShouldBeFalse(
			"the clause is Pending, not 'has ever asked' — otherwise one decline keeps an " +
			"account alive forever");
	}

	/// <summary>
	/// The boundary. Written because <c>&lt;=</c> instead of <c>&lt;</c> here deletes a day early
	/// and nothing else in the file notices.
	/// </summary>
	[Fact]
	public async Task Cleanup_IdleAccountAt179Days_IsNotDeleted()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: Live);

		await DormantAccountAsync(app, "DaveSmith", idleDays: 179);

		MaintenanceReport report = await app.RunMaintenanceAsync();

		report.AccountsDeleted.ShouldBe(0);

		(await ExistsAsync(app, "DaveSmith")).ShouldBeTrue();
	}

	/// <summary>Thirty days' notice, to the only address there is a way to send it to (§7.11).</summary>
	[Fact]
	public async Task Cleanup_At150Days_SendsWarningWhenEmailKnown()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: Live);

		await DormantAccountAsync(app, "DaveSmith", idleDays: 150, email: "dave@example.com");

		app.Emails.Clear();

		MaintenanceReport report = await app.RunMaintenanceAsync();

		report.AccountsWarned.ShouldBe(1);
		report.AccountsDeleted.ShouldBe(0, "150 days is a warning, not a deletion");

		app.Emails.To("dave@example.com").Count.ShouldBe(1);

		// The window is thirty days wide and the job runs nightly. Without a record that the
		// warning went, this is thirty emails — which reads as a broken service rather than a
		// courtesy, and is how a sending domain gets blocked.
		await app.RunMaintenanceAsync();
		await app.RunMaintenanceAsync();

		app.Emails.To("dave@example.com").Count.ShouldBe(1, "warned once, not once a night");
	}

	/// <summary>
	/// The gap §7.2's notice is about: without an address there is no way to warn, and the account
	/// goes at 180 days having been told nothing. The sweep must not invent a way to tell them.
	/// </summary>
	[Fact]
	public async Task Cleanup_At150Days_SendsNothingWhenNoEmail()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: Live);

		await DormantAccountAsync(app, "DaveSmith", idleDays: 150);

		app.Emails.Clear();

		MaintenanceReport report = await app.RunMaintenanceAsync();

		report.AccountsWarned.ShouldBe(0);
		app.Emails.Sent.ShouldBeEmpty();
	}

	/// <summary>
	/// The batch cap (§7.11). One run can never take a long lock — and a predicate that has gone
	/// wrong is bounded by how many nights it survives unnoticed rather than by how many accounts
	/// there are.
	/// </summary>
	[Fact]
	public async Task Cleanup_RespectsMaxDeletesPerRun()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: new Dictionary<string, string?>
			{
				["Maintenance:DryRun"] = "false",
				["Maintenance:MaxDeletesPerRun"] = "2",
			});

		await DormantAccountAsync(app, "RiderOne", idleDays: 200);
		await DormantAccountAsync(app, "RiderTwo", idleDays: 200);
		await DormantAccountAsync(app, "RiderThree", idleDays: 200);

		MaintenanceReport first = await app.RunMaintenanceAsync();

		first.AccountsDeleted.ShouldBe(2);
		(await CountUsersAsync(app)).ShouldBe(1);

		// And the third goes the next night, rather than being stranded by the cap.
		MaintenanceReport second = await app.RunMaintenanceAsync();

		second.AccountsDeleted.ShouldBe(1);
		(await CountUsersAsync(app)).ShouldBe(0);
	}

	/// <summary>
	/// §7.2 says a username can never be changed; §7.11 says a deleted one goes back to the pool.
	/// Those are consistent only because an eligible account has never joined a ride, so no rider
	/// can have formed an association with the name — there is nothing to inherit or impersonate.
	/// </summary>
	[Fact]
	public async Task Cleanup_ReleasesUsernameForReuse()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: Live);

		await DormantAccountAsync(app, "DaveSmith", idleDays: 200);

		await app.RunMaintenanceAsync();

		using HttpClient client = app.CreateClient().From("203.0.113.90");

		using HttpResponseMessage response = await client.PostRegisterAsync("DaveSmith");

		response.StatusCode.ShouldBe(
			HttpStatusCode.Created,
			"a hard delete releases the name; a soft one would have kept it reserved forever");
	}

	/// <summary>
	/// What the device sees afterwards (§7.11). "Something went wrong" is indistinguishable from a
	/// bug and from a bad password; the client has to be able to say what actually happened and
	/// offer to make a new account.
	/// </summary>
	[Fact]
	public async Task Cleanup_DeletedAccountRefresh_ReturnsDistinguishableReason()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: Live);

		using HttpClient client = app.CreateClient().From("203.0.113.91");

		TokenResponse session = await client.RegisterAsync("DaveSmith");

		await IdleAsync(app, "DaveSmith", days: 200);

		await app.RunMaintenanceAsync();

		using HttpResponseMessage refused = await client.PostAsJsonAsync(
			"/api/v1/auth/token",
			new TokenRequest(GrantTypes.Refresh, RefreshToken: session.RefreshToken));

		refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		string body = await refused.Content.ReadAsStringAsync();

		body.ShouldContain(
			"deleted",
			Case.Insensitive,
			"the app says \"this account was removed after 180 days without use\", which it " +
			"cannot do from a generic sign-in failure");
	}

	/// <summary>
	/// §7.8's address is kept long enough to be useful for throttling and no longer (§7.11). Null
	/// therefore means "not recorded any more", not "unknown".
	/// </summary>
	[Fact]
	public async Task Cleanup_NullsRegistrationIpAfter30Days()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: Live);

		using HttpClient older = app.CreateClient().From("203.0.113.10");
		await older.RegisterAsync("OldAccount");

		// Far enough that the older account is past retention and the newer one is not, in one run.
		app.Clock.Advance(TimeSpan.FromDays(31));

		using HttpClient newer = app.CreateClient().From("203.0.113.11");
		await newer.RegisterAsync("NewAccount");

		MaintenanceReport report = await app.RunMaintenanceAsync();

		report.RegistrationIpsCleared.ShouldBe(1);

		(await RegistrationIpAsync(app, "OldAccount")).ShouldBeNull();

		(await RegistrationIpAsync(app, "NewAccount")).ShouldNotBeNull(
			"the ladder still needs it — clearing it early breaks §7.8 rather than tidying it");
	}

	/// <summary>
	/// The kill switch (§7.11): account deletion off, everything else still running. That is the
	/// distinction from <c>DryRun</c>, and it is the setting an operator reaches for at 3 a.m. when
	/// the predicate has done something surprising and the disk still needs collecting.
	/// </summary>
	[Fact]
	public async Task Cleanup_KillSwitchOff_LeavesAccountsButStillTidiesUp()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: new Dictionary<string, string?>
			{
				["Maintenance:DryRun"] = "false",
				["Maintenance:DeleteInactiveAccounts"] = "false",
			});

		using HttpClient client = app.CreateClient().From("203.0.113.12");
		await client.RegisterAsync("DaveSmith");

		// Real elapsed time, not a backdated column: created_by_ip is aged against created_utc,
		// which registration stamps from the clock.
		app.Clock.Advance(TimeSpan.FromDays(31));

		await IdleAsync(app, "DaveSmith", days: 200);

		MaintenanceReport report = await app.RunMaintenanceAsync();

		report.AccountsDeleted.ShouldBe(0);
		(await ExistsAsync(app, "DaveSmith")).ShouldBeTrue();

		report.RegistrationIpsCleared.ShouldBe(1, "the other sweeps are not gated by this switch");
	}

	/// <summary>
	/// The shipped defaults, asserted separately because every other test in this file overrides
	/// them. A default that quietly became <c>DryRun = false</c> would make the whole suite pass.
	/// </summary>
	[Fact]
	public void Maintenance_ShippedDefaults_DeleteNothing()
	{
		MaintenanceOptions defaults = new();

		defaults.DryRun.ShouldBeTrue();
		defaults.MaxDeletesPerRun.ShouldBe(500);
		defaults.InactiveDays.ShouldBe(180);
		defaults.WarnAfterDays.ShouldBe(150);
	}

	/// <summary>Creates an account and backdates it, without spending 200 days of fake clock.</summary>
	private static async Task<Guid> DormantAccountAsync(
		DlrWebApplicationFactory app,
		string userName,
		int idleDays,
		string? email = null)
	{
		// A fresh client per account: the §7.8 ladder counts registrations per address, and the
		// fourth from one address is asked for an email it does not have.
		using HttpClient client = app.CreateClient().From($"198.51.100.{Random.Shared.Next(1, 250)}");

		await client.RegisterAsync(userName, email: email);

		return await IdleAsync(app, userName, idleDays, email is not null);
	}

	/// <summary>
	/// Backdates an account's activity — and confirms its address, when it has one.
	/// <para>
	/// <strong>A minute past the day, not exactly on it.</strong> §7.11's predicate is
	/// <c>last_active_utc &lt; now() - 180 days</c>, strictly, so an account last active at exactly
	/// the horizon has not yet been idle for 180 days — it is at 180 days. "Idle for <em>at least</em>
	/// N days" is what these tests mean, and the 179-day test is what pins the boundary from the
	/// other side.
	/// </para>
	/// </summary>
	private static Task<Guid> IdleAsync(
		DlrWebApplicationFactory app,
		string userName,
		int days,
		bool confirmEmail = false) =>
		app.WithDatabaseAsync(async database =>
		{
			AppUser user = await database.Users.SingleAsync(row => row.UserName == userName);

			user.LastActiveUtc = app.Clock.GetUtcNow().AddDays(-days).AddMinutes(-1);

			// §7.11 warns "if a *confirmed* address is known". An address that was typed and never
			// confirmed may belong to somebody who mistyped it, which is the same reason §7.7
			// refuses to send a reset to one.
			user.EmailConfirmed = confirmEmail;

			await database.SaveChangesAsync();

			return user.Id;
		});

	private static Task<Guid> RideAsync(DlrWebApplicationFactory app, Guid ownerId) =>
		app.WithDatabaseAsync(async database =>
		{
			GroupRide ride = new()
			{
				Id = Guid.NewGuid(),
				OwnerId = ownerId,
				Name = "Sunday",
				StartUtc = app.Clock.GetUtcNow(),
				State = GroupRideState.Open,
				JoinCode = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
				CreatedUtc = app.Clock.GetUtcNow(),
			};

			database.Add(ride);

			database.Add(new GroupRideMember
			{
				GroupRideId = ride.Id,
				UserId = ownerId,
				Role = GroupRideRole.Owner,
				JoinedUtc = app.Clock.GetUtcNow(),
			});

			await database.SaveChangesAsync();

			return ride.Id;
		});

	private static Task<bool> ExistsAsync(DlrWebApplicationFactory app, string userName) =>
		app.WithDatabaseAsync(database =>
			database.Users.AnyAsync(user => user.UserName == userName));

	private static Task<int> CountUsersAsync(DlrWebApplicationFactory app) =>
		app.WithDatabaseAsync(database => database.Users.CountAsync());

	private static Task<string?> RegistrationIpAsync(DlrWebApplicationFactory app, string userName) =>
		app.WithDatabaseAsync(async database =>
		{
			AppUser user = await database.Users
				.AsNoTracking()
				.SingleAsync(row => row.UserName == userName);

			return user.CreatedByIp?.ToString();
		});
}
