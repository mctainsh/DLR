namespace DLR.Server.Maintenance;

/// <summary>
/// What one run of <see cref="NightlyMaintenanceService"/> did, or - under
/// <see cref="MaintenanceOptions.DryRun"/> - what it would have done.
/// <para>
/// <strong>The same shape in both modes, deliberately.</strong> A dry run that reported nothing
/// would make the switch useless: the point of running for a week and reading the output is that
/// the numbers you read are the numbers you will get when you turn it off.
/// </para>
/// </summary>
public sealed record MaintenanceReport
{
	/// <summary>Whether this run changed anything at all.</summary>
	public bool WasDryRun { get; init; }

	/// <summary>
	/// The accounts the §7.11 predicate selected, named rather than counted.
	/// <para>
	/// Counted would be useless for the thing this exists for. "Seven accounts would be deleted" is
	/// not something anybody can check; seven usernames is.
	/// </para>
	/// </summary>
	public IReadOnlyList<InactiveAccount> InactiveCandidates { get; init; } = [];

	/// <summary>How many of those were actually deleted. Zero on a dry run.</summary>
	public int AccountsDeleted { get; init; }

	/// <summary>How many 150-day warnings went out.</summary>
	public int AccountsWarned { get; init; }

	/// <summary>How many <c>created_by_ip</c> values were cleared (§7.8).</summary>
	public int RegistrationIpsCleared { get; init; }

	/// <summary>Expired or long-revoked refresh tokens removed (§7.13).</summary>
	public int RefreshTokensDeleted { get; init; }

	/// <summary>Retained pre-edit originals past their undo window (§15.6).</summary>
	public int RevisionsPurged { get; init; }

	/// <summary>Resolved reports and their snapshots past retention (§17.7).</summary>
	public int ReportsPurged { get; init; }

	/// <summary>Blobs on the volume that no row pointed at (§16.6).</summary>
	public int OrphanBlobsDeleted { get; init; }

	/// <summary>Daily log files older than the configured retention (§14.6).</summary>
	public int LogFilesDeleted { get; init; }
}

/// <summary>An account the inactivity predicate selected (§7.11).</summary>
/// <param name="Id">Which account.</param>
/// <param name="UserName">
/// The name, because that is what makes a dry-run log readable - and because the name is released
/// back to the pool on deletion, so it is also the thing an operator might recognise.
/// </param>
/// <param name="LastActiveUtc">When the server last heard from it.</param>
public readonly record struct InactiveAccount(Guid Id, string UserName, DateTimeOffset LastActiveUtc);
