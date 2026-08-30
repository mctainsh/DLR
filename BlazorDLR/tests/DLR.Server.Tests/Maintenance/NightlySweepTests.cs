using System.Net.Http.Headers;
using System.Net.Http.Json;
using DLR.Core.Contracts.Identity;
using DLR.Core.Contracts.Photos;
using DLR.Server.Data;
using DLR.Server.Data.Identity;
using DLR.Server.Data.Moderation;
using DLR.Server.Data.Photos;
using DLR.Server.Data.Rides;
using DLR.Server.Data.Tracks;
using DLR.Server.Maintenance;
using DLR.Server.Tracks;
using DLR.TestSupport.Database;
using DLR.TestSupport.Hosting;
using DLR.TestSupport.Identity;
using DLR.TestSupport.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace DLR.Server.Tests.Maintenance;

/// <summary>
/// The six sweeps that are not about accounts (§7.11).
/// <para>
/// They share the job because they share the risk, not because they share a feature: a stale
/// position, an expired undo, a photo nobody points at and a resolved report are four unrelated
/// things that are all deleted on a timer with nobody watching.
/// </para>
/// </summary>
public sealed class NightlySweepTests(PostgresFixture postgres)
{
	private static Dictionary<string, string?> Live => new()
	{
		["Maintenance:DryRun"] = "false",
	};

	private static Dictionary<string, string?> DryRun => new()
	{
		["Maintenance:DryRun"] = "true",
	};

