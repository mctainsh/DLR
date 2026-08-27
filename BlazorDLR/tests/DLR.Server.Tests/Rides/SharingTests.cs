using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Rides;
using DLR.Server.Data.Positions;
using DLR.Server.Data.Rides;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Rides;

/// <summary>
/// Joining a ride and agreeing to broadcast are two separate decisions (§5.6).
/// <para>
/// The load-bearing assertion in most of these is not that the flag changed — it is that the
/// stored row is <em>gone</em>. Stopping the broadcast while leaving a last-known position at rest
/// in the database is precisely what a rider turning sharing off is asking you not to do (§10.1).
/// </para>
/// </summary>
public sealed class SharingTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";

	/// <summary>A prompt that treats a swipe-away as consent is not a consent prompt (§5.6).</summary>
	[Fact]
	public async Task Join_DismissedSharingPrompt_LeavesShareLocationFalse()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await LiveRideAsync(app, organiser);

		// Joining, and then doing nothing at all — which is what dismissing the prompt is.
		await JoinAsync(rider, ride.JoinCode!);

		bool sharing = await app.WithDatabaseAsync(database =>
			database.Set<GroupRideMember>()
				.Where(member => member.GroupRideId == ride.Id)
				.OrderBy(member => member.JoinedUtc)
				.Select(member => member.ShareLocation)
				.LastAsync());

		sharing.ShouldBeFalse("dismissing is 'not now', and an accidental 'on' cannot be un-shared");

		// The organiser is not sharing either. Creating a ride is not consent to broadcast.
		RideDetail seen = (await organiser.GetFromJsonAsync<RideDetail>($"{RidesUrl}/{ride.Id}"))!;

		seen.Members.ShouldAllBe(member => !member.Sharing);
	}

	/// <summary>
	/// A rider may be in a ride without sharing (§5.6). The alternative — making sharing the price
	/// of seeing the map — is simpler and coercive.
	/// </summary>
	[Fact]
	public async Task Join_SharingDeclined_MemberSeesOthersButPublishesNothing()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient watcher = await SignedInAsync(app, "SamJones");

		RideDetail ride = await LiveRideAsync(app, organiser);

		await JoinAsync(watcher, ride.JoinCode!);

		// The organiser shares; the watcher does not.
		await ShareAsync(organiser, ride.Id, share: true);
		await PublishAsync(organiser, At(-33.86, 151.20));

		await PublishAsync(watcher, At(-33.87, 151.21));

		await app.FlushPositionsAsync();

		List<RiderPositionDto> visible =
			(await watcher.GetFromJsonAsync<List<RiderPositionDto>>($"{RidesUrl}/{ride.Id}/positions"))!;

		visible.ShouldHaveSingleItem().UserName.ShouldBe(
			"DaveSmith",
			"a pillion or a support-van driver has every reason to watch without broadcasting");

		// And nothing of the watcher's was stored anywhere.
		int stored = await app.WithDatabaseAsync(database =>
			database.Set<RiderPosition>().CountAsync());

		stored.ShouldBe(1);
	}

	/// <summary>
	/// Consent is filtered on the <strong>write</strong> (§5.7). A rider not sharing has no row at
	/// all — not a hidden pin. Broadcasting anyway and asking recipients to hide it would leave the
	/// position in the database, in the fan-out and on the wire.
	/// </summary>
	[Fact]
	public async Task Publish_ByNonSharingMember_IsRejectedAndStoresNothing()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await LiveRideAsync(app, organiser);

		await JoinAsync(rider, ride.JoinCode!);

		PublishResult result = await PublishAsync(rider, At(-33.86, 151.20));

		result.RideIds.ShouldBeEmpty("no adventure consented, so the fix lands nowhere");

		int stored = await app.WithDatabaseAsync(database =>
			database.Set<RiderPosition>().CountAsync());

		stored.ShouldBe(0);
	}

	/// <summary>
	/// The whole point of the task. Ceasing to update the row is not the same as deleting it, and
	/// only one of them is what the rider asked for (§5.6, §10.1).
	/// </summary>
	[Fact]
	public async Task Sharing_TurnedOff_DeletesPersistedRowImmediately()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await LiveRideAsync(app, organiser);

		await ShareAsync(organiser, ride.Id, share: true);
		await PublishAsync(organiser, At(-33.86, 151.20));

		// Flushed first, so what the delete below has to remove is a genuinely persisted row
		// rather than a cache entry that had never reached the database anyway.
		await app.FlushPositionsAsync();

		(await CountPositionsAsync(app)).ShouldBe(1, "so the delete below has something to prove");

		SharingState off = await ShareAsync(organiser, ride.Id, share: false);

		off.Sharing.ShouldBeFalse();
		off.HasPosition.ShouldBeFalse();

		(await CountPositionsAsync(app)).ShouldBe(
			0,
			"stopping the broadcast alone would leave a last-known position at rest — exactly " +
			"what turning sharing off is asking you not to keep");
	}

	/// <summary>
	/// The organiser controls the <em>ride</em>; the rider controls their <em>location</em> (§5.6).
	/// <para>
	/// Asserted against the route surface rather than a permission check, because the asymmetry is
	/// structural: there is no endpoint that expresses "set another traveller's sharing", so there is
	/// no guard on it that could later be relaxed.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Organiser_CannotEnableSharingOnBehalfOfAMember()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await LiveRideAsync(app, organiser);

		await JoinAsync(rider, ride.JoinCode!);

		Guid riderId = (await organiser.GetFromJsonAsync<RideDetail>($"{RidesUrl}/{ride.Id}"))!
			.Members.Single(member => member.UserName == "SamJones").UserId;

		// No route accepts a user id here, so this is refused by routing rather than by a check
		// somebody could delete. Routing answers 405 rather than 404 because the host also serves
		// the Blazor app: MapStaticAssets registers a `{**path:file}` catch-all limited to
		// GET/HEAD, which claims every otherwise-unmatched path and turns a non-GET into "method
		// not allowed". Either code carries the assertion — the point is that no PUT handler
		// exists at this path, not which flavour of refusal the router reaches for.
		using HttpResponseMessage byId = await organiser.PutAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/sharing/{riderId}",
			new SetSharingRequest(Share: true));

		byId.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);

		// And the organiser turning their own switch on moves nobody else's.
		await ShareAsync(organiser, ride.Id, share: true);

		RideDetail after = (await organiser.GetFromJsonAsync<RideDetail>($"{RidesUrl}/{ride.Id}"))!;

		after.Members.Single(member => member.UserName == "SamJones").Sharing.ShouldBeFalse();
		after.Members.Single(member => member.UserName == "DaveSmith").Sharing.ShouldBeTrue();
	}

	/// <summary>Leaving does what turning the switch off does, for the same reason (§5.6).</summary>
	[Fact]
	public async Task Leaving_DeletesTheirPositionAndMembership()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await LiveRideAsync(app, organiser);

		await JoinAsync(rider, ride.JoinCode!);
		await ShareAsync(rider, ride.Id, share: true);
		await PublishAsync(rider, At(-33.86, 151.20));

		await app.FlushPositionsAsync();

		(await CountPositionsAsync(app)).ShouldBe(1);

		using HttpResponseMessage left = await rider.DeleteAsync($"{RidesUrl}/{ride.Id}/members/me");

		left.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		(await CountPositionsAsync(app)).ShouldBe(0);

		(await rider.GetAsync($"{RidesUrl}/{ride.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	/// <summary>Removal by the organiser does too (§5.2, §5.6).</summary>
	[Fact]
	public async Task Removal_ByOrganiser_DeletesTheirPosition()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await LiveRideAsync(app, organiser);

		await JoinAsync(rider, ride.JoinCode!);
		await ShareAsync(rider, ride.Id, share: true);
		await PublishAsync(rider, At(-33.86, 151.20));

		await app.FlushPositionsAsync();

		Guid riderId = (await organiser.GetFromJsonAsync<RideDetail>($"{RidesUrl}/{ride.Id}"))!
			.Members.Single(member => member.UserName == "SamJones").UserId;

		using HttpResponseMessage removed =
			await organiser.DeleteAsync($"{RidesUrl}/{ride.Id}/members/{riderId}");

		removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		(await CountPositionsAsync(app)).ShouldBe(0);
	}

	/// <summary>The default ending: everything goes, immediately (§5.6).</summary>
	[Fact]
	public async Task RideEnd_DefaultChoice_DeletesAllPositionsImmediately()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await LiveRideAsync(app, organiser);

		await JoinAsync(rider, ride.JoinCode!);

		foreach (HttpClient client in new[] { organiser, rider })
		{
			await ShareAsync(client, ride.Id, share: true);
			await PublishAsync(client, At(-33.86, 151.20));
		}

		await app.FlushPositionsAsync();

		(await CountPositionsAsync(app)).ShouldBe(2);

		using HttpResponseMessage ended = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/ending",
			new EndRideRequest());

		ended.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		(await CountPositionsAsync(app)).ShouldBe(0);

		RideDetail after = (await organiser.GetFromJsonAsync<RideDetail>($"{RidesUrl}/{ride.Id}"))!;

		after.State.ShouldBe(RideStateDto.Completed);
		after.Members.ShouldAllBe(member => !member.Sharing);
	}

	/// <summary>
	/// The §5.5 guard, at the endpoint. A retried or out-of-order fix must not move a rider
	/// backwards on the map.
	/// </summary>
	[Fact]
	public async Task Publish_OlderFix_DoesNotRegressTheStoredPosition()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await LiveRideAsync(app, organiser);

		await ShareAsync(organiser, ride.Id, share: true);

		DateTimeOffset now = DlrWebApplicationFactory.DefaultStart;

		await PublishAsync(organiser, At(-33.86, 151.20) with { RecordedUtc = now });
		await PublishAsync(organiser, At(-33.99, 151.99) with { RecordedUtc = now.AddSeconds(-30) });

		RiderPositionDto stored =
			(await organiser.GetFromJsonAsync<List<RiderPositionDto>>($"{RidesUrl}/{ride.Id}/positions"))!
			.ShouldHaveSingleItem();

		stored.Lat.ShouldBe(PositionScale.FromDegrees(-33.86), "the older fix must not win");
		stored.RecordedUtc.ShouldBe(now);
	}

	/// <summary>
	/// The positive case the two §7.3 revocation tests are measured against — without it they
	/// would both pass against an endpoint that always returned nothing.
	/// </summary>
	[Fact]
	public async Task Profile_CoMemberOfActiveRide_SeesTheSharedFields()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		await ShareProfileAsync(rider);

		Guid riderId = await IdOfAsync(app, "SamJones");

		RideDetail ride = await LiveRideAsync(app, organiser);

		await JoinAsync(rider, ride.JoinCode!);

		ProfileView seen = await ProfileAsync(organiser, riderId);

		seen.DisplayName.ShouldBe("Sam");
		seen.PhoneNumber.ShouldBe("0400000000");
	}

	/// <summary>§7.3's deferred test, writable now that there is a ride to leave.</summary>
	[Fact]
	public async Task Profile_AfterLeavingRide_SharedFieldsAreNoLongerVisible()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		await ShareProfileAsync(rider);

		Guid riderId = await IdOfAsync(app, "SamJones");

		RideDetail ride = await LiveRideAsync(app, organiser);

		await JoinAsync(rider, ride.JoinCode!);

		(await ProfileAsync(organiser, riderId)).PhoneNumber.ShouldBe("0400000000");

		await rider.DeleteAsync($"{RidesUrl}/{ride.Id}/members/me");

		ProfileView after = await ProfileAsync(organiser, riderId);

		after.DisplayName.ShouldBeNull();
		after.PhoneNumber.ShouldBeNull("sharing is ride-scoped and revokes itself");
	}

	/// <summary>
	/// §7.3's other deferred test. Profile sharing ends the moment the ride is Completed and
	/// deliberately does <em>not</em> follow the position wind-down: that window exists so people
	/// can watch each other get home, and there is no equivalent reason to keep a phone number
	/// visible for two more hours.
	/// </summary>
	[Fact]
	public async Task Profile_AfterRideCompletes_SharedFieldsAreNoLongerVisible()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		await ShareProfileAsync(rider);

		Guid riderId = await IdOfAsync(app, "SamJones");

		RideDetail ride = await LiveRideAsync(app, organiser);

		await JoinAsync(rider, ride.JoinCode!);

		(await ProfileAsync(organiser, riderId)).PhoneNumber.ShouldBe("0400000000");

		await organiser.PostAsJsonAsync($"{RidesUrl}/{ride.Id}/ending", new EndRideRequest());

		ProfileView after = await ProfileAsync(organiser, riderId);

		after.PhoneNumber.ShouldBeNull("the adventure is over, and so is the audience");
	}

	/// <summary>
	/// A stranger gets an empty profile rather than a 404, so the endpoint cannot be used to ask
	/// whether an account shares a ride with you (§7.3).
	/// </summary>
	[Fact]
	public async Task Profile_OfAStranger_IsEmptyRatherThanNotFound()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient stranger = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		await ShareProfileAsync(rider);

		Guid riderId = await IdOfAsync(app, "SamJones");

		ProfileView seen = await ProfileAsync(stranger, riderId);

		seen.PhoneNumber.ShouldBeNull();

		// Indistinguishable from an account that does not exist at all.
		ProfileView nobody = await ProfileAsync(stranger, Guid.NewGuid());

		nobody.ShouldBe(seen);
	}

	[Fact]
	public async Task Positions_NonMember_CannotRead()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient outsider = await SignedInAsync(app, "NosyNed");

		RideDetail ride = await LiveRideAsync(app, organiser);

		await ShareAsync(organiser, ride.Id, share: true);
		await PublishAsync(organiser, At(-33.86, 151.20));

		(await outsider.GetAsync($"{RidesUrl}/{ride.Id}/positions")).StatusCode
			.ShouldBe(HttpStatusCode.NotFound);
	}

	private static PositionUpdate At(double lat, double lon) => new(
		PositionScale.FromDegrees(lat),
		PositionScale.FromDegrees(lon),
		DlrWebApplicationFactory.DefaultStart);

	private static async Task<PublishResult> PublishAsync(HttpClient client, PositionUpdate update)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/positions", update);

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<PublishResult>())!;
	}

	private static async Task<SharingState> ShareAsync(HttpClient client, Guid rideId, bool share)
	{
		using HttpResponseMessage response = await client.PutAsJsonAsync(
			$"{RidesUrl}/{rideId}/sharing/me",
			new SetSharingRequest(share));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<SharingState>())!;
	}

	/// <summary>
	/// Read back through a mirror type, not <see cref="SharedProfile"/> itself.
	/// <para>
	/// <see cref="SharedProfile"/>'s properties are <c>private init</c> so that
	/// <see cref="SharedProfile.For"/> is the only way to build one — which also means
	/// <c>System.Text.Json</c> cannot rehydrate it and would hand every test a silently empty
	/// object that passes any "is not visible" assertion. Asserting on the wire form is what the
	/// rule is actually about.
	/// </para>
	/// </summary>
	private static async Task<ProfileView> ProfileAsync(HttpClient viewer, Guid ownerId) =>
		(await viewer.GetFromJsonAsync<ProfileView>($"/api/v1/users/{ownerId}/profile"))!;

	private sealed record ProfileView(string? DisplayName, string? PhoneNumber, string? Email);

	/// <summary>Turns on every switch, so a leak has something to leak.</summary>
	private static async Task ShareProfileAsync(HttpClient rider)
	{
		using HttpResponseMessage updated = await rider.PutAsJsonAsync(
			"/api/v1/me/profile",
			new UpdateProfileRequest(
				"Sam",
				"0400000000",
				ShareDisplayName: true,
				SharePhoneNumber: true));

		updated.StatusCode.ShouldBe(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());
	}

	private static Task<Guid> IdOfAsync(DlrWebApplicationFactory app, string userName) =>
		app.WithDatabaseAsync(database => database.Users
			.Where(user => user.UserName == userName)
			.Select(user => user.Id)
			.SingleAsync());

	private static Task<int> CountPositionsAsync(DlrWebApplicationFactory app) =>
		app.WithDatabaseAsync(database => database.Set<RiderPosition>().CountAsync());

	/// <summary>
	/// A ride in <c>Live</c>, because publishing only lands in live rides (§5.5). Set directly:
	/// the Draft → Open → Live transitions are SRV-25's endpoint, not this task's.
	/// </summary>
	private static async Task<RideDetail> LiveRideAsync(
		DlrWebApplicationFactory app,
		HttpClient organiser)
	{
		using HttpResponseMessage response = await organiser.PostAsJsonAsync(
			RidesUrl,
			new CreateRideRequest(
				"Saturday hills",
				DlrWebApplicationFactory.DefaultStart.AddDays(3),
				JoinPolicy: JoinPolicyDto.Open));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		RideDetail ride = (await response.Content.ReadFromJsonAsync<RideDetail>())!;

		await app.WithDatabaseAsync(async database =>
		{
			await database.Set<GroupRide>()
				.Where(row => row.Id == ride.Id)
				.ExecuteUpdateAsync(row => row.SetProperty(x => x.State, GroupRideState.Live));

			return 0;
		});

		return ride;
	}

	private static async Task JoinAsync(HttpClient client, string code)
	{
		using HttpResponseMessage response =
			await client.PostAsJsonAsync($"{RidesUrl}/join", new JoinByCodeRequest(code));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
	}

	private static async Task<HttpClient> SignedInAsync(DlrWebApplicationFactory app, string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}
}
