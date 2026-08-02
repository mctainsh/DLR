using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DLR.Core.Contracts.Account;
using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Markers;
using DLR.Core.Contracts.Photos;
using DLR.Core.Contracts.Rides;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;

namespace DLR.Server.Tests.Account;

/// <summary>
/// <c>GET /api/v1/me/export</c> (§6.3, §10.1, §16.6).
/// <para>
/// An export is a promise about completeness, which makes an omission a different and worse kind of
/// bug from an ugly file: the account holder has been told they were given everything, and they
/// were not. So the tests here are mostly about what is <em>in</em> the archive rather than how it
/// is shaped.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AccountExportTests(PostgresFixture postgres)
{
	private const string ExportUrl = "/api/v1/me/export";

	/// <summary>
	/// §15.6 says the retained original is exported with the track — it is the rider's data for as
	/// long as this server holds it, and leaving it out would make the archive's claim false for
	/// exactly the seven days it matters.
	/// </summary>
	[Fact]
	public async Task Export_IncludesRetainedRevisionWhileItExists()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient rider = await AccountDeletionTests.SignedInAsync(app, "DaveSmith");

		Guid trackId = await AccountDeletionTests.UploadTrackAsync(rider);

		await AccountDeletionTests.EditAsync(rider, trackId);

		using ZipArchive archive = await ExportAsync(rider);

		AccountExport manifest = Manifest(archive);

		ExportedTrack track = manifest.Tracks.ShouldHaveSingleItem();

		track.PreviousVersionGpxPath.ShouldNotBeNull();

		archive.GetEntry(track.GpxPath).ShouldNotBeNull();

		// The file, not merely the field. A manifest naming a path the archive does not contain
		// would satisfy every assertion above and hand the rider nothing.
		archive.GetEntry(track.PreviousVersionGpxPath!).ShouldNotBeNull(
			"a path in the manifest with no file behind it is not an export of anything");

		// And it must be the pre-edit points, not a second copy of the current ones — the whole
		// reason §15.6 keeps it is that the two differ.
		string current = Text(archive, track.GpxPath);
		string previous = Text(archive, track.PreviousVersionGpxPath!);

		previous.ShouldNotBe(current);
	}

	/// <summary>
	/// The undo window closes and the original stops existing (§15.6). The export then has nothing
	/// to offer and must say so rather than naming a file it cannot produce.
	/// </summary>
	[Fact]
	public async Task Export_AfterTheOriginalIsPurged_NamesNoPreviousVersion()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient rider = await AccountDeletionTests.SignedInAsync(app, "DaveSmith");

		Guid trackId = await AccountDeletionTests.UploadTrackAsync(rider);

		await AccountDeletionTests.EditAsync(rider, trackId);

		using HttpResponseMessage purged = await rider.DeleteAsync(
			$"/api/v1/tracks/{trackId}/previous-version");

		purged.EnsureSuccessStatusCode();

		using ZipArchive archive = await ExportAsync(rider);

		Manifest(archive).Tracks.ShouldHaveSingleItem().PreviousVersionGpxPath.ShouldBeNull();
	}

	/// <summary>
	/// §7.15 names this one. Both halves: the values <em>and</em> the switches — what a rider chose
	/// to share is a decision about their own privacy, and an export showing a phone number without
	/// saying who could see it answers a different question.
	/// </summary>
	[Fact]
	public async Task Profile_Export_IncludesAllRecordedFieldsAndSwitches()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient rider = await AccountDeletionTests.SignedInAsync(app, "DaveSmith");

		using HttpResponseMessage updated = await rider.PutAsJsonAsync(
			"/api/v1/me/profile",
			new UpdateProfileRequest(
				"Dave",
				"+61400000000",
				ShareDisplayName: true,
				SharePhoneNumber: false,
				ShareEmail: false));

		updated.EnsureSuccessStatusCode();

		using ZipArchive archive = await ExportAsync(rider);

		ExportedProfile profile = Manifest(archive).Profile;

		profile.DisplayName.ShouldBe("Dave");

		profile.PhoneNumber.ShouldBe(
			"+61400000000",
			"a value withheld from other riders is still the rider's own data (§7.3)");

		profile.ShareDisplayName.ShouldBeTrue();
		profile.SharePhoneNumber.ShouldBeFalse();
		profile.ShareEmail.ShouldBeFalse();
	}

	/// <summary>
	/// §16.6 requires the export to include markers <strong>and their photos</strong>. A list of
	/// identifiers would not be an export of anybody's photographs.
	/// </summary>
	[Fact]
	public async Task Export_IncludesMarkersAndTheImagesThemselves()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient rider = await AccountDeletionTests.SignedInAsync(app, "DaveSmith");

		Guid trackId = await AccountDeletionTests.UploadTrackAsync(rider);

		PhotoUploaded photo = await AccountDeletionTests.UploadPhotoAsync(rider);

		using HttpResponseMessage placed = await rider.PostAsJsonAsync(
			"/api/v1/markers",
			new CreateMarkerRequest(
				trackId,
				null,
				PositionScale.FromDegrees(-33.86),
				PositionScale.FromDegrees(151.20),
				"water",
				"Tap by the bridge",
				"Cold",
				90));

		placed.EnsureSuccessStatusCode();

		MarkerDto marker = (await placed.Content.ReadFromJsonAsync<MarkerDto>())!;

		using HttpResponseMessage attached = await rider.PatchAsJsonAsync(
			$"/api/v1/markers/{marker.Id}/photo",
			new AttachPhotoRequest(photo.PhotoId));

		attached.EnsureSuccessStatusCode();

		using ZipArchive archive = await ExportAsync(rider);

		AccountExport manifest = Manifest(archive);

		ExportedMarker exported = manifest.Markers.ShouldHaveSingleItem();

		exported.Title.ShouldBe("Tap by the bridge");
		exported.DirectionDeg.ShouldBe((short)90);
		exported.PhotoId.ShouldBe(photo.PhotoId);

		ExportedPhoto image = manifest.Photos.ShouldHaveSingleItem();

		ZipArchiveEntry? entry = archive.GetEntry(image.ImagePath);

		entry.ShouldNotBeNull("§16.6 says the export includes the photographs, not their ids");
		entry.Length.ShouldBeGreaterThan(0);
	}

	/// <summary>
	/// A poll is a comment (§17.5), so it exports through the same list — and its options come with
	/// it, since a poll reduced to its question is not the post that was made.
	/// </summary>
	[Fact]
	public async Task Export_IncludesThreadPostsPollsAndVotes()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await AccountDeletionTests.SignedInAsync(app, "Organiser");
		using HttpClient rider = await AccountDeletionTests.SignedInAsync(app, "DaveSmith");

		RideDetail ride = await AccountDeletionTests.RideAsync(app, organiser, rider);

		CommentDto poll = await AccountDeletionTests.PostAsync(
			rider,
			ride.Id,
			new PostCommentRequest(Guid.NewGuid(), "Café or pub?", Poll: new PollSpec(["Café", "Pub"])));

		using HttpResponseMessage voted = await rider.PostAsJsonAsync(
			$"/api/v1/comments/{poll.Id}/votes",
			new CastVoteRequest([poll.Poll!.Options[1].Id]));

		voted.EnsureSuccessStatusCode();

		using HttpResponseMessage reacted = await rider.PutAsJsonAsync(
			$"/api/v1/comments/{poll.Id}/reaction",
			new SetReactionRequest("like"));

		reacted.EnsureSuccessStatusCode();

		using ZipArchive archive = await ExportAsync(rider);

		AccountExport manifest = Manifest(archive);

		ExportedComment exported = manifest.Comments.ShouldHaveSingleItem();

		exported.Body.ShouldBe("Café or pub?");
		exported.PollOptions.ShouldBe(["Café", "Pub"]);

		manifest.Votes.ShouldHaveSingleItem().OptionText.ShouldBe("Pub");
		manifest.Reactions.ShouldHaveSingleItem().Reaction.ShouldBe("like");

		manifest.Rides.ShouldHaveSingleItem().Name.ShouldBe("Sunday");
	}

	/// <summary>
	/// The join code is a ride's entire access control and goes only to the organiser (§5.2). An
	/// export handed to a member that carried it would let any member re-share the group the
	/// organiser curated — through a file nobody thinks of as a sharing surface.
	/// </summary>
	[Fact]
	public async Task Export_NeverCarriesARidesJoinCode()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await AccountDeletionTests.SignedInAsync(app, "Organiser");
		using HttpClient member = await AccountDeletionTests.SignedInAsync(app, "DaveSmith");

		RideDetail ride = await AccountDeletionTests.RideAsync(app, organiser, member);

		ride.JoinCode.ShouldNotBeNull("the organiser's own view carries it, which is the point");

		using ZipArchive archive = await ExportAsync(member);

		// Against the raw manifest text, not against a property — the rule is "the code is not in
		// the file", not "one field is null", which is how SRV-20 asserted the same obligation.
		Text(archive, AccountExportBuilderManifest)
			.ShouldNotContain(ride.JoinCode!, Case.Insensitive);
	}

	/// <summary>An export is the most complete thing this API produces. It is never anonymous.</summary>
	[Fact]
	public async Task Export_WithoutAToken_IsRefused()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient anonymous = app.CreateClient();

		using HttpResponseMessage refused = await anonymous.GetAsync(ExportUrl);

		refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	private const string AccountExportBuilderManifest = "export.json";

	private static async Task<ZipArchive> ExportAsync(HttpClient client)
	{
		using HttpResponseMessage response = await client.GetAsync(ExportUrl);

		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

		response.Content.Headers.ContentType?.MediaType.ShouldBe("application/zip");

		MemoryStream buffer = new(await response.Content.ReadAsByteArrayAsync());

		return new ZipArchive(buffer, ZipArchiveMode.Read);
	}

	private static AccountExport Manifest(ZipArchive archive) =>
		JsonSerializer.Deserialize<AccountExport>(
			Text(archive, AccountExportBuilderManifest),
			new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

	private static string Text(ZipArchive archive, string path)
	{
		ZipArchiveEntry entry = archive.GetEntry(path)
			?? throw new InvalidOperationException($"The archive has no '{path}'.");

		using StreamReader reader = new(entry.Open());

		return reader.ReadToEnd();
	}
}
