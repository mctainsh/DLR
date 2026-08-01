namespace DLR.Server.Maintenance;

/// <summary>
/// The brakes on the one destructive timer in the project (§7.11).
/// <para>
/// Every other background service in this codebase either writes rows or stops a broadcast. This
/// one deletes accounts, and it runs while nobody is watching — so the defaults here are chosen so
/// that a deployment which never reads this section deletes <em>nothing</em>.
/// </para>
/// </summary>
public sealed class MaintenanceOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "Maintenance";

	/// <summary>
	/// Logs what would go and changes nothing. <strong>Defaults to <c>true</c></strong> (§7.11).
	/// <para>
	/// <strong>It gates every sweep, not only the account deletion.</strong> §7.11 describes it in
	/// terms of accounts because that is the sweep worth reading the output of, but a dry run that
	/// still deleted refresh tokens, positions and photo blobs would be a dry run in name only — and
	/// an operator who turns this on has said "show me, do not touch it". One switch that means one
	/// thing is worth more than a switch that means it for the frightening sweep and not the others.
	/// </para>
	/// </summary>
	public bool DryRun { get; set; } = true;

	/// <summary>
	/// The kill switch for the 180-day sweep alone (§7.11) — off without a redeploy.
	/// <para>
	/// Separate from <see cref="DryRun"/> on purpose: this turns off account deletion while leaving
	/// the tidying sweeps running, which is what you want at 3 a.m. when the predicate has done
	/// something surprising and the disk still needs collecting.
	/// </para>
	/// </summary>
	public bool DeleteInactiveAccounts { get; set; } = true;

	/// <summary>
	/// The most accounts one run may delete (§7.11). Batched so a single night can never take a
	/// long lock, and so a predicate that has gone wrong is bounded by how many nights it survives
	/// unnoticed rather than by how many accounts exist.
	/// </summary>
	public int MaxDeletesPerRun { get; set; } = 500;

	/// <summary>Days of inactivity after which an account holding nothing is deleted (§7.11).</summary>
	public int InactiveDays { get; set; } = 180;

	/// <summary>Days of inactivity after which the warning email goes out, if an address is known.</summary>
	public int WarnAfterDays { get; set; } = 150;

	/// <summary>How long <c>created_by_ip</c> is kept after registration (§7.8).</summary>
	public int RegistrationIpRetentionDays { get; set; } = 30;

	/// <summary>How long an expired or revoked <c>refresh_token</c> row is kept (§7.13).</summary>
	public int RefreshTokenRetentionDays { get; set; } = 30;

	/// <summary>
	/// How long an <em>unreferenced</em> blob must have sat on disk before the sweep may take it.
	/// <para>
	/// <strong>This is the whole safety of the orphan sweep.</strong> A blob is written before the
	/// row that points at it is committed, so for the width of one request every new upload looks
	/// exactly like an orphan. The grace window is what separates "nothing points at this" from
	/// "nothing points at this <em>yet</em>", and it is measured against the file's own timestamp
	/// rather than against anything in the database, because the database is what is missing.
	/// </para>
	/// </summary>
	public int OrphanBlobGraceHours { get; set; } = 24;

	/// <summary>
	/// How often the job runs. <strong>Zero or less means no timer at all</strong> — for a
	/// deployment that would rather drive it from <c>cron</c>, and for tests, which call
	/// <c>RunAsync</c> directly rather than advancing a fake clock a whole day into a
	/// <see cref="PeriodicTimer"/>.
	/// </summary>
	public double IntervalHours { get; set; } = 24;

	/// <summary>
	/// Where the run summary is emailed, or null for nowhere (§9).
	/// <para>
	/// <strong>"A destructive job you do not watch is a destructive job you will regret."</strong>
	/// The dry-run log is only read by somebody who goes looking; this arrives. It carries the
	/// counts and the candidate usernames — which is what makes a week of dry runs a thing an
	/// operator actually does rather than intends to.
	/// </para>
	/// <para>
	/// Off by default, because a self-hosted fork with no mail transport configured should not have
	/// its nightly job logging a delivery failure every morning.
	/// </para>
	/// </summary>
	public string? AlertEmail { get; set; }
}
