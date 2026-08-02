using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DLR.Core.Contracts.Account;
using DLR.Core.Contracts.Comments;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Moderation;
using DLR.Core.Contracts.Photos;
using DLR.Core.Contracts.Rides;
using DLR.Core.Contracts.Tracks;
using DLR.Server.Data.Comments;
using DLR.Server.Data.Photos;
using DLR.Server.Data.Tracks;
using DLR.Server.Tracks;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using DLR.TestSupport.Photos;
using DLR.TestSupport.Tracks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.Server.Tests.Account;

/// <summary>
/// <c>DELETE /api/v1/me</c> — the one irreversible action in the API (§6.3, §10.1, §16.6).
/// <para>
/// The half of §16.6 that is easy to get wrong is not the rows. <c>ON DELETE CASCADE</c> clears
/// those and does it well; it reaches nothing on the filesystem, so a deletion that trusts it
/// leaves the photographs behind — a privacy failure that presents as a storage bill.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AccountDeletionTests(PostgresFixture postgres)
{
	private const string MeUrl = "/api/v1/me";

	/// <summary>
	/// The thread is authored content and §10.1 keeps it — <em>while the author has an account</em>.
	/// Erasure means the posts, the reactions and the votes, not only the row that names them.
	/// </summary>
	[Fact]
	public async Task AccountDeleted_RemovesCommentsReactionsAndVotes()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient organiser = await SignedInAsync(app, "Organiser");
		using HttpClient leaver = await SignedInAsync(app, "DaveSmith");

		RideDetail ride = await RideAsync(app, organiser, leaver);

		// A poll from the organiser, so the leaver has a vote on somebody else's comment — the
		// row that a naive "delete their comments" would miss entirely.
		CommentDto poll = await PostAsync(
			organiser,
			ride.Id,
			new PostCommentRequest(
				Guid.NewGuid(),
				"Café or pub?",
				Poll: new PollSpec(["Café", "Pub"])));

		CommentDto post = await PostAsync(
			leaver,
			ride.Id,
			new PostCommentRequest(Guid.NewGuid(), "On my way"));

		using HttpResponseMessage reacted = await leaver.PutAsJsonAsync(
			$"/api/v1/comments/{poll.Id}/reaction",
			new SetReactionRequest("like"));

		reacted.EnsureSuccessStatusCode();

		using HttpResponseMessage voted = await leaver.PostAsJsonAsync(
			$"/api/v1/comments/{poll.Id}/votes",
			new CastVoteRequest([poll.Poll!.Options[0].Id]));

		voted.EnsureSuccessStatusCode();

		(await CountAsync<RideComment>(app)).ShouldBe(2);
		(await CountAsync<CommentReaction>(app)).ShouldBe(1);
		(await CountAsync<PollVote>(app)).ShouldBe(1);

		await DeleteAsync(leaver);

		(await CountAsync<RideComment>(app)).ShouldBe(1, "the organiser's poll is not theirs to take");
		(await ExistsAsync<RideComment>(app, post.Id)).ShouldBeFalse();

		(await CountAsync<CommentReaction>(app)).ShouldBe(
			0,
			"a reaction on somebody else's post is still the leaver's data");

		(await CountAsync<PollVote>(app)).ShouldBe(
			0,
			"and so is a vote — a tally that still counted them would be counting a person who " +
			"no longer exists");
	}

	/// <summary>
	/// The test §16.6 is written for. <c>ON DELETE CASCADE</c> reaches rows and not a filesystem,
	/// so the blob has to be deleted explicitly — and the list of which blobs has to be gathered
	/// <em>before</em> the rows that name them are gone.
	/// </summary>
	[Fact]
	public async Task Photo_AccountDeleted_RemovesBlobsFromObjectStorage()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient leaver = await SignedInAsync(app, "DaveSmith");

		PhotoUploaded photo = await UploadPhotoAsync(leaver);

		(string full, string thumb) = await app.WithDatabaseAsync(async database =>
		{
			Photo row = await database.Set<Photo>().SingleAsync(entity => entity.Id == photo.PhotoId);

			return (row.BlobRef, row.ThumbBlobRef);
		});

		(await BlobExistsAsync(app, full)).ShouldBeTrue();
		(await BlobExistsAsync(app, thumb)).ShouldBeTrue();

		await DeleteAsync(leaver);

		(await BlobExistsAsync(app, full)).ShouldBeFalse();

		(await BlobExistsAsync(app, thumb)).ShouldBeFalse(
			"a photo is two files, and a delete that reads only BlobRef leaves every thumbnail " +
			"on the disk being backed up");
	}

	/// <summary>
	/// §15.6 says the retained original is deleted with the account, and it is the one blob most
	/// easily missed: it hangs off the track rather than off the account, so a delete scoped by
	/// <c>OwnerId</c> alone never sees it.
	/// </summary>
	[Fact]
	public async Task AccountDeletion_CascadesTrackRevisions()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient leaver = await SignedInAsync(app, "DaveSmith");

		Guid trackId = await UploadTrackAsync(leaver);

		await EditAsync(leaver, trackId);

		string revisionBlob = await app.WithDatabaseAsync(database =>
			database.Set<TrackRevision>()
				.Where(revision => revision.TrackId == trackId)
				.Select(revision => revision.BlobRef)
				.SingleAsync());

		(await BlobExistsAsync(app, revisionBlob)).ShouldBeTrue();

		await DeleteAsync(leaver);

		(await CountAsync<TrackRevision>(app)).ShouldBe(0);
		(await CountAsync<Track>(app)).ShouldBe(0);

		(await BlobExistsAsync(app, revisionBlob)).ShouldBeFalse(
			"the pre-edit original is reached through the track, not through the account — a " +
			"query scoped only by OwnerId never sees it");
	}

	/// <summary>
	/// Scoping, and it is the mistake that destroys somebody else's data without leaving a trace.
	/// A blob list gathered without the owner filter deletes every retained original and every
	/// photograph on the server.
	/// </summary>
	[Fact]
	public async Task AccountDeletion_LeavesOtherAccountsBlobsAlone()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient leaver = await SignedInAsync(app, "DaveSmith");
		using HttpClient stayer = await SignedInAsync(app, "SamJones");

		Guid stayerTrack = await UploadTrackAsync(stayer);
		await EditAsync(stayer, stayerTrack);

		PhotoUploaded stayerPhoto = await UploadPhotoAsync(stayer);

		await UploadTrackAsync(leaver);
		await UploadPhotoAsync(leaver);

		string stayerRevision = await app.WithDatabaseAsync(database =>
			database.Set<TrackRevision>()
				.Where(revision => revision.TrackId == stayerTrack)
				.Select(revision => revision.BlobRef)
				.SingleAsync());

		string stayerImage = await app.WithDatabaseAsync(database =>
			database.Set<Photo>()
				.Where(photo => photo.Id == stayerPhoto.PhotoId)
				.Select(photo => photo.BlobRef)
				.SingleAsync());

		await DeleteAsync(leaver);

		(await BlobExistsAsync(app, stayerRevision)).ShouldBeTrue();
		(await BlobExistsAsync(app, stayerImage)).ShouldBeTrue();

		(await CountAsync<Track>(app)).ShouldBe(1);
		(await CountAsync<Photo>(app)).ShouldBe(1);
	}

	/// <summary>
	/// SRV-31 left <c>user_block.blocked_id</c> as <c>NO ACTION</c> — two cascade paths into
	/// <c>asp_net_users</c> through one table is an error in PostgreSQL. Nothing but this endpoint
	/// and the nightly sweep ever meets that constraint, and an unhandled violation fails the whole
	/// deletion rather than skipping a row.
	/// </summary>
	[Fact]
	public async Task AccountDeletion_WhenSomebodyElseHasBlockedThem_StillSucceeds()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient blocker = await SignedInAsync(app, "Blocker");
		using HttpClient leaver = await SignedInAsync(app, "DaveSmith");

		Guid leaverId = await UserIdAsync(app, "DaveSmith");

		using HttpResponseMessage blocked = await blocker.PostAsJsonAsync(
			"/api/v1/blocks",
			new BlockUserRequest(leaverId));

		blocked.EnsureSuccessStatusCode();

		await DeleteAsync(leaver);

		(await ExistsUserAsync(app, "DaveSmith")).ShouldBeFalse();
	}

	/// <summary>
	/// A stolen fifteen-minute access token should not be enough to end somebody's account. Every
	/// account has a password (§7.2), so requiring it excludes nobody.
	/// </summary>
	[Fact]
	public async Task AccountDeletion_WithoutTheCurrentPassword_IsRefused()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		using HttpRequestMessage request = new(HttpMethod.Delete, MeUrl)
		{
			Content = JsonContent.Create(new DeleteAccountRequest("not-the-password")),
		};

		using HttpResponseMessage refused = await rider.SendAsync(request);

		refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		(await ExistsUserAsync(app, "DaveSmith")).ShouldBeTrue();
	}

	/// <summary>
	/// The username goes back to the pool, exactly as the §7.11 sweep's does — and for a rider who
	/// chose to leave, the same reasoning does not apply, so this is worth pinning rather than
	/// assuming. A hard delete is a hard delete.
	/// </summary>
	[Fact]
	public async Task AccountDeletion_ReleasesTheUsername()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		await DeleteAsync(rider);

		using HttpClient fresh = app.CreateClient().From("203.0.113.77");

		using HttpResponseMessage registered = await fresh.PostRegisterAsync("DaveSmith");

		registered.StatusCode.ShouldBe(HttpStatusCode.Created);
	}

	private static async Task DeleteAsync(HttpClient client)
	{
		using HttpRequestMessage request = new(HttpMethod.Delete, MeUrl)
		{
			Content = JsonContent.Create(new DeleteAccountRequest(TestRegistration.ValidPassword)),
		};

		using HttpResponseMessage response = await client.SendAsync(request);

		response.StatusCode.ShouldBe(
			HttpStatusCode.NoContent,
			await response.Content.ReadAsStringAsync());
	}

	internal static async Task<HttpClient> SignedInAsync(DlrWebApplicationFactory app, string userName)
	{
		using HttpClient registrar = app.CreateClient().From($"198.51.100.{Random.Shared.Next(1, 250)}");

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}

	internal static async Task<RideDetail> RideAsync(
		DlrWebApplicationFactory app,
		HttpClient organiser,
		params HttpClient[] members)
	{
		using HttpResponseMessage created = await organiser.PostAsJsonAsync(
			"/api/v1/group-rides",
			new CreateRideRequest("Sunday", app.Clock.GetUtcNow().AddHours(1), JoinPolicy: JoinPolicyDto.Open));

		created.EnsureSuccessStatusCode();

		RideDetail ride = (await created.Content.ReadFromJsonAsync<RideDetail>())!;

		foreach (HttpClient member in members)
		{
			using HttpResponseMessage joined = await member.PostAsJsonAsync(
				"/api/v1/group-rides/join",
				new JoinByCodeRequest(ride.JoinCode!));

			joined.EnsureSuccessStatusCode();
		}

		return ride;
	}

	internal static async Task<CommentDto> PostAsync(
		HttpClient client,
		Guid rideId,
		PostCommentRequest request)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			$"/api/v1/group-rides/{rideId}/comments",
			request);

		response.EnsureSuccessStatusCode();

		return (await response.Content.ReadFromJsonAsync<CommentDto>())!;
	}

	internal static async Task<PhotoUploaded> UploadPhotoAsync(HttpClient client)
	{
		using MultipartFormDataContent form = [];
		using ByteArrayContent file = new(ImageFixtures.Jpeg(400, 300));

		file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

		form.Add(file, "file", "photo.jpg");

		using HttpResponseMessage response = await client.PostAsync("/api/v1/photos", form);

		response.EnsureSuccessStatusCode();

		return (await response.Content.ReadFromJsonAsync<PhotoUploaded>())!;
	}

	internal static async Task<Guid> UploadTrackAsync(HttpClient client)
	{
		using HttpResponseMessage response = await client.PostAsync(
			"/api/v1/tracks/import",
			GpxForm(GpxFixtures.SingleTrack()));

		response.EnsureSuccessStatusCode();

		TrackImportResult result = (await response.Content.ReadFromJsonAsync<TrackImportResult>())!;

		return result.Tracks[0].TrackId!.Value;
	}

	/// <summary>Trims a point off, which is what puts a row in <c>track_revision</c> (§15.6).</summary>
	internal static async Task EditAsync(HttpClient client, Guid trackId)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync(
			$"/api/v1/tracks/{trackId}/edit",
			new EditTrackRequest(1, [new IndexRange(0, 1)]));

		response.EnsureSuccessStatusCode();
	}

	private static MultipartFormDataContent GpxForm(string gpx)
	{
		MultipartFormDataContent form = [];
		ByteArrayContent file = new(System.Text.Encoding.UTF8.GetBytes(gpx));

		file.Headers.ContentType = new MediaTypeHeaderValue("application/gpx+xml");

		form.Add(file, "file", "ride.gpx");

		return form;
	}

	internal static async Task<bool> BlobExistsAsync(DlrWebApplicationFactory app, string blobRef)
	{
		using IServiceScope scope = app.Services.CreateScope();

		return await scope.ServiceProvider.GetRequiredService<IBlobStore>().ExistsAsync(blobRef);
	}

	private static Task<int> CountAsync<T>(DlrWebApplicationFactory app)
		where T : class =>
		app.WithDatabaseAsync(database => database.Set<T>().CountAsync());

	private static Task<bool> ExistsAsync<T>(DlrWebApplicationFactory app, Guid id)
		where T : class =>
		app.WithDatabaseAsync(database =>
			database.Set<T>().AnyAsync(entity => EF.Property<Guid>(entity, "Id") == id));

	private static Task<bool> ExistsUserAsync(DlrWebApplicationFactory app, string userName) =>
		app.WithDatabaseAsync(database => database.Users.AnyAsync(user => user.UserName == userName));

	internal static Task<Guid> UserIdAsync(DlrWebApplicationFactory app, string userName) =>
		app.WithDatabaseAsync(database =>
			database.Users.Where(user => user.UserName == userName).Select(user => user.Id).SingleAsync());
}
