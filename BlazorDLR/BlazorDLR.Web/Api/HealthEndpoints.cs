using DLR.Core.Contracts.Health;
using DLR.Server.Data;
using DLR.Server.Tracks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DLR.Server.Api;

/// <summary>Thresholds for <c>GET /healthz</c> (§9, §9.1).</summary>
public sealed class HealthOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "Health";

	/// <summary>
	/// Megabytes that must remain free on the blob volume before the server calls itself healthy.
	/// <para>
	/// <strong>This is the disk-usage alert §9 asks for</strong>, and it costs nothing extra: the
	/// free uptime pinger that is already watching this URL starts failing, which is the alert.
	/// Buying a monitoring product to watch one number on one machine would be the wrong shape of
	/// answer for a €4 VPS.
	/// </para>
	/// <para>
	/// 2 GB on a 40 GB disk. Generous enough that there is time to add a Hetzner volume — about
	/// €0.05/GB and one command — and not so generous that it fires while nothing is wrong.
	/// </para>
	/// </summary>
	public long MinimumFreeMb { get; set; } = 2_048;
}

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
public static class HealthEndpoints
{
	/// <summary>Route name.</summary>
	public const string RouteName = "Health";
}

/// <summary>
/// <c>GET /healthz</c> (§9).
/// <para>
/// Three questions, and the two that are not "is the process up" are the interesting ones. A
/// container that answers HTTP while its schema is a migration behind is the failure mode of a
/// half-finished deploy, and it does not announce itself — every request works until one touches
/// the column that is not there yet. A container on a full disk answers HTTP too, right up until
/// PostgreSQL cannot write (§9.1).
/// </para>
/// </summary>
[ApiController]
public sealed class HealthController : ControllerBase
{
	[HttpGet("/healthz", Name = HealthEndpoints.RouteName)]
	[AllowAnonymous]
	[EndpointSummary("Whether this server can serve: database, schema and disk.")]
	public async Task<IActionResult> GetAsync(
		[FromServices] DlrDbContext database,
		[FromServices] IOptions<BlobStoreOptions> blobs,
		[FromServices] IOptions<HealthOptions> options,
		[FromServices] ILoggerFactory loggers,
		CancellationToken cancellationToken)
	{
		bool reachable = true;
		int pending = 0;

		try
		{
			// The count, not the names. Asking the database rather than the assembly is the point:
			// it is the deployed schema being judged, not what this build believes about it.
			pending = (await database.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			// Logged in full, reported as a boolean. A connection failure's message names the host,
			// the port and often the user, and this endpoint is anonymous.
			loggers
				.CreateLogger(typeof(HealthController))
				.LogError(exception, "Health check could not reach the database.");

			reachable = false;
		}

		BlobVolumeHealth volume = VolumeHealth(blobs.Value.RootPath, options.Value.MinimumFreeMb);

		bool healthy = reachable && pending == 0 && volume.Ok;

		HealthReport report = new(
			healthy ? "healthy" : "unhealthy",
			reachable,
			reachable && pending == 0,
			pending,
			volume);

		// 503 rather than 200-with-a-sad-body. The pinger reads the status code and nothing else,
		// and a health endpoint that always answers 200 is a health endpoint nobody is watching.
		return healthy
			? Ok(report)
			: StatusCode(StatusCodes.Status503ServiceUnavailable, report);
	}

	private static BlobVolumeHealth VolumeHealth(string rootPath, long minimumFreeMb)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
			{
				// Not configured, or not mounted. Either way nothing can be stored, which is not a
				// healthy server — and a missing mount is precisely the deploy mistake that
				// otherwise presents as "uploads fail" a day later.
				return new BlobVolumeHealth(false, 0);
			}

			long freeMb = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(rootPath))!)
				.AvailableFreeSpace / 1024 / 1024;

			return new BlobVolumeHealth(freeMb >= minimumFreeMb, freeMb);
		}
		catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
		{
			// A volume we cannot even measure is not a volume we should call healthy.
			return new BlobVolumeHealth(false, 0);
		}
	}
}
