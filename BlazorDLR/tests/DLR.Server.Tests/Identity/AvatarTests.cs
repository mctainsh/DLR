using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Photos;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using DLR.TestSupport.Photos;

namespace DLR.Server.Tests.Identity;

/// <summary>
/// The profile photograph, and the batch lookup every screen that draws a name makes (§7.3, §16.4).
/// <para>
/// The interesting property is the one that is <em>not</em> like the three switched fields beside
/// it: this has no sharing switch and no gate, because it exists to sit beside the username and the
/// username is already readable by every signed-in rider (§7.2). The tests below pin that down in
/// both directions - anybody signed in can read it, and nobody at all can set somebody else's.
/// </para>
/// </summary>
public sealed class AvatarTests(PostgresFixture postgres)
{
	private const string AvatarUrl = "/api/v1/me/avatar";
	private const string ProfileUrl = "/api/v1/me/profile";
	private const string LookupUrl = "/api/v1/users/avatars";

	[Fact]
	public async Task Setting_AnAvatar_PutsItOnTheProfileAndInTheLookup()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient rider = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		PhotoUploaded photo = await UploadPhotoAsync(rider);

		OwnProfile saved = await SetAvatarAsync(rider, photo.PhotoId);

		saved.AvatarPhotoId.ShouldBe(photo.PhotoId);

		// What a later read says, not only what the write echoed.
		OwnProfile read = (await rider.GetFromJsonAsync<OwnProfile>(ProfileUrl))!;

		read.AvatarPhotoId.ShouldBe(photo.PhotoId);

		// And what everybody else sees. No switch was turned on, because there is not one -
		// adding the photograph is the consent.
		RiderAvatarDto only = (await LookupAsync(reader, "DaveSmith")).ShouldHaveSingleItem();

