namespace DLR.Core.Contracts.Health;

/// <summary>
/// What <c>GET /healthz</c> answers (§9).
/// <para>
/// <strong>Deliberately thin, because it is anonymous.</strong> A free uptime pinger is the whole
/// alerting budget this project has, so the endpoint has to be reachable without a credential — and
/// anything it reports is therefore public. Booleans and a count; no migration names, no connection
/// details, no version of PostgreSQL. Somebody deciding whether to attack this server learns
/// nothing from it that <c>/api/v1/about</c> does not already publish on purpose (§14.6.2).
/// </para>
/// </summary>
/// <param name="Status">
/// <c>healthy</c> or <c>unhealthy</c>. The status <em>code</em> is what a pinger reads; this is for
/// the person who then opens the URL.
/// </param>
/// <param name="Database">Whether the server could reach PostgreSQL at all.</param>
/// <param name="MigrationsApplied">
/// Whether the schema is current. A server running against a schema older than its code is the
/// failure mode of a half-finished deploy, and it does not announce itself — every request works
/// until one touches the column that is not there yet.
/// </param>
/// <param name="PendingMigrations">How many are outstanding. A number, never their names.</param>
/// <param name="BlobVolume">
/// Free space on the volume holding tracks and photos (§9.1). Reported because a full disk stops
/// PostgreSQL <em>writing</em>, not merely stopping uploads — a far worse failure than a slow map,
/// and the one this project is most likely to hit first on a 40 GB VPS.
/// </param>
public sealed record HealthReport(
	string Status,
	bool Database,
	bool MigrationsApplied,
	int PendingMigrations,
	BlobVolumeHealth BlobVolume);

/// <summary>The blob volume's headroom (§9.1).</summary>
/// <param name="Ok">Whether free space is above the configured floor.</param>
/// <param name="FreeMb">Megabytes free, rounded. Coarse on purpose.</param>
public sealed record BlobVolumeHealth(bool Ok, long FreeMb);
