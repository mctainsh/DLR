using DLR.Server.Data;
using DLR.Server.Data.Identity;
using DLR.Server.Data.Moderation;
using DLR.Server.Data.Positions;
using DLR.Server.Data.Rides;
using DLR.Server.Data.Tracks;
using DLR.Server.Diagnostics;
using DLR.Server.Identity;
using DLR.Server.Moderation;
using DLR.Server.Positions;
using DLR.Server.Tracks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DLR.Server.Maintenance;

/// <summary>
/// The one nightly job, carrying all seven sweeps (§7.11).
/// <para>
/// <strong>One service rather than seven.</strong> They have nothing in common as features — a
/// dormant account, an idle position, an expired undo, a photo nobody points at — and everything in
/// common as risk: each is destructive, each runs on a timer, and none of them has anybody watching
/// when it fires. Sharing the job means they share the dry run, the kill switch and the batch cap,
/// which is the part that actually matters.
/// </para>
/// <para>
/// <strong>Every sweep is its own failure.</strong> One that throws is logged and the rest still
/// run: a blob volume that has gone read-only must not be the reason nobody's <c>created_by_ip</c>
/// was cleared for a fortnight.
/// </para>
/// </summary>
/// <param name="scopes">A scope per run.</param>
/// <param name="clock">The project's clock — every horizon here is measured against it (§10.4).</param>
/// <param name="options">The brakes.</param>
/// <param name="moderation">§17.7's retention, which this job enforces and does not own.</param>
/// <param name="logger">Where a dry run's output is read, and where a failure is recorded.</param>
public sealed class NightlyMaintenanceService(
	IServiceScopeFactory scopes,
	TimeProvider clock,
	IOptions<MaintenanceOptions> options,
	IOptions<ModerationOptions> moderation,
	ILogger<NightlyMaintenanceService> logger) : BackgroundService
{
	/// <inheritdoc />
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		double hours = options.Value.IntervalHours;

		if (hours <= 0)
		{
			// No timer at all — for a deployment that would rather drive this from cron, and for
			// the test suite, which calls RunAsync directly. Advancing a fake clock a whole day
			// into a PeriodicTimer is a race twice over, and SRV-22 already paid for finding out.
			logger.LogInformation("Nightly maintenance is not on a timer; run it externally.");

			return;
		}

		using PeriodicTimer timer = new(TimeSpan.FromHours(hours), clock);

		try
		{
			while (await timer.WaitForNextTickAsync(stoppingToken))
			{
				await RunAsync(stoppingToken);
			}
		}
		catch (OperationCanceledException)
		{
			// Shutdown. Every horizon is stored rather than held in memory, so the next process
			// start picks up exactly where this one would have.
		}
	}

	/// <summary>Runs every sweep once. Public so a test — or an operator — can drive one run.</summary>
	/// <param name="cancellationToken">Abandons the run.</param>
	public async Task<MaintenanceReport> RunAsync(CancellationToken cancellationToken = default)
	{
		await using AsyncServiceScope scope = scopes.CreateAsyncScope();

		MaintenanceOptions settings = options.Value;
		DlrDbContext database = scope.ServiceProvider.GetRequiredService<DlrDbContext>();
		DateTimeOffset now = clock.GetUtcNow();

		logger.LogInformation(
			"Nightly maintenance starting at {Now} ({Mode}).",
			now,
			settings.DryRun ? "dry run — nothing will be changed" : "live");

		IReadOnlyList<InactiveAccount> candidates = await SweepAsync(
			"inactive accounts",
			() => CandidatesAsync(database, settings, now, cancellationToken),
			(IReadOnlyList<InactiveAccount>)[]);

		foreach (InactiveAccount candidate in candidates)
		{
			// Named, one per line. "Seven accounts would be deleted" is not something an operator
			// can check; seven usernames and their last-active dates are.
			logger.LogInformation(
				"Inactive account {UserName} ({UserId}), last active {LastActiveUtc}: {Verdict}.",
				candidate.UserName,
				candidate.Id,
				candidate.LastActiveUtc,
				settings.DryRun ? "would be deleted" : "deleting");
		}

		MaintenanceReport report = new()
		{
			WasDryRun = settings.DryRun,
			InactiveCandidates = candidates,

			AccountsDeleted = await SweepAsync(
				"account deletion",
				() => DeleteAccountsAsync(database, settings, now, candidates, cancellationToken),
				0),

			AccountsWarned = await SweepAsync(
				"inactivity warnings",
				() => WarnAsync(scope, database, settings, now, candidates, cancellationToken),
				0),

			RegistrationIpsCleared = await SweepAsync(
				"registration addresses",
				() => ClearRegistrationIpsAsync(database, settings, now, cancellationToken),
				0),

			PositionsDeleted = await SweepAsync(
				"idle positions",
				() => DeleteIdlePositionsAsync(scope, settings, now),
				0),

			OrphanedPositionsDeleted = await SweepAsync(
				"orphaned positions",
				() => DeleteOrphanedPositionsAsync(scope, settings),
				0),

			RefreshTokensDeleted = await SweepAsync(
				"refresh tokens",
				() => DeleteDeadTokensAsync(database, settings, now, cancellationToken),
				0),

			RevisionsPurged = await SweepAsync(
				"track revisions",
				() => PurgeRevisionsAsync(scope, database, settings, now, cancellationToken),
				0),

			ReportsPurged = await SweepAsync(
				"resolved reports",
				() => PurgeReportsAsync(database, settings, now, cancellationToken),
				0),

			OrphanBlobsDeleted = await SweepAsync(
				"orphaned blobs",
				() => DeleteOrphanBlobsAsync(scope, database, settings, now, cancellationToken),
				0),

			LogFilesDeleted = await SweepAsync(
				"log files",
				() => Task.FromResult(DeleteOldLogFiles(scope, settings, now)),
				0),
		};

		logger.LogInformation(
			"Nightly maintenance finished: {Deleted} accounts deleted, {Warned} warned, " +
			"{Ips} addresses cleared, {Positions} positions, {Tokens} refresh tokens, " +
			"{Revisions} revisions, {Reports} reports, {Blobs} blobs, {Logs} log files. " +
			"{Orphans} positions belonged to a rider who was not sharing.",
			report.AccountsDeleted,
			report.AccountsWarned,
			report.RegistrationIpsCleared,
			report.PositionsDeleted,
			report.RefreshTokensDeleted,
			report.RevisionsPurged,
			report.ReportsPurged,
			report.OrphanBlobsDeleted,
			report.LogFilesDeleted,
			report.OrphanedPositionsDeleted);

		await SweepAsync("run alert", () => AlertAsync(scope, settings, report, now), false);

		return report;
	}

	/// <summary>
	/// Emails the run summary, when an address is configured (§9).
	/// <para>
	/// Its own <c>SweepAsync</c> wrapper, so a mail transport that is down cannot make a run that
	/// did its work report as a failure. The whole point of the alert is the reverse.
	/// </para>
	/// </summary>
	private static async Task<bool> AlertAsync(
		AsyncServiceScope scope,
		MaintenanceOptions settings,
		MaintenanceReport report,
		DateTimeOffset now)
	{
		if (string.IsNullOrWhiteSpace(settings.AlertEmail))
		{
			return false;
		}

		string candidates = report.InactiveCandidates.Count == 0
			? "(none)"
			: string.Join(
				"\n",
				report.InactiveCandidates.Select(candidate =>
					$"  {candidate.UserName} — last active {candidate.LastActiveUtc:u}"));

		await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(
			new EmailMessage(
				settings.AlertEmail,
				$"Dumb Luck Routes nightly maintenance — {(report.WasDryRun ? "dry run" : "live")}",
				$"""
				Run at {now:u}.

				Accounts deleted:        {report.AccountsDeleted}
				Accounts warned:         {report.AccountsWarned}
				Registration IPs nulled: {report.RegistrationIpsCleared}
				Positions removed:       {report.PositionsDeleted}
				Not-sharing positions:   {report.OrphanedPositionsDeleted}
				Refresh tokens removed:  {report.RefreshTokensDeleted}
				Track revisions purged:  {report.RevisionsPurged}
				Reports purged:          {report.ReportsPurged}
				Orphaned blobs deleted:  {report.OrphanBlobsDeleted}
				Log files deleted:       {report.LogFilesDeleted}

				Inactive account candidates:
				{candidates}
				""".ReplaceLineEndings("\n")));

		return true;
	}

	/// <summary>
	/// §7.11's conjunction, and the safety property of the whole job.
	/// <para>
	/// <strong>One method, used by both the deletion and the warning.</strong> An account warned by
	/// one predicate and deleted by another is either warned and kept — noise — or deleted with no
	/// warning, which is the failure the notice at signup promises will not happen.
	/// </para>
	/// </summary>
	private static IQueryable<AppUser> HoldingNothing(DlrDbContext database, IQueryable<AppUser> users) =>
		users
			.Where(user => !database.Set<Track>().Any(track => track.OwnerId == user.Id))
			.Where(user => !database.Set<GroupRideMember>().Any(member => member.UserId == user.Id))
			.Where(user => !database.Set<GroupRide>().Any(ride => ride.OwnerId == user.Id))
			.Where(user => !database
				.Set<GroupRideJoinRequest>()
				.Any(request =>
					request.UserId == user.Id && request.Status == JoinRequestStatus.Pending));

	private static async Task<IReadOnlyList<InactiveAccount>> CandidatesAsync(
		DlrDbContext database,
		MaintenanceOptions settings,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		if (!settings.DeleteInactiveAccounts)
		{
			return [];
		}

		DateTimeOffset idleBefore = now.AddDays(-settings.InactiveDays);

		return await HoldingNothing(database, database.Users.Where(user => user.LastActiveUtc < idleBefore))
			.OrderBy(user => user.LastActiveUtc)
			.Take(settings.MaxDeletesPerRun)
			.Select(user => new InactiveAccount(user.Id, user.UserName!, user.LastActiveUtc))
			.ToListAsync(cancellationToken);
	}

	private async Task<int> DeleteAccountsAsync(
		DlrDbContext database,
		MaintenanceOptions settings,
		DateTimeOffset now,
		IReadOnlyList<InactiveAccount> candidates,
		CancellationToken cancellationToken)
	{
		if (settings.DryRun || candidates.Count == 0)
		{
			return 0;
		}

		List<Guid> ids = [.. candidates.Select(candidate => candidate.Id)];

		// The reason the next refresh can say what happened (§7.11). Written before the delete,
		// because after it the tokens are gone: the cascade takes refresh_token with the account.
		List<DeletedAccountToken> tombstones = await database
			.Set<RefreshToken>()
			.Where(token => ids.Contains(token.UserId) && token.RevokedUtc == null)
			.Select(token => new DeletedAccountToken { TokenHash = token.TokenHash, DeletedUtc = now })
			.ToListAsync(cancellationToken);

		database.AddRange(tombstones);
		await database.SaveChangesAsync(cancellationToken);

		// user_block.blocked_id is NoAction, not Cascade — two cascade paths into asp_net_users
		// through one table is a multiple-cascade-path error in PostgreSQL, so SRV-31 left this
		// side to this sweep. Nothing else in the project deletes an account, so nothing else has
		// ever hit the constraint, and an unhandled violation here does not skip one account: it
		// aborts the statement and the whole night's deletions with it.
		await database
			.Set<UserBlock>()
			.Where(block => ids.Contains(block.BlockedId))
			.ExecuteDeleteAsync(cancellationToken);

		// Hard delete. Every other table reaches asp_net_users through ON DELETE CASCADE, and
		// §7.11's criteria are what make that safe — an eligible account has never joined a ride,
		// so there is nothing of anybody else's hanging off it.
		return await database
			.Set<AppUser>()
			.Where(user => ids.Contains(user.Id))
			.ExecuteDeleteAsync(cancellationToken);
	}

	private async Task<int> WarnAsync(
		AsyncServiceScope scope,
		DlrDbContext database,
		MaintenanceOptions settings,
		DateTimeOffset now,
		IReadOnlyList<InactiveAccount> deleting,
		CancellationToken cancellationToken)
	{
		if (settings.DryRun)
		{
			return 0;
		}

		DateTimeOffset warnBefore = now.AddDays(-settings.WarnAfterDays);
		List<Guid> going = [.. deleting.Select(candidate => candidate.Id)];

		List<AppUser> toWarn = await HoldingNothing(
			database,
			database.Users.Where(user =>
				user.LastActiveUtc < warnBefore

				// Once, not once a night. The window is thirty days wide and the job is nightly,
				// so without this the courtesy is thirty emails and a blocked sending domain.
				&& user.InactivityWarnedUtc == null

				// Confirmed, per §7.11. An address that was typed and never confirmed may belong
				// to somebody who mistyped it — the same reason §7.7 will not send a reset to one.
				&& user.EmailConfirmed
				&& user.Email != null

				// Warning somebody the same run is about to delete is not a warning.
				&& !going.Contains(user.Id)))
			.OrderBy(user => user.LastActiveUtc)
			.Take(settings.MaxDeletesPerRun)
			.ToListAsync(cancellationToken);

		if (toWarn.Count == 0)
		{
			return 0;
		}

		AccountEmails emails = scope.ServiceProvider.GetRequiredService<AccountEmails>();

		int warned = 0;

		foreach (AppUser user in toWarn)
		{
			try
			{
				await emails.SendInactivityWarningAsync(
					user,
					user.LastActiveUtc.AddDays(settings.InactiveDays),
					cancellationToken);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				// Stamped only on success, so a transport failure is retried tomorrow rather than
				// swallowing somebody's only notice. Left loud: an account deleted after a warning
				// that never arrived is the worst outcome this job has.
				logger.LogError(exception, "Could not warn {UserId} about inactivity.", user.Id);

				continue;
			}

			user.InactivityWarnedUtc = now;
			warned++;
		}

		await database.SaveChangesAsync(cancellationToken);

		return warned;
	}

	private static async Task<int> ClearRegistrationIpsAsync(
		DlrDbContext database,
		MaintenanceOptions settings,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		if (settings.DryRun)
		{
			return await database.Users.CountAsync(
				user => user.CreatedByIp != null
					&& user.CreatedUtc < now.AddDays(-settings.RegistrationIpRetentionDays),
				cancellationToken);
		}

		DateTimeOffset before = now.AddDays(-settings.RegistrationIpRetentionDays);

		return await database
			.Users
			.Where(user => user.CreatedByIp != null && user.CreatedUtc < before)
			.ExecuteUpdateAsync(
				user => user.SetProperty(entity => entity.CreatedByIp, (System.Net.IPAddress?)null),
				cancellationToken);
	}

	/// <summary>
	/// Deletes daily log files past <see cref="FileLogOptions.RetainDays"/> (§14.6).
	/// <para>
	/// In the nightly job rather than in the log provider, because it is the same kind of thing as
	/// every other sweep here — a bounded amount of deletion, once a day, honouring the same
	/// <see cref="MaintenanceOptions.DryRun"/> switch. A provider that pruned as it wrote would be
	/// doing filesystem work on the logging path, which is the one place this project has decided
	/// not to do work.
	/// </para>
	/// <para>
	/// <em>Which</em> files are ours is the reader's question, not this one's — see
	/// <see cref="ServerLogReader.Prune"/>. This decides only when they expire.
	/// </para>
	/// </summary>
	/// <param name="scope">Where the reader and the settings come from.</param>
	/// <param name="settings">Honours <see cref="MaintenanceOptions.DryRun"/>.</param>
	/// <param name="now">The run's instant (§10.4).</param>
	/// <returns>How many files were deleted, or would have been.</returns>
	private static int DeleteOldLogFiles(IServiceScope scope, MaintenanceOptions settings, DateTimeOffset now)
	{
		FileLogOptions logging = scope.ServiceProvider.GetRequiredService<IOptions<FileLogOptions>>().Value;

		if (logging.RetainDays <= 0)
		{
			// Retention off. Kept as an explicit branch rather than a zero-day cut-off, which would
			// read as "delete everything" and quietly do it.
			return 0;
		}

		DateOnly cutoff = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-logging.RetainDays);

		return scope.ServiceProvider.GetRequiredService<ServerLogReader>().Prune(cutoff, settings.DryRun);
	}

	/// <summary>
	/// The backstop for a position whose rider is not sharing with that adventure (§10.1).
	/// </summary>
	/// <remarks>
	/// Its own sweep and its own number rather than folded into the idle one, because they are
	/// different facts: the idle count is housekeeping, and this one is a defect having fired.
	/// A report that added them together would hide the second inside the first.
	/// </remarks>
	private static async Task<int> DeleteOrphanedPositionsAsync(
		AsyncServiceScope scope,
		MaintenanceOptions settings)
	{
		PositionStore positions = scope.ServiceProvider.GetRequiredService<PositionStore>();

		return settings.DryRun
			? await positions.CountOrphanedAsync()
			: await positions.ClearOrphanedAsync();
	}

	/// <summary>
	/// The backstop for a position nothing is updating any more (§5.6, §7.11).
	/// </summary>
	/// <remarks>
	/// There is no longer an end-of-adventure that reclaims rows, so this is what stops a phone
	/// that died mid-ride broadcasting a pin for ever. It is a garbage collector, not a privacy
	/// guarantee: a rider still sending fixes is still sharing, however long they have been at it.
	/// </remarks>
	private static async Task<int> DeleteIdlePositionsAsync(
		AsyncServiceScope scope,
		MaintenanceOptions settings,
		DateTimeOffset now)
	{
		RideOptions ride = scope.ServiceProvider.GetRequiredService<IOptions<RideOptions>>().Value;
		PositionStore positions = scope.ServiceProvider.GetRequiredService<PositionStore>();

		DateTimeOffset floor = now.AddDays(-Math.Max(1, ride.PositionIdleDays));

		return settings.DryRun
			? await positions.CountIdleAsync(floor)
			: await positions.ClearIdleAsync(floor);
	}

	private static async Task<int> DeleteDeadTokensAsync(
		DlrDbContext database,
		MaintenanceOptions settings,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		DateTimeOffset before = now.AddDays(-settings.RefreshTokenRetentionDays);

		IQueryable<RefreshToken> dead = database
			.Set<RefreshToken>()
			.Where(token => token.ExpiresUtc < before
				|| (token.RevokedUtc != null && token.RevokedUtc < before));

		int count = settings.DryRun
			? await dead.CountAsync(cancellationToken)
			: await dead.ExecuteDeleteAsync(cancellationToken);

		if (!settings.DryRun)
		{
			// The tombstones age out on the same horizon, for the same reason: a device that has
			// not been opened in a month will be told to sign in, and that is answer enough.
			await database
				.Set<DeletedAccountToken>()
				.Where(token => token.DeletedUtc < before)
				.ExecuteDeleteAsync(cancellationToken);
		}

		return count;
	}

	private static async Task<int> PurgeRevisionsAsync(
		AsyncServiceScope scope,
		DlrDbContext database,
		MaintenanceOptions settings,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		List<TrackRevision> expired = await database
			.Set<TrackRevision>()
			.Where(revision => revision.PurgeAfterUtc <= now)
			.ToListAsync(cancellationToken);

		if (settings.DryRun || expired.Count == 0)
		{
			return settings.DryRun ? expired.Count : 0;
		}

		IBlobStore blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();

		// The bytes go with the row. §15.6's whole promise to the rider who trimmed their home
		// address off a track is that the removed points stop existing; a purge that dropped the
		// pointer and left the blob would leave them on the disk being backed up.
		foreach (TrackRevision revision in expired)
		{
			await blobs.DeleteAsync(revision.BlobRef, cancellationToken);
		}

		database.RemoveRange(expired);

		await database.SaveChangesAsync(cancellationToken);

		return expired.Count;
	}

	private async Task<int> PurgeReportsAsync(
		DlrDbContext database,
		MaintenanceOptions settings,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		DateTimeOffset before = now.AddDays(-moderation.Value.ReportRetentionDays);

		// Resolved only. Ageing out an unresolved report would turn a backlog into a silent
		// amnesty, and the operator's queue is exactly the thing that gets behind.
		IQueryable<ContentReport> spent = database
			.Set<ContentReport>()
			.Where(report => report.ResolvedUtc != null && report.ResolvedUtc < before);

		return settings.DryRun
			? await spent.CountAsync(cancellationToken)
			: await spent.ExecuteDeleteAsync(cancellationToken);
	}

	/// <summary>
	/// The one sweep that cannot be expressed as a query, because half its input is a filesystem
	/// (§16.6). <c>ON DELETE CASCADE</c> reaches rows and not files.
	/// </summary>
	private async Task<int> DeleteOrphanBlobsAsync(
		AsyncServiceScope scope,
		DlrDbContext database,
		MaintenanceOptions settings,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		IReadOnlyList<BlobColumn> columns = BlobReferences.InModel(database.Model);

		// The one place in this folder that earns §10.4's raw-SQL exemption. The query is a union
		// over columns discovered from the model rather than typed out, which is what makes the
		// guard test possible: there is no second list of tables here to fall out of step with
		// BlobReferences.Declared.
		string sql = string.Join(
			" UNION ",
			columns.Select(column =>
				$"SELECT \"{column.Column}\" AS \"Value\" FROM \"{column.Table}\""));

		HashSet<string> referenced = await database.Database
			.SqlQueryRaw<string>(sql)
			.ToHashSetAsync(StringComparer.Ordinal, cancellationToken);

		// The blob store stamps what it writes from this same clock, which is what makes the two
		// sides of this comparison comparable at all — see FileSystemBlobStore.
		DateTimeOffset cutoff = now.AddHours(-settings.OrphanBlobGraceHours);

		IBlobStore blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();

		List<string> orphans = [];

		await foreach (BlobEntry blob in blobs.ListAsync(cancellationToken))
		{
			// A blob is written before the row that points at it is committed, so for the width of
			// one request every new upload is indistinguishable from an orphan. Without the grace
			// window this sweep deletes photographs out from under the requests uploading them.
			if (blob.WrittenUtc <= cutoff && !referenced.Contains(blob.BlobRef))
			{
				orphans.Add(blob.BlobRef);
			}
		}

		if (settings.DryRun)
		{
			foreach (string orphan in orphans)
			{
				logger.LogInformation("Orphaned blob {BlobRef} would be deleted.", orphan);
			}

			return orphans.Count;
		}

		foreach (string orphan in orphans)
		{
			await blobs.DeleteAsync(orphan, cancellationToken);
		}

		return orphans.Count;
	}

	/// <summary>
	/// Runs one sweep, and turns a failure into a logged zero rather than an abandoned night.
	/// </summary>
	private async Task<T> SweepAsync<T>(string name, Func<Task<T>> sweep, T onFailure)
	{
		try
		{
			return await sweep();
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			logger.LogError(
				exception,
				"Nightly maintenance sweep '{Sweep}' failed; the remaining sweeps still ran.",
				name);

			return onFailure;
		}
	}
}