		only.UserName.ShouldBe("DaveSmith");
		only.PhotoId.ShouldBe(photo.PhotoId);
	}

	[Fact]
	public async Task AnAvatar_IsReadableByAnySignedInRider_NotOnlyCoMembersOfARide()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient rider = await SignedInAsync(app, "DaveSmith");
		using HttpClient stranger = await SignedInAsync(app, "RileyJones");

		PhotoUploaded photo = await UploadPhotoAsync(rider);

		await SetAvatarAsync(rider, photo.PhotoId);

		// The two share no ride at all. §7.3's gate gives a stranger nothing -
		SharedProfile profile = (await stranger.GetFromJsonAsync<SharedProfile>(
			$"/api/v1/users/{await IdOfAsync(app, "DaveSmith")}/profile"))!;

		profile.DisplayName.ShouldBeNull();
		profile.PhoneNumber.ShouldBeNull();

		// - and the avatar deliberately travels further, because the name beside it already does.
		(await LookupAsync(stranger, "DaveSmith")).ShouldHaveSingleItem().PhotoId.ShouldBe(photo.PhotoId);

		// The bytes are reachable too, or the identifier would be useless to the screen holding it.
		using HttpResponseMessage thumbnail = await stranger.GetAsync($"/api/v1/photos/{photo.PhotoId}/thumbnail");

		thumbnail.StatusCode.ShouldBe(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Setting_SomebodyElsesPhoto_IsRefused()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient rider = await SignedInAsync(app, "DaveSmith");
		using HttpClient other = await SignedInAsync(app, "RileyJones");

		PhotoUploaded theirs = await UploadPhotoAsync(other);

		using HttpResponseMessage response = await rider.PutAsJsonAsync(AvatarUrl, new SetAvatarRequest(theirs.PhotoId));

		// Refused rather than silently ignored: a guessed identifier would otherwise put somebody
		// else's face beside this account's name on every screen in the app.
		response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());

		OwnProfile read = (await rider.GetFromJsonAsync<OwnProfile>(ProfileUrl))!;

		read.AvatarPhotoId.ShouldBeNull();
	}

	[Fact]
	public async Task Setting_APhotoThatDoesNotExist_IsRefused()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		using HttpResponseMessage response = await rider.PutAsJsonAsync(AvatarUrl, new SetAvatarRequest(Guid.NewGuid()));

		response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Removing_TakesItOffEveryScreen_AndIsIdempotent()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient rider = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		PhotoUploaded photo = await UploadPhotoAsync(rider);

		await SetAvatarAsync(rider, photo.PhotoId);

		using (HttpResponseMessage cleared = await rider.DeleteAsync(AvatarUrl))
		{
			cleared.StatusCode.ShouldBe(HttpStatusCode.OK, await cleared.Content.ReadAsStringAsync());
			(await cleared.Content.ReadFromJsonAsync<OwnProfile>())!.AvatarPhotoId.ShouldBeNull();
		}

		(await LookupAsync(reader, "DaveSmith")).ShouldHaveSingleItem().PhotoId.ShouldBeNull();

		// Again. The caller is asking for a state, not for a row, so an account with no photograph
		// is a 200 rather than a 404.
		using HttpResponseMessage again = await rider.DeleteAsync(AvatarUrl);

		again.StatusCode.ShouldBe(HttpStatusCode.OK);
	}

	/// <summary>
	/// The whole-profile PUT must not be able to clear a photograph it knows nothing about - the
	/// reason the avatar is its own sub-resource, and the same trap §10.1's private area avoids.
	/// </summary>
	[Fact]
	public async Task SavingTheRestOfTheProfile_DoesNotClearTheAvatar()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		PhotoUploaded photo = await UploadPhotoAsync(rider);

		await SetAvatarAsync(rider, photo.PhotoId);

		// Exactly what an older client sends: every field it knows about, and nothing about this one.
		using HttpResponseMessage saved = await rider.PutAsJsonAsync(
			ProfileUrl,
			new UpdateProfileRequest(DisplayName: "Dave", PhoneNumber: "0400 000 000"));

		saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());

		(await saved.Content.ReadFromJsonAsync<OwnProfile>())!.AvatarPhotoId.ShouldBe(photo.PhotoId);
	}

	[Fact]
	public async Task Lookup_AnswersForEveryNameAsked_IncludingOnesWithNoAccount()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient rider = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		PhotoUploaded photo = await UploadPhotoAsync(rider);

		await SetAvatarAsync(rider, photo.PhotoId);

		IReadOnlyList<RiderAvatarDto> answers = await LookupAsync(reader, "DaveSmith", "RileyJones", "NoSuchRider");

		// A row per question, not per row found. A caller that had to tell "no photograph" from
		// "no such account" by the absence of a row would be holding a username oracle - and a
		// client that could not cache the negative answer would ask again on every render.
		answers.Count.ShouldBe(3);
		answers.Single(row => row.UserName == "DaveSmith").PhotoId.ShouldBe(photo.PhotoId);
		answers.Single(row => row.UserName == "RileyJones").PhotoId.ShouldBeNull();
		answers.Single(row => row.UserName == "NoSuchRider").PhotoId.ShouldBeNull();
	}

	/// <summary>
	/// A caller holding a name off a cached row may not have the casing the account was created
	/// with, and the name it gets back has to be the one it asked with - otherwise its cache never
	/// hits (§7.2).
	/// </summary>
	[Fact]
	public async Task Lookup_MatchesWithoutRegardToCase_AndEchoesTheNameAsked()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient rider = await SignedInAsync(app, "DaveSmith");
		using HttpClient reader = await SignedInAsync(app, "RileyJones");

		PhotoUploaded photo = await UploadPhotoAsync(rider);

		await SetAvatarAsync(rider, photo.PhotoId);

		RiderAvatarDto only = (await LookupAsync(reader, "davesmith")).ShouldHaveSingleItem();

		only.PhotoId.ShouldBe(photo.PhotoId);
		only.UserName.ShouldBe("davesmith");
	}

	[Fact]
	public async Task Lookup_WithNoNames_IsAnEmptyListRatherThanAnError()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient reader = await SignedInAsync(app, "DaveSmith");

		using HttpResponseMessage response = await reader.GetAsync(LookupUrl);

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		(await response.Content.ReadFromJsonAsync<List<RiderAvatarDto>>())!.ShouldBeEmpty();
	}

	/// <summary>
	/// Truncated rather than refused: a client asking about too many names should get the first
	/// hundred avatars, not an error that leaves a whole screen with none.
	/// </summary>
	[Fact]
	public async Task Lookup_PastTheCap_TakesTheFirstNamesRatherThanRefusing()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient reader = await SignedInAsync(app, "DaveSmith");

		string[] names = [.. Enumerable.Range(0, AvatarLookup.MaxNames + 25).Select(index => $"Rider{index:000}")];

		IReadOnlyList<RiderAvatarDto> answers = await LookupAsync(reader, names);

		answers.Count.ShouldBe(AvatarLookup.MaxNames);
	}

	[Fact]
	public async Task Lookup_RequiresASignedInCaller()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);
		using HttpClient anonymous = app.CreateClient();

		using HttpResponseMessage response = await anonymous.GetAsync($"{LookupUrl}?names=DaveSmith");

		// A photograph travels as far as the username does, and the username needs a session.
		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	// -- Helpers -------------------------------------------------------------------------

	private static async Task<OwnProfile> SetAvatarAsync(HttpClient client, Guid photoId)
	{
		using HttpResponseMessage response = await client.PutAsJsonAsync(AvatarUrl, new SetAvatarRequest(photoId));

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<OwnProfile>())!;
	}

	private static async Task<IReadOnlyList<RiderAvatarDto>> LookupAsync(HttpClient client, params string[] names)
	{
		string query = string.Join(AvatarLookup.Separator, names.Select(Uri.EscapeDataString));

		using HttpResponseMessage response = await client.GetAsync($"{LookupUrl}?names={query}");

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		return (await response.Content.ReadFromJsonAsync<List<RiderAvatarDto>>())!;
	}

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

	private static Task<Guid> IdOfAsync(DlrWebApplicationFactory app, string userName) =>
		app.WithDatabaseAsync(database =>
			Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
				database.Users.Where(user => user.UserName == userName).Select(user => user.Id)));

	private static async Task<HttpClient> SignedInAsync(DlrWebApplicationFactory app, string userName)
	{
		using HttpClient registrar = app.CreateClient();

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}
}
