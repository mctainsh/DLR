using DLR.Core.Contracts.Announcements;
using DLR.Core.Contracts.Identity;
using DLR.Server.Hubs;
using DLR.Server.Tests.Hubs;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.AspNetCore.SignalR.Client;

namespace DLR.Server.Tests.Announcements;

/// <summary>
/// Sending an announcement the moment it goes live (§20.3).
/// <para>
/// This is the half of the feature that makes it worth having: a rider who has had the app open
/// for an hour is exactly the person who needs to hear that the server goes down in ten minutes,
/// and a launch-time-only channel would reach everybody except them.
/// </para>
/// <para>
/// The connection is a plain hub connection with no <c>JoinRide</c> - deliberately. An
/// announcement belongs to the server, not to an adventure, so a rider who is in no ride at all
/// must still receive it.
/// </para>
/// </summary>
public sealed class AnnouncementBroadcastTests(PostgresFixture postgres)
{
	private static readonly TimeSpan Arrives = TimeSpan.FromSeconds(10);

	private static readonly TimeSpan StaysAway = TimeSpan.FromSeconds(2);

	[Fact]
	public async Task OneThatHasJustGoneLive_ReachesARiderWhoIsInNoRide()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		TokenResponse session = await SignedInAsync(app);

		await using HubConnection hub = await HubClient.ConnectAsync(app, session);

		Task<AnnouncementDto> heard = NextAsync(hub);

		// A minute into the future, then the clock moves past it: the sweep sends what became live
		// since it last looked, so a row has to cross that edge rather than merely exist.
		DateTimeOffset from = app.Clock.GetUtcNow().AddMinutes(1);
		await Notices.WriteAsync(app, "Server restart at nine", from, severity: NoticeSeverity.Warning);

		app.Clock.Advance(TimeSpan.FromMinutes(2));
		await app.FlushAnnouncementsAsync();

		AnnouncementDto announcement = await heard.WaitAsync(Arrives);

		announcement.Title.ShouldBe("Server restart at nine");
		announcement.Severity.ShouldBe(NoticeSeverity.Warning);
	}

	[Fact]
	public async Task ItIsSentOnce_HoweverManyTimesTheSweepRuns()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		TokenResponse session = await SignedInAsync(app);

		await using HubConnection hub = await HubClient.ConnectAsync(app, session);

		List<AnnouncementDto> heard = [];
		hub.On<AnnouncementDto>(nameof(IRideClient.AnnouncementPosted), announcement =>
		{
			lock (heard) heard.Add(announcement);
		});

		await Notices.WriteAsync(app, "Only once", app.Clock.GetUtcNow().AddMinutes(1));

		app.Clock.Advance(TimeSpan.FromMinutes(2));
		await app.FlushAnnouncementsAsync();

		// Long enough for a second copy to have turned up if one were coming.
		app.Clock.Advance(TimeSpan.FromMinutes(2));
		await app.FlushAnnouncementsAsync();
		await Task.Delay(StaysAway);

		lock (heard)
		{
			heard.Count.ShouldBe(
				1,
				"the swept window is (last tick, now], which is what makes the send once-only "
					+ "without a column to mark");
		}
	}

	[Fact]
	public async Task OneStillAheadOfItsPublishFrom_IsNotSent()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		TokenResponse session = await SignedInAsync(app);

		await using HubConnection hub = await HubClient.ConnectAsync(app, session);

		Task<AnnouncementDto> leaked = NextAsync(hub);

		await Notices.WriteAsync(app, "Next Friday", app.Clock.GetUtcNow().AddDays(3));

		app.Clock.Advance(TimeSpan.FromMinutes(2));
		await app.FlushAnnouncementsAsync();

		await Should.ThrowAsync<TimeoutException>(() => leaked.WaitAsync(StaysAway));
	}

	[Fact]
	public async Task OneWrittenBeforeTheServerStarted_IsLeftToTheLaunchCheck()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		// Already live when the sweep's window opens at the boot instant. Sending it would mean
		// every restart re-blasting everything published while the process was down.
		await Notices.WriteAsync(app, "From before", app.Clock.GetUtcNow().AddHours(-1));

		TokenResponse session = await SignedInAsync(app);

		await using HubConnection hub = await HubClient.ConnectAsync(app, session);

		Task<AnnouncementDto> leaked = NextAsync(hub);

		app.Clock.Advance(TimeSpan.FromMinutes(2));
		await app.FlushAnnouncementsAsync();

		await Should.ThrowAsync<TimeoutException>(() => leaked.WaitAsync(StaysAway));
	}

	[Fact]
	public async Task OneThatWentLiveAndExpiredBetweenTicks_IsNotSent()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		TokenResponse session = await SignedInAsync(app);

		await using HubConnection hub = await HubClient.ConnectAsync(app, session);

		Task<AnnouncementDto> leaked = NextAsync(hub);

		DateTimeOffset from = app.Clock.GetUtcNow().AddMinutes(1);

		await Notices.WriteAsync(app, "Gone already", from, TimeSpan.FromMinutes(1));

		app.Clock.Advance(TimeSpan.FromMinutes(10));
		await app.FlushAnnouncementsAsync();

		await Should.ThrowAsync<TimeoutException>(
			() => leaked.WaitAsync(StaysAway),
			"a message nobody could still act on should not arrive at all");
	}

	private static Task<AnnouncementDto> NextAsync(HubConnection hub)
	{
		TaskCompletionSource<AnnouncementDto> next =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		hub.On<AnnouncementDto>(
			nameof(IRideClient.AnnouncementPosted),
			announcement => next.TrySetResult(announcement));

		return next.Task;
	}

	private static async Task<TokenResponse> SignedInAsync(DlrWebApplicationFactory app)
	{
		using HttpClient registrar = app.CreateClient();

		return await registrar.RegisterAsync("DaveSmith");
	}
}