	/// <summary>
	/// §15.6's undo window, enforced (§7.11). The API already refuses to undo past
	/// <c>PurgeAfterUtc</c> — this is what makes the bytes go, which is the half the rider who
	/// trimmed their home address off a track actually asked for.
	/// </summary>
	[Fact]
	public async Task NightlySweep_PurgesRevisionsPastPurgeAfterUtc()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: Live);

		Guid ownerId = await AccountAsync(app, "DaveSmith");

		string expiredBlob = await PutBlobAsync(app, "expired");
		string liveBlob = await PutBlobAsync(app, "still-undoable");

		Guid expiredTrack = await TrackAsync(app, ownerId, await PutBlobAsync(app, "track-one"));
		Guid liveTrack = await TrackAsync(app, ownerId, await PutBlobAsync(app, "track-two"));

		await app.WithDatabaseAsync(async database =>
		{
			database.Add(new TrackRevision
			{
				TrackId = expiredTrack,
				Version = 1,
				BlobRef = expiredBlob,
				ReplacedUtc = app.Clock.GetUtcNow().AddDays(-8),
				PurgeAfterUtc = app.Clock.GetUtcNow().AddDays(-1),
			});

			database.Add(new TrackRevision
			{
				TrackId = liveTrack,
				Version = 1,
				BlobRef = liveBlob,
				ReplacedUtc = app.Clock.GetUtcNow(),
				PurgeAfterUtc = app.Clock.GetUtcNow().AddDays(7),
			});

			await database.SaveChangesAsync();
		});

		MaintenanceReport report = await app.RunMaintenanceAsync();

		report.RevisionsPurged.ShouldBe(1);

		(await RevisionExistsAsync(app, expiredTrack)).ShouldBeFalse();
		(await RevisionExistsAsync(app, liveTrack)).ShouldBeTrue("its window has not closed");

		// The row and the bytes, not just the row. A purge that dropped the pointer and left the
		// blob would leave the trimmed-off points on the disk being backed up (§15.6).
		(await BlobExistsAsync(app, expiredBlob)).ShouldBeFalse();
		(await BlobExistsAsync(app, liveBlob)).ShouldBeTrue();
	}

	/// <summary>
	/// <c>ON DELETE CASCADE</c> does not reach the filesystem (§16.6), so the only way to find a
	/// blob nothing points at is to enumerate what is there and subtract what is referenced.
	/// </summary>
	[Fact]
	public async Task NightlySweep_DeletesOrphanedPhotoBlobs()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: Live);

		using HttpClient rider = await SignedInAsync(app, "DaveSmith");

		PhotoUploaded orphaned = await UploadPhotoAsync(rider);
		PhotoUploaded kept = await UploadPhotoAsync(rider);

		(string orphanedFull, string orphanedThumb) = await BlobRefsAsync(app, orphaned.PhotoId);
		(string keptFull, string keptThumb) = await BlobRefsAsync(app, kept.PhotoId);

		// Exactly what an account deletion or a cascade leaves behind: the row is gone and the two
		// files are still on the volume.
		await app.WithDatabaseAsync(database =>
			database.Set<Photo>()
				.Where(photo => photo.Id == orphaned.PhotoId)
				.ExecuteDeleteAsync());

		// Past the grace window. Inside it every blob written in the last day looks like an orphan,
		// because the row that points at it is committed after the bytes are. Advancing the fake
		// clock is enough because the store stamps what it writes from that same clock.
		app.Clock.Advance(TimeSpan.FromHours(25));

		MaintenanceReport report = await app.RunMaintenanceAsync();

		report.OrphanBlobsDeleted.ShouldBe(2, "the stored image and its thumbnail");

		(await BlobExistsAsync(app, orphanedFull)).ShouldBeFalse();
		(await BlobExistsAsync(app, orphanedThumb)).ShouldBeFalse();

		(await BlobExistsAsync(app, keptFull)).ShouldBeTrue();
		(await BlobExistsAsync(app, keptThumb)).ShouldBeTrue(
			"the thumbnail is a second blob on the same row — a sweep that only read blob_ref " +
			"would take every thumbnail in the store");
	}

	/// <summary>
	/// The grace window is the whole safety of the orphan sweep. A blob is written before the row
	/// that points at it is committed, so for the width of one request every new upload is
	/// indistinguishable from an orphan — and taking one would delete a photograph out from under
	/// the request that was still uploading it.
	/// </summary>
	[Fact]
	public async Task NightlySweep_LeavesUnreferencedBlobsInsideTheGraceWindow()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: Live);

		// No row points at this at any moment of the test. It survives purely because it is new.
		string justWritten = await PutBlobAsync(app, "mid-request");

		MaintenanceReport report = await app.RunMaintenanceAsync();

		report.OrphanBlobsDeleted.ShouldBe(0);
		(await BlobExistsAsync(app, justWritten)).ShouldBeTrue();
	}

	/// <summary>
	/// The sweep asks the model which columns hold blob references rather than carrying a list
	/// somebody has to remember to extend.
	/// <para>
	/// <strong>This is the guard on the most dangerous code in the project.</strong> A blob column
	/// the sweep does not know about is not a missed tidy-up — every value in it is unreferenced as
	/// far as the sweep can tell, so the next run deletes all of them. It reddens the moment a new
	/// blob-bearing column is added, which is the moment somebody can still think about it.
	/// </para>
	/// </summary>
	[Fact]
	public async Task OrphanSweep_CoversEveryBlobColumnInTheModel()
	{
		await using DlrWebApplicationFactory app = await DlrWebApplicationFactory.CreateAsync(postgres);

		using IServiceScope scope = app.Services.CreateScope();

		IModel model = scope.ServiceProvider.GetRequiredService<DlrDbContext>().Model;

		List<string> covered =
		[
			.. BlobReferences.InModel(model).Select(column => $"{column.Table}.{column.Column}"),
		];

		List<string> everyBlobLookingColumn =
		[
			.. from entity in model.GetEntityTypes()
			   from property in entity.GetProperties()
			   where property.ClrType == typeof(string)
				   && property.Name.Contains("Blob", StringComparison.Ordinal)
			   select $"{entity.GetTableName()}.{property.GetColumnName()}",
		];

		covered.Order().ShouldBe(
			everyBlobLookingColumn.Order(),
			"every column that holds a blob reference has to be subtracted from the candidate " +
			"set, or the sweep deletes the blobs it names");

		covered.Count.ShouldBe(4, "track, track_revision, and a photo's two files");
	}

	/// <summary>
	/// §7.13's row hygiene. Sessions do not expire in practice, so what accumulates here is the
	/// revoked and the genuinely expired — a chain per sign-in, forever, on a €4 VPS.
	/// </summary>
	[Fact]
	public async Task NightlySweep_DeletesLongRevokedRefreshTokens()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: Live);

		using HttpClient client = app.CreateClient().From("203.0.113.60");

		await client.RegisterAsync("DaveSmith");

		Guid deviceId = await app.WithDatabaseAsync(database =>
			database.Set<Device>().Select(device => device.Id).SingleAsync());

		Guid userId = await app.WithDatabaseAsync(database =>
			database.Users.Select(user => user.Id).SingleAsync());

		await app.WithDatabaseAsync(async database =>
		{
			// The registration's own token is cleared out first so the three below are the whole
			// population and the counts mean what they say.
			await database.Set<RefreshToken>().ExecuteDeleteAsync();

			database.Add(Token(userId, deviceId, app, revokedDaysAgo: 31));
			database.Add(Token(userId, deviceId, app, revokedDaysAgo: 3));
			database.Add(Token(userId, deviceId, app, revokedDaysAgo: null));

			await database.SaveChangesAsync();
		});

		MaintenanceReport report = await app.RunMaintenanceAsync();

		report.RefreshTokensDeleted.ShouldBe(1);

		(await TokenCountAsync(app)).ShouldBe(
			2,
			"a token revoked three days ago is still the explanation for why a device stopped " +
			"working, and the live one is somebody's session");
	}

	/// <summary>
	/// §17.7's retention. The snapshot is a copy of content that may have been deleted everywhere
	/// else, so keeping it forever is its own privacy problem — and only <em>resolved</em> reports
	/// age out, because ageing out a backlog is a silent amnesty.
	/// </summary>
	[Fact]
	public async Task NightlySweep_PurgesResolvedReportsPastRetention()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: Live);

		Guid reporterId = await AccountAsync(app, "DaveSmith");

		await app.WithDatabaseAsync(async database =>
		{
			database.Add(Report(reporterId, app, resolvedDaysAgo: 91));
			database.Add(Report(reporterId, app, resolvedDaysAgo: 10));
			database.Add(Report(reporterId, app, resolvedDaysAgo: null));

			await database.SaveChangesAsync();
		});

		MaintenanceReport report = await app.RunMaintenanceAsync();

		report.ReportsPurged.ShouldBe(1);

		List<DateTimeOffset?> remaining = await app.WithDatabaseAsync(database =>
			database.Set<ContentReport>()
				.Select(row => row.ResolvedUtc)
				.ToListAsync());

		remaining.Count.ShouldBe(2);

		remaining.ShouldContain(
			(DateTimeOffset?)null,
			"an open report is somebody still waiting on an answer, whatever its age");
	}

	/// <summary>
	/// SRV-31 left this one FK to this task: <c>user_block.blocked_id</c> is <c>NoAction</c>,
	/// because two cascade paths into <c>asp_net_users</c> through one table is an error in
	/// PostgreSQL. Nothing else in the project deletes an account, so nothing else has ever hit it
	/// — and an unhandled FK violation here does not skip one account, it aborts the whole sweep.
	/// </summary>
	[Fact]
	public async Task Cleanup_AccountSomebodyElseHasBlocked_IsStillDeleted()
	{
		await using DlrWebApplicationFactory app =
			await DlrWebApplicationFactory.CreateAsync(postgres, settings: Live);

		Guid blockerId = await AccountAsync(app, "Blocker");
		Guid blockedId = await AccountAsync(app, "Blocked");

		await app.WithDatabaseAsync(async database =>
		{
			database.Add(new UserBlock
			{
				BlockerId = blockerId,
				BlockedId = blockedId,
				CreatedUtc = app.Clock.GetUtcNow(),
			});

			AppUser blocked = await database.Users.SingleAsync(user => user.Id == blockedId);
			blocked.LastActiveUtc = app.Clock.GetUtcNow().AddDays(-200);

			await database.SaveChangesAsync();
		});

		MaintenanceReport report = await app.RunMaintenanceAsync();

		report.AccountsDeleted.ShouldBe(1);

		bool blockSurvives = await app.WithDatabaseAsync(database =>
			database.Set<UserBlock>().AnyAsync(block => block.BlockedId == blockedId));

		blockSurvives.ShouldBeFalse("a block on an account that no longer exists is not a block");
	}

	private static async Task<Guid> AccountAsync(DlrWebApplicationFactory app, string userName)
	{
		using HttpClient client = app.CreateClient().From($"198.51.100.{Random.Shared.Next(1, 250)}");

		await client.RegisterAsync(userName);

		return await app.WithDatabaseAsync(database =>
			database.Users.Where(user => user.UserName == userName).Select(user => user.Id).SingleAsync());
	}

	private static async Task<HttpClient> SignedInAsync(DlrWebApplicationFactory app, string userName)
	{
		using HttpClient registrar = app.CreateClient().From($"198.51.100.{Random.Shared.Next(1, 250)}");

		TokenResponse session = await registrar.RegisterAsync(userName);

		return app.CreateClient().Authenticated(session);
	}

	private static async Task<PhotoUploaded> UploadPhotoAsync(HttpClient client)
	{
		using MultipartFormDataContent form = [];
		using ByteArrayContent file = new(ImageFixtures.Jpeg(400, 300));

		file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

		form.Add(file, "file", "photo.jpg");

		using HttpResponseMessage response = await client.PostAsync("/api/v1/photos", form);

		response.EnsureSuccessStatusCode();

		return (await response.Content.ReadFromJsonAsync<PhotoUploaded>())!;
	}

	private static Task<(string Full, string Thumb)> BlobRefsAsync(
		DlrWebApplicationFactory app,
		Guid photoId) =>
		app.WithDatabaseAsync(async database =>
		{
			Photo photo = await database.Set<Photo>().SingleAsync(row => row.Id == photoId);

			return (photo.BlobRef, photo.ThumbBlobRef);
		});

	/// <summary>Writes a blob straight to the store, with no row anywhere pointing at it.</summary>
	private static async Task<string> PutBlobAsync(DlrWebApplicationFactory app, string content)
	{
		using IServiceScope scope = app.Services.CreateScope();

		IBlobStore blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();

		using MemoryStream bytes = new(System.Text.Encoding.UTF8.GetBytes(content));

		return await blobs.PutAsync(bytes);
	}

	private static async Task<bool> BlobExistsAsync(DlrWebApplicationFactory app, string blobRef)
	{
		using IServiceScope scope = app.Services.CreateScope();

		return await scope.ServiceProvider.GetRequiredService<IBlobStore>().ExistsAsync(blobRef);
	}

	private static Task<Guid> TrackAsync(DlrWebApplicationFactory app, Guid ownerId, string blobRef) =>
		app.WithDatabaseAsync(async database =>
		{
			Track track = new()
			{
				Id = Guid.NewGuid(),
				OwnerId = ownerId,
				ClientGuid = Guid.NewGuid(),
				CreatedUtc = app.Clock.GetUtcNow(),
				BlobRef = blobRef,
				ContentHash = [1, 2, 3],
			};

			database.Add(track);

			await database.SaveChangesAsync();

			return track.Id;
		});

	private static Task<Guid> RideAsync(DlrWebApplicationFactory app, Guid ownerId) =>
		app.WithDatabaseAsync(async database =>
		{
			GroupRide ride = new()
			{
				Id = Guid.NewGuid(),
				OwnerId = ownerId,
				Name = "Sunday",
				StartUtc = app.Clock.GetUtcNow(),
				JoinCode = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
				CreatedUtc = app.Clock.GetUtcNow(),
			};

			database.Add(ride);

			database.Add(new GroupRideMember
			{
				GroupRideId = ride.Id,
				UserId = ownerId,
				Role = GroupRideRole.Owner,
				JoinedUtc = app.Clock.GetUtcNow(),
				ShareLocation = true,
			});

			await database.SaveChangesAsync();

			return ride.Id;
		});

	private static RefreshToken Token(
		Guid userId,
		Guid deviceId,
		DlrWebApplicationFactory app,
		int? revokedDaysAgo) =>
		new()
		{
			Id = Guid.NewGuid(),
			UserId = userId,
			DeviceId = deviceId,
			FamilyId = Guid.NewGuid(),
			TokenHash = Guid.NewGuid().ToByteArray(),
			IssuedUtc = app.Clock.GetUtcNow().AddDays(-400),
			ExpiresUtc = app.Clock.GetUtcNow().AddYears(9),
			RevokedUtc = revokedDaysAgo is { } days ? app.Clock.GetUtcNow().AddDays(-days) : null,
			RevokedReason = revokedDaysAgo is null ? null : RevocationReasons.SignedOut,
		};

	private static ContentReport Report(
		Guid reporterId,
		DlrWebApplicationFactory app,
		int? resolvedDaysAgo) =>
		new()
		{
			Id = Guid.NewGuid(),
			TargetKind = ReportTargetKind.Comment,
			TargetId = Guid.NewGuid(),
			ReportedByUserId = reporterId,
			Reason = "Abusive",
			ContentSnapshot = "what it said at the time",
			CreatedUtc = app.Clock.GetUtcNow().AddDays(-200),
			ResolvedUtc = resolvedDaysAgo is { } days ? app.Clock.GetUtcNow().AddDays(-days) : null,
		};

	private static Task<bool> RevisionExistsAsync(DlrWebApplicationFactory app, Guid trackId) =>
		app.WithDatabaseAsync(database =>
			database.Set<TrackRevision>().AnyAsync(revision => revision.TrackId == trackId));

	private static Task<int> TokenCountAsync(DlrWebApplicationFactory app) =>
		app.WithDatabaseAsync(database => database.Set<RefreshToken>().CountAsync());
}
