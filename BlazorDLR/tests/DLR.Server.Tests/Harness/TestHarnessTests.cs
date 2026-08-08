using DLR.Server.Identity;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.Server.Tests.Harness;

/// <summary>
/// The harness testing itself. Everything in Milestones B–F depends on these three
/// things working and on nothing else external, so they get assertions of their own
/// rather than being assumed by the first feature test that needs them.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TestHarnessTests(PostgresFixture postgres)
{
	[Fact]
	public async Task Database_Container_StartsAndAppliesMigrations()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		IReadOnlyList<string> applied = await app.WithDatabaseAsync(async database =>
			(IReadOnlyList<string>)[.. await database.Database.GetAppliedMigrationsAsync()]);

		IReadOnlyList<string> known = await app.WithDatabaseAsync(database =>
			Task.FromResult<IReadOnlyList<string>>([.. database.Database.GetMigrations()]));

		applied.ShouldNotBeEmpty(
			"a container that starts but applies no schema is a harness that will pass every " +
			"test until the first one asks for a table");
		applied.ShouldBe(known);
	}

	[Fact]
	public async Task Database_EachFactory_GetsItsOwnDatabase()
	{
		await using DlrWebApplicationFactory first = await DlrWebApplicationFactory.CreateAsync(postgres);
		await using DlrWebApplicationFactory second = await DlrWebApplicationFactory.CreateAsync(postgres);

		first.ConnectionString.ShouldNotBe(second.ConnectionString);
	}

	[Fact]
	public async Task Clock_IsTheFakeOne_AndAdvancesOnDemand()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		TimeProvider clock = app.Services.GetRequiredService<TimeProvider>();

		clock.GetUtcNow().ShouldBe(DlrWebApplicationFactory.DefaultStart);

		app.Clock.Advance(TimeSpan.FromHours(3));

		clock.GetUtcNow().ShouldBe(DlrWebApplicationFactory.DefaultStart.AddHours(3),
			"the server must resolve the same TimeProvider instance the test advances, or " +
			"every timing assertion in the project is testing a clock nobody uses");
	}

	/// <summary>
	/// The harness's real acceptance criterion: the 180-day inactivity sweep (§7.11) is
	/// verifiable in milliseconds, and what it sent is inspectable.
	/// </summary>
	[Fact]
	public async Task Email_CanBeAssertedOnAfterAdvancingTheClockSixMonths()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		app.Clock.Advance(TimeSpan.FromDays(180));

		IEmailSender sender = app.Services.GetRequiredService<IEmailSender>();
		TimeProvider clock = app.Services.GetRequiredService<TimeProvider>();

		await sender.SendAsync(new EmailMessage(
			To: "rider@example.com",
			Subject: "Your Dumb Luck Routes account",
			PlainTextBody: $"Nothing has happened here since {clock.GetUtcNow():yyyy-MM-dd}."));

		app.Emails.Sent.ShouldHaveSingleItem();
		app.Emails.To("rider@example.com").ShouldHaveSingleItem()
			.PlainTextBody.ShouldContain("2026-06-30");
	}

	[Fact]
	public async Task Email_FailWith_MakesTheTransportThrow()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		app.Emails.FailWith = new InvalidOperationException("transport is down");

		IEmailSender sender = app.Services.GetRequiredService<IEmailSender>();

		await Should.ThrowAsync<InvalidOperationException>(
			sender.SendAsync(new EmailMessage("rider@example.com", "subject", "body")));

		app.Emails.Sent.ShouldBeEmpty();
	}
}
