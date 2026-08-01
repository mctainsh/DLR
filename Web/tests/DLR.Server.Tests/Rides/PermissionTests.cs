using System.Net;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Photos;
using DLR.Core.Contracts.Rides;
using DLR.Server.Data.Markers;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using DLR.TestSupport.Photos;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Rides;

/// <summary>
/// The organiser's three content switches (§5.8).
/// <para>
/// The switches exist for the ride that needs them — a large public charity ride, or one that has
/// gone sideways — which is why all three default <em>on</em>. The two rules that are easy to get
/// backwards are that turning one off deletes nothing, and that it never applies to the organiser
/// who set it.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PermissionTests(PostgresFixture postgres)
{
	private const string RidesUrl = "/api/v1/group-rides";
	private const string MarkersUrl = "/api/v1/markers";

	[Fact]
	public async Task Permissions_MarkersOff_MemberPostReturns403()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient member = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, member, ride.Id);

		// On by default, so the member can place one before anything is changed. Without this the
		// test would pass against a switch that was off from the start, which is a different bug.
		ride.Permissions.AllowMemberMarkers.ShouldBeTrue("§5.8 defaults are on");

		MarkerDto before = await CreateMarkerAsync(member, ride.Id);

		await SetPermissionsAsync(organiser, ride.Id, new RidePermissions(AllowMemberMarkers: false));

		using HttpResponseMessage refused = await member.PostAsJsonAsync(
			MarkersUrl,
			MarkerRequest(ride.Id));

		refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		// Distinguishable, so a client can tell "you may not" from "that failed" (§5.8).
		refused.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

		// And the one already placed is untouched — asserted here as well as in its own test,
		// because this is the moment somebody would be tempted to tidy up.
		before.Id.ShouldNotBe(Guid.Empty);

		int stillThere = await app.WithDatabaseAsync(database =>
			database.Set<Marker>().CountAsync(marker => marker.GroupRideId == ride.Id));

		stillThere.ShouldBe(1);
	}

	/// <summary>
	/// The photo switch is its own, not a consequence of the comment one (§5.8).
	/// <para>
	/// <strong>Enforcement is on the attach, not the upload.</strong> A photo is a standalone
	/// resource with no ride context — taken at the top of a hill, uploaded whenever there is
	/// signal (§16.4) — so the ride's switch can only be consulted when the image is bound to
	/// something in that ride.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Permissions_PhotosOff_MarkerWithoutPhotoStillSucceeds()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient member = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, member, ride.Id);

		await SetPermissionsAsync(organiser, ride.Id, new RidePermissions(AllowMemberPhotos: false));

		// Markers are a different switch and it is still on, so this works.
		MarkerDto marker = await CreateMarkerAsync(member, ride.Id);

		// The upload itself is not ride-scoped and is not refused.
		PhotoUploaded photo = await UploadPhotoAsync(member);

		using HttpResponseMessage refused = await member.PatchAsJsonAsync(
			$"{MarkersUrl}/{marker.Id}/photo",
			new AttachPhotoRequest(photo.PhotoId));

		refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		MarkerDto[] markers = (await member.GetFromJsonAsync<MarkerDto[]>(
			$"{RidesUrl}/{ride.Id}/markers"))!;

		markers.Single().PhotoId.ShouldBeNull("the attach was refused, so nothing was bound");
	}

	/// <summary>
	/// Same rule as §7.3's profile sharing: revoking a permission is not an instruction to destroy
	/// what was already permitted.
	/// </summary>
	[Fact]
	public async Task Permissions_TurnedOff_ExistingContentIsUntouched()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient member = await SignedInAsync(app, "SamJones");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, member, ride.Id);

		MarkerDto placed = await CreateMarkerAsync(member, ride.Id);

		PhotoUploaded photo = await UploadPhotoAsync(member);

		using (HttpResponseMessage attached = await member.PatchAsJsonAsync(
			$"{MarkersUrl}/{placed.Id}/photo",
			new AttachPhotoRequest(photo.PhotoId)))
		{
			attached.StatusCode.ShouldBe(HttpStatusCode.OK, await attached.Content.ReadAsStringAsync());
		}

		await SetPermissionsAsync(
			organiser,
			ride.Id,
			new RidePermissions(AllowMemberMarkers: false, AllowMemberComments: false, AllowMemberPhotos: false));

		// Still there, still readable, still carrying its photograph — by the member who is now
		// forbidden from creating another.
		MarkerDto[] markers = (await member.GetFromJsonAsync<MarkerDto[]>(
			$"{RidesUrl}/{ride.Id}/markers"))!;

		markers.Length.ShouldBe(1);
		markers[0].Id.ShouldBe(placed.Id);
		markers[0].Title.ShouldBe(placed.Title);
		markers[0].PhotoId.ShouldBe(photo.PhotoId);

		using HttpResponseMessage stillServed = await member.GetAsync($"/api/v1/photos/{photo.PhotoId}");

		stillServed.StatusCode.ShouldBe(HttpStatusCode.OK, "the image is not deleted either");
	}

	/// <summary>
	/// An organiser who turned member markers off did not thereby give up placing markers, and a
	/// rule that made them do so would read as a bug the first time it happened (§5.8).
	/// </summary>
	[Fact]
	public async Task Permissions_OrganiserIsNeverRestrictedByOwnSwitches()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient member = await SignedInAsync(app, "SamJones");
		using HttpClient leader = await SignedInAsync(app, "AlexLee");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, member, ride.Id);
		await JoinAsync(app, leader, ride.Id);
		await PromoteAsync(app, ride.Id, "AlexLee", GroupRideRole.Leader);

		await SetPermissionsAsync(
			organiser,
			ride.Id,
			new RidePermissions(AllowMemberMarkers: false, AllowMemberPhotos: false));

		// The organiser is unaffected.
		MarkerDto theirs = await CreateMarkerAsync(organiser, ride.Id);

		PhotoUploaded photo = await UploadPhotoAsync(organiser);

		using (HttpResponseMessage attached = await organiser.PatchAsJsonAsync(
			$"{MarkersUrl}/{theirs.Id}/photo",
			new AttachPhotoRequest(photo.PhotoId)))
		{
			attached.StatusCode.ShouldBe(HttpStatusCode.OK, await attached.Content.ReadAsStringAsync());
		}

		// So is a leader — §5.8 exempts both, and a check written as "is the owner" would pass
		// every assertion above and fail this one.
		await CreateMarkerAsync(leader, ride.Id);

		// The ordinary member is not.
		using HttpResponseMessage refused = await member.PostAsJsonAsync(MarkersUrl, MarkerRequest(ride.Id));

		refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Permissions_SetByOrdinaryMember_Returns403()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "DaveSmith");
		using HttpClient member = await SignedInAsync(app, "SamJones");
		using HttpClient stranger = await SignedInAsync(app, "PatKim");

		RideDetail ride = await CreateRideAsync(organiser);

		await JoinAsync(app, member, ride.Id);

		using (HttpResponseMessage refused = await member.PutAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/permissions",
			new RidePermissions(AllowMemberMarkers: false)))
		{
			refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		}

		// And somebody not in the ride gets a 404, not a 403 — a ride id is shareable, so a
		// distinguishable refusal would make this an oracle for who is in which ride (§5.2).
		using HttpResponseMessage unknown = await stranger.PutAsJsonAsync(
			$"{RidesUrl}/{ride.Id}/permissions",
			new RidePermissions(AllowMemberMarkers: false));

		unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);

		// The refusals changed nothing.
		RideDetail after = (await organiser.GetFromJsonAsync<RideDetail>($"{RidesUrl}/{ride.Id}"))!;

		after.Permissions.AllowMemberMarkers.ShouldBeTrue();
	}

	private static async Task SetPermissionsAsync(
		HttpClient organiser,
		Guid rideId,
		RidePermissions permissions)
	{
		using HttpResponseMessage response = await organiser.PutAsJsonAsync(
			$"{RidesUrl}/{rideId}/permissions",
			permissions);

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		RidePermissions applied = (await response.Content.ReadFromJsonAsync<RidePermissions>())!;

		applied.ShouldBe(permissions);
	}

	private static CreateMarkerRequest MarkerRequest(Guid rideId) => new(
		TrackId: null,
		GroupRideId: rideId,
		PositionScale.FromDegrees(-33.86),
		PositionScale.FromDegrees(151.20),
		"hazard",
		"Gravel on the corner");

	private static async Task<MarkerDto> CreateMarkerAsync(HttpClient client, Guid rideId)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(MarkersUrl, MarkerRequest(rideId));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<MarkerDto>())!;
	}

	private static async Task<PhotoUploaded> UploadPhotoAsync(HttpClient client)
	{
		using MultipartFormDataContent form = [];
		using ByteArrayContent file = new(ImageFixtures.Jpeg(240, 180));

		file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");

		form.Add(file, "file", "photo.jpg");

		using HttpResponseMessage response = await client.PostAsync("/api/v1/photos", form);

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<PhotoUploaded>())!;
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

	/// <summary>Joins by the code, which only the organiser's copy of the ride carries (§5.2).</summary>
	private static async Task JoinAsync(DlrWebApplicationFactory app, HttpClient member, Guid rideId)
	{
		string code = await app.WithDatabaseAsync(database =>
			database.Set<Data.Rides.GroupRide>()
				.Where(ride => ride.Id == rideId)
				.Select(ride => ride.JoinCode)
				.SingleAsync());

		using HttpResponseMessage response = await member.PostAsJsonAsync(
			$"{RidesUrl}/join",
			new JoinByCodeRequest(code));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
	}

	/// <summary>
	/// Straight to the table: there is no promote endpoint yet, and this test is about the
	/// permission check reading the role rather than about how somebody comes to have it.
	/// </summary>
	private static async Task PromoteAsync(
		DlrWebApplicationFactory app,
		Guid rideId,
		string userName,
		GroupRideRole role) =>
		await app.WithDatabaseAsync(async database =>
		{
			Data.Rides.GroupRideMember member = await database
				.Set<Data.Rides.GroupRideMember>()
				.SingleAsync(row => row.GroupRideId == rideId && row.User!.UserName == userName);

			member.Role = (Data.Rides.GroupRideRole)role;

			await database.SaveChangesAsync();
		});

	private static async Task<HttpClient> SignedInAsync(DlrWebApplicationFactory app, string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}

	/// <summary>Mirrors <c>GroupRideRole</c> so the test reads without a persistence using.</summary>
	private enum GroupRideRole
	{
		Owner = 0,
		Leader = 1,
		Rider = 2,
		Spectator = 3,
	}
}
