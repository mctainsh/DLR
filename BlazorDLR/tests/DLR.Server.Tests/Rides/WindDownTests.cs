using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
using DLR.Server.Data.Positions;
using DLR.Server.Data.Rides;
using DLR.Server.Positions;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DLR.Server.Tests.Rides;

/// <summary>
/// The end of a ride is a choice, not an event (§5.6).
/// <para>
/// The naive rule — ride ends, all sharing stops, all positions deleted — has a real failure mode:
/// an organiser who ends the ride at the pub blanks the map while three riders are still an hour
/// from home in the dark. The wind-down fixes that, and four rules stop it becoming a loophole.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class WindDownTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";

	/// <summary>
	/// Rule 1, and the whole point of the task. The cap is server-side and unconditional — a
	/// bounded window that depends on a client to honour it is an unbounded window, and a flat
	/// battery must not leave somebody broadcasting.
	/// </summary>
	[Fact]
	public async Task RideEnd_WindDown_ExpiresServerSideWithoutAnyClient()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await LiveSharingRideAsync(app, organiser);

		await EndAsync(organiser, ride.Id, RideEndingDto.WindDown);

		await app.FlushPositionsAsync();

		(await CountPositionsAsync(app)).ShouldBe(1, "the wind-down keeps them on the map");

		// Nobody touches the app again. No request, no reconnect, no client of any kind — the
		// phone is in a pocket, or flat.
		app.Clock.Advance(TimeSpan.FromMinutes(121));

		await SweepAsync(app);

		(await CountPositionsAsync(app)).ShouldBe(
			0,
			"at the deadline the server force-stops sharing for everyone still on");

		bool stillSharing = await app.WithDatabaseAsync(database =>
			database.Set<GroupRideMember>()
				.AnyAsync(member => member.GroupRideId == ride.Id && member.ShareLocation));

		stillSharing.ShouldBeFalse();

		// And the window is gone rather than merely passed, so nothing can reopen it.
		DateTimeOffset? ends = await app.WithDatabaseAsync(database =>
			database.Set<GroupRide>()
				.Where(row => row.Id == ride.Id)
				.Select(row => row.SharingEndsUtc)
				.SingleAsync());

		ends.ShouldBeNull();
	}

	/// <summary>The default, and the one that is offered first (§5.6).</summary>
	[Fact]
	public async Task RideEnd_DefaultChoice_DeletesAllPositionsImmediately()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await LiveSharingRideAsync(app, organiser);

		await app.FlushPositionsAsync();

		(await CountPositionsAsync(app)).ShouldBe(1);

		// No argument at all — the default ending is the default in the contract too.
		using HttpResponseMessage ended = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/ending",
			new EndRideRequest());

		ended.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		(await CountPositionsAsync(app)).ShouldBe(0, "immediately, not at a deadline");

		DateTimeOffset? ends = await app.WithDatabaseAsync(database =>
			database.Set<GroupRide>()
				.Where(row => row.Id == ride.Id)
				.Select(row => row.SharingEndsUtc)
				.SingleAsync());

		ends.ShouldBeNull("no window was granted");
	}

	/// <summary>
	/// The wind-down is not a slow stop — it is continued sharing. Members who were sharing keep
	/// publishing, which is the actual use: the organiser at home wants to see everyone else got
	/// home too.
	/// </summary>
	[Fact]
	public async Task RideEnd_WindDown_KeepsSharingMembersPublishing()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await LiveSharingRideAsync(app, organiser);

		await JoinAsync(rider, ride.JoinCode!);
		await ShareAsync(rider, ride.Id, share: true);

		await EndAsync(organiser, ride.Id, RideEndingDto.WindDown);

		app.Clock.Advance(TimeSpan.FromMinutes(30));

		// The access token lasts fifteen minutes (§7.4), so half an hour of fake time has expired
		// it. Signing in again is what the rider's client would do, and doing it here keeps the
		// test about the wind-down rather than about token lifetimes.
		await ReauthenticateAsync(app, rider, "SamJones");
		await ReauthenticateAsync(app, organiser, "DaveSmith");

		// Still riding home, still publishing, an hour and a half inside the window.
		PublishResult published = await PublishAsync(rider, -33.90, 151.25);

		published.RideIds.ShouldBe([ride.Id], "a completed adventure inside its window still takes fixes");

		List<RiderPositionDto> visible =
			(await organiser.GetFromJsonAsync<List<RiderPositionDto>>(
				$"{RidesUrl}/{ride.Id}/positions"))!;

		visible.ShouldContain(position => position.UserName == "SamJones");
	}

	/// <summary>
	/// Rule 2. No renewal, no "add another hour" — a window that can be extended is an indefinite
	/// window with extra steps, and indefinite is what §1 promises this app never does.
	/// </summary>
	[Fact]
	public async Task RideEnd_WindDown_CannotBeExtended()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await LiveSharingRideAsync(app, organiser);

		await EndAsync(organiser, ride.Id, RideEndingDto.WindDown);

		DateTimeOffset? first = await EndsAtAsync(app, ride.Id);

		first.ShouldNotBeNull();

		app.Clock.Advance(TimeSpan.FromMinutes(60));

		await ReauthenticateAsync(app, organiser, "DaveSmith");

		using HttpResponseMessage again = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/ending",
			new EndRideRequest(RideEndingDto.WindDown));

		again.StatusCode.ShouldBe(HttpStatusCode.Conflict);

		(await EndsAtAsync(app, ride.Id)).ShouldBe(
			first,
			"a refused extension must not quietly move the deadline anyway");
	}

	/// <summary>Rule 4's other half: the organiser can end it early for everyone (§5.6).</summary>
	[Fact]
	public async Task RideEnd_WindDown_OrganiserCanEndItEarlyForEveryone()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await LiveSharingRideAsync(app, organiser);

		await JoinAsync(rider, ride.JoinCode!);
		await ShareAsync(rider, ride.Id, share: true);
		await PublishAsync(rider, -33.87, 151.21);

		await EndAsync(organiser, ride.Id, RideEndingDto.WindDown);

		await app.FlushPositionsAsync();

		(await CountPositionsAsync(app)).ShouldBe(2);

		app.Clock.Advance(TimeSpan.FromMinutes(10));

		// Everybody is home. Stop it now rather than in another hour and fifty minutes.
		using HttpResponseMessage stopped = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/ending",
			new EndRideRequest(RideEndingDto.Immediate));

		stopped.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		(await CountPositionsAsync(app)).ShouldBe(0);

		(await EndsAtAsync(app, ride.Id)).ShouldBeNull();

		// And nobody can publish back into it.
		(await PublishAsync(rider, -33.88, 151.22)).RideIds.ShouldBeEmpty();
	}

	/// <summary>
	/// Rule 4. A rider can stop at any point, and it deletes their row exactly as in the live case
	/// — without ending anybody else's (§5.6).
	/// </summary>
	[Fact]
	public async Task RideEnd_WindDown_RiderStoppingDeletesOnlyTheirRow()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await LiveSharingRideAsync(app, organiser);

		await JoinAsync(rider, ride.JoinCode!);
		await ShareAsync(rider, ride.Id, share: true);
		await PublishAsync(rider, -33.87, 151.21);

		await EndAsync(organiser, ride.Id, RideEndingDto.WindDown);

		await app.FlushPositionsAsync();

		(await CountPositionsAsync(app)).ShouldBe(2);

		// One rider is home and turns it off.
		await ShareAsync(rider, ride.Id, share: false);

		(await CountPositionsAsync(app)).ShouldBe(1, "only theirs");

		Guid riderId = await IdOfAsync(app, "SamJones");

		bool theirsGone = !await app.WithDatabaseAsync(database =>
			database.Set<RiderPosition>().AnyAsync(position => position.UserId == riderId));

		theirsGone.ShouldBeTrue();

		// The window itself is untouched — one rider stopping does not end it for the others.
		(await EndsAtAsync(app, ride.Id)).ShouldNotBeNull();
	}

	/// <summary>
	/// §5.5 rule 1's second half. A restart during a wind-down must not blank the map for the
	/// riders it exists to protect.
	/// </summary>
	[Fact]
	public async Task Rehydrate_RideInUnexpiredWindDown_IsLoaded()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await LiveSharingRideAsync(app, organiser);

		await EndAsync(organiser, ride.Id, RideEndingDto.WindDown);

		await app.FlushPositionsAsync();

		RiderPositionCache cache = await RehydrateAsync(app, app.Clock.GetUtcNow().AddMinutes(30));

		cache.ForRide(ride.Id).ShouldHaveSingleItem();
	}

	/// <summary>And must not resurrect one that has expired.</summary>
	[Fact]
	public async Task Rehydrate_RideInExpiredWindDown_IsNotLoaded()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await LiveSharingRideAsync(app, organiser);

		await EndAsync(organiser, ride.Id, RideEndingDto.WindDown);

		await app.FlushPositionsAsync();

		// Past the deadline, and the sweep has not run yet — a process that died before it could.
		// The rehydrator must not put the ride back on the map in that gap.
		RiderPositionCache cache = await RehydrateAsync(app, app.Clock.GetUtcNow().AddMinutes(121));

		cache.ForRide(ride.Id).ShouldBeEmpty();
	}

	/// <summary>
	/// A rider who was <em>not</em> sharing when the ride ended does not start because of the
	/// wind-down. It continues consent; it does not grant it.
	/// </summary>
	[Fact]
	public async Task RideEnd_WindDown_DoesNotStartSharingForSomebodyWhoHadItOff()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient watcher = await SignedInAsync(app, "SamJones");

		RideDetail ride = await LiveSharingRideAsync(app, organiser);

		await JoinAsync(watcher, ride.JoinCode!);

		await EndAsync(organiser, ride.Id, RideEndingDto.WindDown);

		(await PublishAsync(watcher, -33.90, 151.25)).RideIds.ShouldBeEmpty();
	}

	private static async Task<RiderPositionCache> RehydrateAsync(
		DlrWebApplicationFactory app,
		DateTimeOffset now)
	{
		FakeTimeProvider clock = new(now);

		RiderPositionCache cache = new(clock);

		PositionCacheRehydrator rehydrator = new(
			cache,
			app.Services.GetRequiredService<IServiceScopeFactory>(),
			clock,
			Options.Create(new RideOptions { StalenessMinutes = 24 * 60 }),
			NullLogger<PositionCacheRehydrator>.Instance);

		await rehydrator.RehydrateAsync();

		return cache;
	}

	/// <summary>
	/// Refreshes a client's bearer token after the clock has moved past its fifteen minutes.
	/// </summary>
	private static async Task ReauthenticateAsync(
		DlrWebApplicationFactory app,
		HttpClient client,
		string userName)
	{
		using HttpClient anonymous = app.CreateClient();

		client.Authenticated(await anonymous.SignInAsync(userName));
	}

	private static Task SweepAsync(DlrWebApplicationFactory app) =>
		app.Services.GetRequiredService<SharingWindDownService>().SweepAsync(CancellationToken.None);

	private static Task<DateTimeOffset?> EndsAtAsync(DlrWebApplicationFactory app, Guid rideId) =>
		app.WithDatabaseAsync(database => database.Set<GroupRide>()
			.Where(row => row.Id == rideId)
			.Select(row => row.SharingEndsUtc)
			.SingleAsync());

	private static Task<int> CountPositionsAsync(DlrWebApplicationFactory app) =>
		app.WithDatabaseAsync(database => database.Set<RiderPosition>().CountAsync());

	private static Task<Guid> IdOfAsync(DlrWebApplicationFactory app, string userName) =>
		app.WithDatabaseAsync(database => database.Users
			.Where(user => user.UserName == userName)
			.Select(user => user.Id)
			.SingleAsync());

	private static async Task EndAsync(HttpClient organiser, Guid rideId, RideEndingDto ending)
	{
		using HttpResponseMessage response = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{rideId}/ending",
			new EndRideRequest(ending));

		response.StatusCode.ShouldBe(
			HttpStatusCode.NoContent,
			await response.Content.ReadAsStringAsync());
	}

	private static async Task<PublishResult> PublishAsync(HttpClient client, double lat, double lon)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			"/api/v1/positions",
			new PositionUpdate(
				PositionScale.FromDegrees(lat),
				PositionScale.FromDegrees(lon),
				DlrWebApplicationFactory.DefaultStart.AddDays(1)));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<PublishResult>())!;
	}

	private static async Task ShareAsync(HttpClient client, Guid rideId, bool share)
	{
		using HttpResponseMessage response = await client.PutAsJsonAsync(
			$"{RidesUrl}/{rideId}/sharing/me",
			new SetSharingRequest(share));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
	}

	private static async Task JoinAsync(HttpClient client, string code)
	{
		using HttpResponseMessage response =
			await client.PostAsJsonAsync($"{RidesUrl}/join", new JoinByCodeRequest(code));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
	}

	/// <summary>A live ride whose organiser is sharing and has published once.</summary>
	private static async Task<RideDetail> LiveSharingRideAsync(
		DlrWebApplicationFactory app,
		HttpClient organiser)
	{
		using HttpResponseMessage created = await organiser.PostAsJsonAsync(
			RidesUrl,
			new CreateRideRequest(
				"Saturday hills",
				DlrWebApplicationFactory.DefaultStart.AddDays(3),
				JoinPolicy: JoinPolicyDto.Open));

		created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

		RideDetail ride = (await created.Content.ReadFromJsonAsync<RideDetail>())!;

		using HttpResponseMessage started =
			await organiser.PostAsync($"{RidesUrl}/{ride.Id}/start", content: null);

		started.StatusCode.ShouldBe(HttpStatusCode.NoContent, await started.Content.ReadAsStringAsync());

		await ShareAsync(organiser, ride.Id, share: true);
		await PublishAsync(organiser, -33.86, 151.20);

		return ride;
	}

	private static async Task<HttpClient> SignedInAsync(DlrWebApplicationFactory app, string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}
}
