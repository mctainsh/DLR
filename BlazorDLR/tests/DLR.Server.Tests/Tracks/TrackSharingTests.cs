using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Moderation;
using DLR.Core.Contracts.Photos;
using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using DLR.TestSupport.Photos;
using DLR.TestSupport.Tracks;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Tests.Tracks;

/// <summary>
/// Describing a route, giving it a cover photograph, and sharing it with everybody (§6.2, §6.3).
/// <para>
/// This is the first surface in the project where one rider reads another rider's track, so the
/// tests below spend most of their attention on the boundary: a private route stays invisible and
/// stays a 404, a public route is readable but not writable, and nothing a stranger can send makes
/// either of those untrue.
/// </para>
/// </summary>
public sealed class TrackSharingTests(PostgresFixture postgres)
{
	private const string TracksUrl = "/api/v1/tracks";
	private const string SharedUrl = "/api/v1/tracks/shared";

	[Fact]
	public async Task Details_StoreTheDescriptionThePhotoAndTheVisibility()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client);
		PhotoUploaded photo = await UploadPhotoAsync(client);

		TrackSummary saved = await SaveDetailsAsync(
			client,
			track.Id,
			new UpdateTrackDetailsRequest("  Gravel after the second bridge.  ", photo.PhotoId, TrackVisibilityDto.Public));

		saved.Description.ShouldBe("Gravel after the second bridge.", "the description is cleaned on the way in");
		saved.PhotoId.ShouldBe(photo.PhotoId);
		saved.Visibility.ShouldBe(TrackVisibilityDto.Public);

		// And it is what a later read says, not only what the write echoed.
		TrackDetail read = (await client.GetFromJsonAsync<TrackDetail>($"{TracksUrl}/{track.Id}"))!;

		read.Track.Description.ShouldBe("Gravel after the second bridge.");
		read.Track.PhotoId.ShouldBe(photo.PhotoId);
		read.Track.IsMine.ShouldBeTrue("the owner is reading their own route");
	}

	/// <summary>
	/// §15.5's version guards point indices, and none of this moves a point — the same reasoning
	/// that keeps a rename unversioned.
	/// </summary>
	[Fact]
	public async Task Details_DoNotBumpTheVersion_SoAnOpenEditStillApplies()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client, points: 60);

		TrackSummary saved = await SaveDetailsAsync(
			client,
			track.Id,
			new UpdateTrackDetailsRequest("A description.", null, TrackVisibilityDto.Private));

		saved.Version.ShouldBe(track.Version);

		using HttpResponseMessage edit = await client.PostAsJsonAsync(
			$"{TracksUrl}/{track.Id}/edit",
			new EditTrackRequest(track.Version, [new IndexRange(0, 5)]));

		edit.StatusCode.ShouldBe(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Details_RefuseAPhotoTheCallerDoesNotOwn()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient stranger = await SignedInAsync(app, "RileyJones");

		TrackSummary track = await UploadAsync(owner);
		PhotoUploaded theirs = await UploadPhotoAsync(stranger);

		using HttpResponseMessage response = await PatchAsync(
			owner,
			$"{TracksUrl}/{track.Id}/details",
			new UpdateTrackDetailsRequest(null, theirs.PhotoId, TrackVisibilityDto.Private));

		// Refused rather than quietly ignored: a guessed identifier would otherwise republish
		// somebody else's photograph as the cover of a route of the caller's choosing.
		response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Details_RefuseADescriptionTooLongToStore()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary track = await UploadAsync(client);

		using HttpResponseMessage response = await PatchAsync(
			client,
			$"{TracksUrl}/{track.Id}/details",
			new UpdateTrackDetailsRequest(new string('x', TrackDescription.MaxLength + 1), null, TrackVisibilityDto.Private));

		// A 400 that says which rule was broken, rather than a 500 from a column width.
		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Details_OnSomebodyElsesTrack_Is404_NotForbidden()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient stranger = await SignedInAsync(app, "RileyJones");

		TrackSummary track = await UploadAsync(owner);

		await SaveDetailsAsync(owner, track.Id, new UpdateTrackDetailsRequest(null, null, TrackVisibilityDto.Public));

		using HttpResponseMessage response = await PatchAsync(
			stranger,
			$"{TracksUrl}/{track.Id}/details",
			new UpdateTrackDetailsRequest("Mine now.", null, TrackVisibilityDto.Private));

		// Public to read is not public to write, and the answer is the same 404 a private track
		// gives so that this cannot be used to ask whether a track id exists (§15.4).
		response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Browse_ListsOtherRidersPublicRoutes_AndNeverPrivateOnes()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		TrackSummary shared = await UploadAsync(owner, name: "Coast run north");
		TrackSummary secret = await UploadAsync(owner, name: "Route past my house");

		await SaveDetailsAsync(owner, shared.Id, new UpdateTrackDetailsRequest("Worth the climb.", null, TrackVisibilityDto.Public));

		SharedTrackPage page = await BrowseAsync(reader);

		SharedTrackSummary only = page.Items.ShouldHaveSingleItem();

		only.Id.ShouldBe(shared.Id);
		only.Name.ShouldBe("Coast run north");
		only.Description.ShouldBe("Worth the climb.");
		only.OwnerName.ShouldBe("DaveSmith", "the username, never a self-chosen display name (§7.3)");
		page.TotalCount.ShouldBe(1);

		page.Items.ShouldNotContain(row => row.Id == secret.Id);
	}

	[Fact]
	public async Task Browse_ExcludesTheCallersOwnSharedRoutes()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient client = await SignedInAsync(app);

		TrackSummary mine = await UploadAsync(client);

		await SaveDetailsAsync(client, mine.Id, new UpdateTrackDetailsRequest(null, null, TrackVisibilityDto.Public));

		SharedTrackPage page = await BrowseAsync(client);

		// They are already on the other tab, and a rider browsing for somewhere new to ride is not
		// looking for the road they recorded themselves.
		page.Items.ShouldBeEmpty();
		page.TotalCount.ShouldBe(0);
		page.PageCount.ShouldBe(1, "an empty result is page 1 of 1, not page 1 of 0");
	}

	[Fact]
	public async Task Browse_UnsharingTakesARouteBackOffTheList()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		TrackSummary track = await UploadAsync(owner);

		await SaveDetailsAsync(owner, track.Id, new UpdateTrackDetailsRequest(null, null, TrackVisibilityDto.Public));
		(await BrowseAsync(reader)).TotalCount.ShouldBe(1);

		await SaveDetailsAsync(owner, track.Id, new UpdateTrackDetailsRequest(null, null, TrackVisibilityDto.Private));
		(await BrowseAsync(reader)).TotalCount.ShouldBe(0);

		// And the detail read closes with it — a stranger who kept the link gets the same 404 as
		// somebody who guessed the id.
		using HttpResponseMessage detail = await reader.GetAsync($"{TracksUrl}/{track.Id}");

		detail.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Browse_FiltersByNameCaseInsensitively_AndTreatsWildcardsAsText()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		await ShareAsync(owner, "Coast Run North");
		await ShareAsync(owner, "Hills loop");

		(await BrowseAsync(reader, name: "coast")).Items.ShouldHaveSingleItem().Name.ShouldBe("Coast Run North");

		// A bare underscore is a single-character wildcard in LIKE. Unescaped it would match
		// every route on the service, which is the opposite of what a filter is for.
		(await BrowseAsync(reader, name: "_")).Items.ShouldBeEmpty();
		(await BrowseAsync(reader, name: "%")).Items.ShouldBeEmpty();
	}

	[Fact]
	public async Task Browse_FiltersByDistanceFromAPoint_AndReportsHowFarAway()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		await ShareAsync(owner, "Right here");

		// Roughly 300 km due south of the fixture's base point, which is well outside a 50 km
		// search and comfortably inside a 500 km one.
		await ShareAsync(owner, "Far away", latitudeOffsetDeg: -2.7);

		SharedTrackPage near = await BrowseAsync(reader, latitude: GpxFixtures.BaseLatitude, longitude: GpxFixtures.BaseLongitude, withinKm: 50);

		SharedTrackSummary only = near.Items.ShouldHaveSingleItem();

		only.Name.ShouldBe("Right here");
		only.AwayKm.ShouldNotBeNull("a filtered list says how far away each route is");
		only.AwayKm!.Value.ShouldBeLessThan(50);

		SharedTrackPage wide = await BrowseAsync(reader, latitude: GpxFixtures.BaseLatitude, longitude: GpxFixtures.BaseLongitude, withinKm: 500);

		wide.TotalCount.ShouldBe(2);

		// Nearest first once an area is asked for: sorting a "within 500 km" list by date would
		// answer a question nobody asked with the control they just used.
		wide.Items[0].Name.ShouldBe("Right here");
		wide.Items[1].Name.ShouldBe("Far away");
	}

	/// <summary>
	/// Null, never zero. Zero kilometres away means "you are standing on it", and a list with no
	/// area filter has not measured anything (§8).
	/// </summary>
	[Fact]
	public async Task Browse_WithoutAnArea_ReportsNoDistance()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		await ShareAsync(owner, "Coast run north");

		(await BrowseAsync(reader)).Items.ShouldHaveSingleItem().AwayKm.ShouldBeNull();
	}

	[Fact]
	public async Task Browse_PagesWithoutRepeatingOrDroppingARow()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		int total = SharedTrackQuery.PageSize + 3;

		for (int index = 0; index < total; index++)
		{
			await ShareAsync(owner, $"Route {index:00}");
		}

		SharedTrackPage first = await BrowseAsync(reader);
		SharedTrackPage second = await BrowseAsync(reader, page: 2);

		first.Items.Count.ShouldBe(SharedTrackQuery.PageSize);
		first.TotalCount.ShouldBe(total);
		first.PageCount.ShouldBe(2);
		second.Items.Count.ShouldBe(3);

		// The whole set, once each. Every route shares FirstSharedUtc — the fake clock does not
		// tick unless a test moves it — so this is exactly the case a sort without a tiebreak
		// gets wrong, and it gets it wrong by repeating one row and losing another (§17.8).
		List<Guid> seen = [.. first.Items.Select(row => row.Id), .. second.Items.Select(row => row.Id)];

		seen.Distinct().Count().ShouldBe(total);
	}

	[Fact]
	public async Task Browse_HidesRoutesFromSomebodyTheReaderHasBlocked()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		await ShareAsync(owner, "Coast run north");

		(await BrowseAsync(reader)).TotalCount.ShouldBe(1);

		Guid ownerId = await IdOfAsync(app, "DaveSmith");

		using HttpResponseMessage blocked = await reader.PostAsJsonAsync("/api/v1/blocks", new BlockUserRequest(ownerId));

		blocked.IsSuccessStatusCode.ShouldBeTrue(await blocked.Content.ReadAsStringAsync());

		// §17.7 is one-directional and applies to every read of authored content. A route somebody
		// published is authored content, and a browse list is exactly the screen a rider blocked
		// somebody to stop seeing.
		(await BrowseAsync(reader)).TotalCount.ShouldBe(0);
	}

	[Fact]
	public async Task Detail_OfAPublicRoute_IsReadableByAnybody_ButSaysItIsNotTheirs()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		TrackSummary track = await UploadAsync(owner, name: "Coast run north");

		await SaveDetailsAsync(owner, track.Id, new UpdateTrackDetailsRequest("Worth the climb.", null, TrackVisibilityDto.Public));

		TrackDetail detail = (await reader.GetFromJsonAsync<TrackDetail>($"{TracksUrl}/{track.Id}"))!;

		detail.Track.Name.ShouldBe("Coast run north");
		detail.Track.IsMine.ShouldBeFalse("the reader does not own it, and the screen decides its buttons on this");
		detail.Track.OwnerName.ShouldBe("DaveSmith");
		detail.Polyline.ShouldNotBeEmpty("a shared route is shared to be looked at");
	}

	[Fact]
	public async Task Detail_OfAPrivateRoute_Is404ToEverybodyElse()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient stranger = await SignedInAsync(app, "RileyJones");

		TrackSummary track = await UploadAsync(owner);

		using HttpResponseMessage response = await stranger.GetAsync($"{TracksUrl}/{track.Id}");

		response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Rename_AndDelete_StayRefusedOnAPublicRouteTheCallerDoesNotOwn()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient stranger = await SignedInAsync(app, "RileyJones");

		TrackSummary track = await UploadAsync(owner);

		await SaveDetailsAsync(owner, track.Id, new UpdateTrackDetailsRequest(null, null, TrackVisibilityDto.Public));

		using HttpResponseMessage renamed = await PatchAsync(
			stranger,
			$"{TracksUrl}/{track.Id}",
			new RenameTrackRequest("Mine now"));

		renamed.StatusCode.ShouldBe(HttpStatusCode.NotFound);

		using HttpResponseMessage deleted = await stranger.DeleteAsync($"{TracksUrl}/{track.Id}");

		deleted.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	/// <summary>
	/// Un-sharing and re-sharing must not push a route back to the top of everybody's list, which
	/// is the one use a timestamp reset here would have.
	/// </summary>
	[Fact]
	public async Task Sharing_StampsTheFirstTimeOnly()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		TrackSummary track = await UploadAsync(owner);

		await SaveDetailsAsync(owner, track.Id, new UpdateTrackDetailsRequest(null, null, TrackVisibilityDto.Public));

		DateTimeOffset first = (await BrowseAsync(reader)).Items.ShouldHaveSingleItem().SharedUtc;

		// Minutes rather than days: an access token lives fifteen (§7.5), and a clock advanced
		// past that would make this a test about token expiry. Any forward movement at all is
		// enough to catch a stamp that resets.
		app.Clock.Advance(TimeSpan.FromMinutes(5));

		await SaveDetailsAsync(owner, track.Id, new UpdateTrackDetailsRequest(null, null, TrackVisibilityDto.Private));
		await SaveDetailsAsync(owner, track.Id, new UpdateTrackDetailsRequest(null, null, TrackVisibilityDto.Public));

		(await BrowseAsync(reader)).Items.ShouldHaveSingleItem().SharedUtc.ShouldBe(first);
	}

	/// <summary>
	/// The browse list is a catalogue, and the same road on it twice is a page of results that is
	/// mostly one route (§6.2). Only the points are compared — the second rider's copy has a name
	/// and a description of its own, and is refused all the same.
	/// </summary>
	[Fact]
	public async Task Sharing_RefusesARouteSomebodyElseHasAlreadyShared()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient first = await SignedInAsync(app, "DaveSmith");
		using HttpClient second = await SignedInAsync(app, "RileyJones");

		await ShareAsync(first, "Coast run north");

		// The same line arriving from another rider — an export of the first one, imported and
		// renamed, is exactly how this happens.
		TrackSummary copy = await UploadAsync(second, name: "My favourite ride");

		using HttpResponseMessage response = await PatchAsync(
			second,
			$"{TracksUrl}/{copy.Id}/details",
			new UpdateTrackDetailsRequest("Found this one myself.", null, TrackVisibilityDto.Public));

		string body = await response.Content.ReadAsStringAsync();

		response.StatusCode.ShouldBe(HttpStatusCode.Conflict, body);

		// And it says which route it clashes with, so the message is one a rider can act on.
		body.ShouldContain("Coast run north");

		// Refused means refused: nothing on the panel was stored, and the list still holds one route.
		TrackDetail read = (await second.GetFromJsonAsync<TrackDetail>($"{TracksUrl}/{copy.Id}"))!;

		read.Track.Visibility.ShouldBe(TrackVisibilityDto.Private);
		read.Track.Description.ShouldBeNull("the whole panel is one save, and it did not happen");
		(await BrowseAsync(second)).TotalCount.ShouldBe(1);
	}

	/// <summary>
	/// The check is scoped to other owners. A rider who holds the same line twice — the recording
	/// and the route they planned it from — publishes whichever of them they consider the good
	/// copy, and refusing that would be telling somebody they may not share their own route.
	/// </summary>
	[Fact]
	public async Task Sharing_AllowsTheSameLineTwiceFromTheSameOwner()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		await ShareAsync(owner, "Coast run north");
		await ShareAsync(owner, "Coast run north, the good version");

		(await BrowseAsync(reader)).TotalCount.ShouldBe(2);
	}

	/// <summary>
	/// The fingerprint column arrived after sharing did, so a track recorded before it has an
	/// empty one. Empty means "not known yet" and is filled from the blob on the way through —
	/// the alternative is two empty hashes matching each other, and every un-fingerprinted route
	/// being a duplicate of every other.
	/// </summary>
	[Fact]
	public async Task Sharing_ChecksARouteRecordedBeforeItHadAFingerprint()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient first = await SignedInAsync(app, "DaveSmith");
		using HttpClient second = await SignedInAsync(app, "RileyJones");

		TrackSummary mine = await UploadAsync(first, name: "Coast run north");
		TrackSummary theirs = await UploadAsync(second, name: "Hills loop");

		// Both rows as they would have been written a week ago.
		await app.WithDatabaseAsync(database => database
			.Set<Data.Tracks.Track>()
			.Where(track => track.Id == mine.Id || track.Id == theirs.Id)
			.ExecuteUpdateAsync(row => row.SetProperty(track => track.RouteHash, Array.Empty<byte>())));

		await SaveDetailsAsync(first, mine.Id, new UpdateTrackDetailsRequest(null, null, TrackVisibilityDto.Public));

		using HttpResponseMessage response = await PatchAsync(
			second,
			$"{TracksUrl}/{theirs.Id}/details",
			new UpdateTrackDetailsRequest(null, null, TrackVisibilityDto.Public));

		response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
	}

	/// <summary>
	/// A name on the shared list identifies a route to somebody who did not record it, and three
	/// rows called <em>Morning loop</em> identify nothing. Case is not a difference — nobody
	/// reading a list sees two names there.
	/// </summary>
	[Fact]
	public async Task Sharing_RefusesANameAnotherSharedRouteIsAlreadyUsing()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient first = await SignedInAsync(app, "DaveSmith");
		using HttpClient second = await SignedInAsync(app, "RileyJones");

		await ShareAsync(first, "Coast Run North");

		// A different road, so it is the name and only the name that is in the way.
		TrackSummary other = await UploadAsync(second, name: "coast run north", latitudeOffsetDeg: -2.7);

		using HttpResponseMessage response = await PatchAsync(
			second,
			$"{TracksUrl}/{other.Id}/details",
			new UpdateTrackDetailsRequest(null, null, TrackVisibilityDto.Public));

		response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());

		// Renamed, it goes on the list.
		using HttpResponseMessage renamed = await PatchAsync(
			second,
			$"{TracksUrl}/{other.Id}",
			new RenameTrackRequest("Coast run south"));

		renamed.StatusCode.ShouldBe(HttpStatusCode.OK, await renamed.Content.ReadAsStringAsync());

		await SaveDetailsAsync(second, other.Id, new UpdateTrackDetailsRequest(null, null, TrackVisibilityDto.Public));

		(await BrowseAsync(first)).TotalCount.ShouldBe(1, "a rider's own shared routes are never on their browse list");
	}

	/// <summary>
	/// Uniqueness is a property of the list, not of the app. A private track is the rider's own
	/// filing system and two of them may be called the same thing — the rule starts where somebody
	/// else has to tell them apart.
	/// </summary>
	[Fact]
	public async Task Sharing_LeavesPrivateNamesAlone()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient first = await SignedInAsync(app, "DaveSmith");
		using HttpClient second = await SignedInAsync(app, "RileyJones");

		await ShareAsync(first, "Coast run north");

		TrackSummary theirs = await UploadAsync(second, name: "Coast run north", latitudeOffsetDeg: -2.7);

		TrackSummary saved = await SaveDetailsAsync(
			second,
			theirs.Id,
			new UpdateTrackDetailsRequest("Kept to myself.", null, TrackVisibilityDto.Private));

		saved.Name.ShouldBe("Coast run north");
		saved.Visibility.ShouldBe(TrackVisibilityDto.Private);
	}

	/// <summary>
	/// Re-sharing a route is not a clash with itself, and neither is saving the panel again on one
	/// that is already on the list. Either would otherwise be a route that could be taken off the
	/// list and never put back.
	/// </summary>
	[Fact]
	public async Task Sharing_ARouteAlreadyOnTheList_IsNotADuplicateOfItself()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");

		TrackSummary track = await UploadAsync(owner, name: "Coast run north");

		await SaveDetailsAsync(owner, track.Id, new UpdateTrackDetailsRequest(null, null, TrackVisibilityDto.Public));
		await SaveDetailsAsync(owner, track.Id, new UpdateTrackDetailsRequest("Second thoughts.", null, TrackVisibilityDto.Public));
		await SaveDetailsAsync(owner, track.Id, new UpdateTrackDetailsRequest(null, null, TrackVisibilityDto.Private));

		TrackSummary again = await SaveDetailsAsync(owner, track.Id, new UpdateTrackDetailsRequest(null, null, TrackVisibilityDto.Public));

		again.Visibility.ShouldBe(TrackVisibilityDto.Public);
	}

	/// <summary>
	/// The other way a shared route ends up wearing a name that is already on the list. The rename
	/// endpoint has to apply the same rule, or the check on the share is a door with a window
	/// beside it.
	/// </summary>
	[Fact]
	public async Task Rename_OfASharedRoute_RefusesANameAlreadyOnTheList()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient first = await SignedInAsync(app, "DaveSmith");
		using HttpClient second = await SignedInAsync(app, "RileyJones");

		await ShareAsync(first, "Coast run north");

		TrackSummary theirs = await ShareAsync(second, "Hills loop", latitudeOffsetDeg: -2.7);

		using HttpResponseMessage response = await PatchAsync(
			second,
			$"{TracksUrl}/{theirs.Id}",
			new RenameTrackRequest("COAST RUN NORTH"));

		response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());

		// And a name nobody has taken still goes through.
		using HttpResponseMessage free = await PatchAsync(
			second,
			$"{TracksUrl}/{theirs.Id}",
			new RenameTrackRequest("Hills loop, anticlockwise"));

		free.StatusCode.ShouldBe(HttpStatusCode.OK, await free.Content.ReadAsStringAsync());
	}

	/// <summary>
	/// A rider renaming their own shared route to what it is already called is not a clash with
	/// itself — most obviously when they are correcting its capitalisation.
	/// </summary>
	[Fact]
	public async Task Rename_OfASharedRoute_ToWhatItIsAlreadyCalled_IsFine()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient owner = await SignedInAsync(app, "DaveSmith");

		TrackSummary track = await ShareAsync(owner, "coast run north");

		using HttpResponseMessage response = await PatchAsync(
			owner,
			$"{TracksUrl}/{track.Id}",
			new RenameTrackRequest("Coast Run North"));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		(await response.Content.ReadFromJsonAsync<TrackSummary>())!.Name.ShouldBe("Coast Run North");
	}

	// -- Helpers -------------------------------------------------------------------------

	private static async Task<SharedTrackPage> BrowseAsync(
		HttpClient client,
		string? name = null,
		double? latitude = null,
		double? longitude = null,
		double? withinKm = null,
		int page = 1)
	{
		List<string> parts = [$"page={page}"];

		if (name is not null)
			parts.Add($"name={Uri.EscapeDataString(name)}");

		if (withinKm is not null)
		{
			parts.Add(FormattableString.Invariant($"lat={latitude}"));
			parts.Add(FormattableString.Invariant($"lon={longitude}"));
			parts.Add(FormattableString.Invariant($"withinKm={withinKm}"));
		}

		using HttpResponseMessage response = await client.GetAsync($"{SharedUrl}?{string.Join('&', parts)}");

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<SharedTrackPage>())!;
	}

	/// <summary>Uploads a track and shares it in one step, for the tests that only care that it is on the list.</summary>
	private static async Task<TrackSummary> ShareAsync(
		HttpClient client,
		string name,
		double latitudeOffsetDeg = 0)
	{
		TrackSummary track = await UploadAsync(client, name: name, latitudeOffsetDeg: latitudeOffsetDeg);

		return await SaveDetailsAsync(client, track.Id, new UpdateTrackDetailsRequest(null, null, TrackVisibilityDto.Public));
	}

	private static async Task<TrackSummary> SaveDetailsAsync(
		HttpClient client,
		Guid trackId,
		UpdateTrackDetailsRequest request)
	{
		using HttpResponseMessage response = await PatchAsync(client, $"{TracksUrl}/{trackId}/details", request);

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<TrackSummary>())!;
	}

	private static Task<HttpResponseMessage> PatchAsync<TBody>(HttpClient client, string url, TBody body) =>
		client.PatchAsync(url, JsonContent.Create(body));

	private static async Task<PhotoUploaded> UploadPhotoAsync(HttpClient client)
	{
		using MultipartFormDataContent form = [];
		using ByteArrayContent file = new(ImageFixtures.Jpeg(400, 300));

		file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

		form.Add(file, "file", "photo.jpg");

		using HttpResponseMessage response = await client.PostAsync("/api/v1/photos", form);

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<PhotoUploaded>())!;
	}

	private static async Task<TrackSummary> UploadAsync(
		HttpClient client,
		string? name = "Morning loop",
		int points = 20,
		double latitudeOffsetDeg = 0)
	{
		TrackGeometry geometry = new(
		[
			.. Enumerable.Range(0, points).Select(index => new TrackPoint(
				GpxFixtures.BaseLatitude + latitudeOffsetDeg + (index * GpxFixtures.MetresToDegreesLatitude(20)),
				GpxFixtures.BaseLongitude,
				50 + (index % 7),
				GpxFixtures.Start.AddSeconds(index * 10))),
		]);

		using HttpResponseMessage response = await client.PostAsJsonAsync(
			TracksUrl,
			new UploadTrackRequest(Guid.NewGuid(), geometry.Points, null, name));

		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<TrackSummary>())!;
	}

	private static Task<Guid> IdOfAsync(DlrWebApplicationFactory app, string userName) =>
		app.WithDatabaseAsync(database =>
			database.Set<Data.Identity.AppUser>()
				.Where(user => user.UserName == userName)
				.Select(user => user.Id)
				.SingleAsync());

	private static async Task<HttpClient> SignedInAsync(
		DlrWebApplicationFactory app,
		string userName = "DaveSmith")
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}
}
