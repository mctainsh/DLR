using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Rides;
using DLR.Core.Markers;
using DLR.Server.Data.Markers;
using DLR.Server.Data.Rides;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Markers;

/// <summary>
/// Authored points of interest (§16.1, §16.2, §16.5).
/// <para>
/// A marker is the one thing on this map that a person deliberately placed and typed, so unlike a
/// position it lives as long as the thing it hangs off. Conflating those two lifecycles is how a
/// privacy-first app quietly starts retaining locations.
/// </para>
/// </summary>
public sealed class MarkerTests(PostgresFixture postgres)
{
	private const string MarkersUrl = "/api/v1/markers";
	private const string RidesUrl = "/api/v1/group-rides";

	/// <summary>
	/// The §16.1 exclusive arc, asserted against the <em>database</em> rather than the endpoint.
	/// The endpoint's check is a courtesy so the caller gets a 400; the constraint is what makes
	/// the invariant true for every path that ever writes this table.
	/// </summary>
	[Fact]
	public async Task Marker_WithBothParents_IsRejectedByCheckConstraint()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);
		Guid trackId = await StageTrackAsync(app, "DaveSmith");
		Guid ownerId = await IdOfAsync(app, "DaveSmith");

		DbUpdateException failure = await Should.ThrowAsync<DbUpdateException>(() =>
			app.WithDatabaseAsync(async database =>
			{
				Marker both = Row(ownerId);

				both.TrackId = trackId;
				both.GroupRideId = ride.Id;

				database.Add(both);

				await database.SaveChangesAsync();
			}));

		failure.InnerException!.Message.ShouldContain(MarkerConfiguration.OneParentConstraint);
	}

	[Fact]
	public async Task Marker_WithNeitherParent_IsRejectedByCheckConstraint()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		Guid ownerId = await IdOfAsync(app, "DaveSmith");

		DbUpdateException failure = await Should.ThrowAsync<DbUpdateException>(() =>
			app.WithDatabaseAsync(async database =>
			{
				database.Add(Row(ownerId));

				await database.SaveChangesAsync();
			}));

		failure.InnerException!.Message.ShouldContain(MarkerConfiguration.OneParentConstraint);
	}

	/// <summary>
	/// Zero is due north and a perfectly good bearing, so a column that defaulted to it would
	/// silently claim every fuel stop points that way (§16.2).
	/// </summary>
	[Fact]
	public async Task Marker_NullDirection_IsStoredAsNullNotZero()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		MarkerDto placed = await CreateAsync(organiser, Request(ride.Id) with { DirectionDeg = null });

		placed.DirectionDeg.ShouldBeNull();

		short? stored = await app.WithDatabaseAsync(database =>
			database.Set<Marker>()
				.Where(marker => marker.Id == placed.Id)
				.Select(marker => marker.DirectionDeg)
				.SingleAsync());

		stored.ShouldBeNull("null means 'no direction', never north");

		// And a marker that genuinely points north keeps its zero.
		MarkerDto north = await CreateAsync(organiser, Request(ride.Id) with { DirectionDeg = 0 });

		north.DirectionDeg.ShouldBe((short)0);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(360)]
	[InlineData(3600)]
	public async Task Marker_DirectionOutOfRange_Returns400(int direction)
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		using HttpResponseMessage refused = await organiser.PostAsJsonAsync(
			MarkersUrl,
			Request(ride.Id) with { DirectionDeg = (short)direction });

		refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}

	/// <summary>
	/// An app one version ahead sends <c>ferry</c>; a server that has never heard of it stores it
	/// anyway, and an older client falls back to <c>note</c>. An enum ordinal would need a
	/// migration in lockstep across two app stores' release cadences (§16.2).
	/// </summary>
	[Fact]
	public async Task Marker_UnknownIconKey_IsStoredAndRendersAsFallback()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		MarkerDto placed = await CreateAsync(organiser, Request(ride.Id) with { Icon = "ferry" });

		placed.Icon.ShouldBe("ferry", "stored as sent, not flattened to the fallback");

		MarkerIcons.IsKnown(placed.Icon).ShouldBeFalse(
			"this version cannot draw it, which is exactly the case being tested");

		// Length and character set are still enforced — "stored, not rejected" is about
		// membership, not about anything at all being acceptable.
		using HttpResponseMessage nonsense = await organiser.PostAsJsonAsync(
			MarkersUrl,
			Request(ride.Id) with { Icon = "Not An Icon!" });

		nonsense.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Marker_TitleOverLimit_Returns400()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		using HttpResponseMessage refused = await organiser.PostAsJsonAsync(
			MarkersUrl,
			Request(ride.Id) with { Title = new string('x', 41) });

		refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

		(await refused.Content.ReadAsStringAsync()).ShouldContain("40");

		// Exactly at the limit is fine — an off-by-one here is a rejected marker for no reason.
		MarkerDto placed = await CreateAsync(
			organiser,
			Request(ride.Id) with { Title = new string('x', 40) });

		placed.Title.Length.ShouldBe(40);
	}

	/// <summary>
	/// A title is optional (§16.2). The icon is what carries the meaning of a pin read at speed,
	/// and "gravel" typed under a gravel pin is the word twice — so an empty one is a marker,
	/// not a 400. It is stored as empty rather than null: the column is NOT NULL, and every
	/// reader already treats "" as "draw the icon alone".
	/// </summary>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public async Task Marker_WithNoTitle_IsAccepted(string title)
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		MarkerDto placed = await CreateAsync(organiser, Request(ride.Id) with { Title = title });

		placed.Title.ShouldBeEmpty("whitespace cleans to the same thing as empty — one untitled marker.");

		string stored = await app.WithDatabaseAsync(database =>
			database.Set<Marker>()
				.Where(marker => marker.Id == placed.Id)
				.Select(marker => marker.Title)
				.SingleAsync());

		stored.ShouldBeEmpty();

		// And an edit can take a label off one that has it, which is the same rule running the
		// other way — a title you can add but never remove is a trap.
		MarkerDto titled = await CreateAsync(organiser, Request(ride.Id) with { Title = "Gravel" });

		using HttpResponseMessage cleared = await organiser.PutAsJsonAsync(
			$"{MarkersUrl}/{titled.Id}",
			new UpdateMarkerRequest(titled.Lat, titled.Lon, titled.Icon, string.Empty));

		cleared.StatusCode.ShouldBe(HttpStatusCode.OK);

		MarkerDto after = (await cleared.Content.ReadFromJsonAsync<MarkerDto>())!;

		after.Title.ShouldBeEmpty();
	}

	[Fact]
	public async Task Marker_OnGroupRide_ByNonMember_Returns403()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient outsider = await SignedInAsync(app, "NosyNed");

		RideDetail ride = await CreateRideAsync(organiser);

		using HttpResponseMessage refused = await outsider.PostAsJsonAsync(MarkersUrl, Request(ride.Id));

		refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		int stored = await app.WithDatabaseAsync(database => database.Set<Marker>().CountAsync());

		stored.ShouldBe(0);
	}

	/// <summary>
	/// Any admitted member, not just the organiser — the useful marker is "gravel across the whole
	/// corner at the 40 km mark", and the person who found it is whoever hit it first (§16.5).
	/// </summary>
	[Fact]
	public async Task Marker_OnGroupRide_AnyMemberMayCreate()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(rider, ride.JoinCode!);

		MarkerDto placed = await CreateAsync(rider, Request(ride.Id) with { Icon = "gravel" });

		placed.CreatedByUserName.ShouldBe("SamJones");

		// And the organiser sees it on the ride.
		List<MarkerDto> onRide =
			(await organiser.GetFromJsonAsync<List<MarkerDto>>($"{RidesUrl}/{ride.Id}/markers"))!;

		onRide.ShouldHaveSingleItem().Id.ShouldBe(placed.Id);
	}

	[Fact]
	public async Task Marker_EditByOtherMember_Returns403()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient author = await SignedInAsync(app, "SamJones");
		using HttpClient other = await SignedInAsync(app, "PatBrown");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(author, ride.JoinCode!);
		await JoinAsync(other, ride.JoinCode!);

		MarkerDto placed = await CreateAsync(author, Request(ride.Id));

		using HttpResponseMessage refused = await other.PutAsJsonAsync(
			$"{MarkersUrl}/{placed.Id}",
			new UpdateMarkerRequest(placed.Lat, placed.Lon, "hazard", "Rewritten"));

		refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		// The author may, and so may the organiser.
		using HttpResponseMessage byAuthor = await author.PutAsJsonAsync(
			$"{MarkersUrl}/{placed.Id}",
			new UpdateMarkerRequest(placed.Lat, placed.Lon, "hazard", "Mine to edit"));

		byAuthor.StatusCode.ShouldBe(HttpStatusCode.OK);

		using HttpResponseMessage byOrganiser = await organiser.PutAsJsonAsync(
			$"{MarkersUrl}/{placed.Id}",
			new UpdateMarkerRequest(placed.Lat, placed.Lon, "hazard", "Organiser's call"));

		byOrganiser.StatusCode.ShouldBe(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Marker_DeleteByOrganiser_Succeeds()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient author = await SignedInAsync(app, "SamJones");
		using HttpClient other = await SignedInAsync(app, "PatBrown");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(author, ride.JoinCode!);
		await JoinAsync(other, ride.JoinCode!);

		MarkerDto placed = await CreateAsync(author, Request(ride.Id));

		(await other.DeleteAsync($"{MarkersUrl}/{placed.Id}")).StatusCode
			.ShouldBe(HttpStatusCode.Forbidden, "an ordinary member does not delete somebody else's");

		using HttpResponseMessage deleted = await organiser.DeleteAsync($"{MarkersUrl}/{placed.Id}");

		deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		(await app.WithDatabaseAsync(database => database.Set<Marker>().CountAsync())).ShouldBe(0);
	}

	[Fact]
	public async Task Marker_ExceedingPerRideCap_Returns409()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: new Dictionary<string, string?>
			{
				["Markers:MaxPerGroupRide"] = "3",
			});

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		for (int index = 0; index < 3; index++)
		{
			await CreateAsync(organiser, Request(ride.Id) with { Title = $"Stop {index}" });
		}

		using HttpResponseMessage refused = await organiser.PostAsJsonAsync(MarkersUrl, Request(ride.Id));

		refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
	}

	/// <summary>
	/// The per-member cap does a different job from the per-ride one: without it, one enthusiastic
	/// member uses the whole ride's allowance (§16.5).
	/// </summary>
	[Fact]
	public async Task Marker_ExceedingPerMemberCap_Returns409()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(
			postgres,
			settings: new Dictionary<string, string?>
			{
				["Markers:MaxPerMemberPerRide"] = "2",
			});

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient rider = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(rider, ride.JoinCode!);

		for (int index = 0; index < 2; index++)
		{
			await CreateAsync(rider, Request(ride.Id) with { Title = $"Mine {index}" });
		}

		using HttpResponseMessage refused = await rider.PostAsJsonAsync(MarkersUrl, Request(ride.Id));

		refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);

		// The ride is nowhere near its own cap — somebody else may still add one.
		MarkerDto theirs = await CreateAsync(organiser, Request(ride.Id) with { Title = "Organiser's" });

		theirs.Id.ShouldNotBe(Guid.Empty);
	}

	/// <summary>
	/// Positions are measured exhaust and go when the ride ends; markers are the record of what
	/// happened and stay (§16.6). The contrast is the point.
	/// </summary>
	[Fact]
	public async Task RideCompleted_DeletesPositionsButKeepsMarkers()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		await organiser.PostAsync($"{RidesUrl}/{ride.Id}/start", content: null);

		await organiser.PutAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/sharing/me",
			new SetSharingRequest(true));

		await organiser.PostAsJsonAsync(
			"/api/v1/positions",
			new PositionUpdate(
				PositionScale.FromDegrees(-33.86),
				PositionScale.FromDegrees(151.20),
				DlrWebApplicationFactory.DefaultStart));

		await CreateAsync(organiser, Request(ride.Id));

		await app.FlushPositionsAsync();

		using HttpResponseMessage ended = await organiser.PostAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/ending",
			new EndRideRequest());

		ended.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		(await app.WithDatabaseAsync(database =>
			database.Set<Data.Positions.RiderPosition>().CountAsync())).ShouldBe(0);

		(await app.WithDatabaseAsync(database => database.Set<Marker>().CountAsync())).ShouldBe(
			1,
			"markers are what happened; positions are the exhaust");
	}

	/// <summary>Deleting the parent takes its markers with it (§16.6).</summary>
	[Fact]
	public async Task DeletingARide_CascadesToItsMarkers()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await CreateRideAsync(organiser);

		await CreateAsync(organiser, Request(ride.Id));

		await app.WithDatabaseAsync(async database =>
			await database.Set<GroupRide>().Where(row => row.Id == ride.Id).ExecuteDeleteAsync());

		(await app.WithDatabaseAsync(database => database.Set<Marker>().CountAsync())).ShouldBe(0);
	}

	private static Marker Row(Guid ownerId) => new()
	{
		Id = Guid.NewGuid(),
		CreatedByUserId = ownerId,
		Lat = PositionScale.FromDegrees(-33.86),
		Lon = PositionScale.FromDegrees(151.20),
		Icon = "hazard",
		Title = "Straight into the database",
		CreatedUtc = DlrWebApplicationFactory.DefaultStart,
		UpdatedUtc = DlrWebApplicationFactory.DefaultStart,
	};

	private static CreateMarkerRequest Request(Guid rideId) => new(
		TrackId: null,
		GroupRideId: rideId,
		PositionScale.FromDegrees(-33.86),
		PositionScale.FromDegrees(151.20),
		"hazard",
		"Gravel on the corner");

	private static async Task<MarkerDto> CreateAsync(HttpClient client, CreateMarkerRequest request)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(MarkersUrl, request);

		response.StatusCode.ShouldBe(
			HttpStatusCode.Created,
			await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<MarkerDto>())!;
	}

	private static async Task<Guid> StageTrackAsync(DlrWebApplicationFactory app, string userName)
	{
		Guid ownerId = await IdOfAsync(app, userName);

		return await app.WithDatabaseAsync(async database =>
		{
			Data.Tracks.Track track = new()
			{
				Id = Guid.NewGuid(),
				OwnerId = ownerId,
				Name = "Saturday",
				BlobRef = "none",
				CreatedUtc = DlrWebApplicationFactory.DefaultStart,
			};

			database.Add(track);

			await database.SaveChangesAsync();

			return track.Id;
		});
	}

	private static Task<Guid> IdOfAsync(DlrWebApplicationFactory app, string userName) =>
		app.WithDatabaseAsync(database => database.Users
			.Where(user => user.UserName == userName)
			.Select(user => user.Id)
			.SingleAsync());

	private static async Task JoinAsync(HttpClient client, string code)
	{
		using HttpResponseMessage response =
			await client.PostAsJsonAsync($"{RidesUrl}/join", new JoinByCodeRequest(code));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
	}

	private static async Task<RideDetail> CreateRideAsync(HttpClient organiser)
	{
		using HttpResponseMessage response = await organiser.PostAsJsonAsync(
			RidesUrl,
			new CreateRideRequest(
				"Saturday hills",
				DlrWebApplicationFactory.DefaultStart.AddDays(3),
				JoinPolicy: JoinPolicyDto.Open));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<RideDetail>())!;
	}

	private static async Task<HttpClient> SignedInAsync(DlrWebApplicationFactory app, string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}
}
