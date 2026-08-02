using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;
using DLR.Server.Data;
using DLR.Server.Data.Tracks;
using DLR.Server.Identity;
using DLR.Server.Markers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DLR.Server.Tracks;

/// <summary>The §15.8 caps, as settings rather than constants (§14.5).</summary>
public sealed class TrackImportOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "Tracks";

	/// <summary>Request-level cap; anything larger is a <c>413</c>.</summary>
	public long MaxUploadBytes { get; set; } = 25 * 1024 * 1024;

	/// <summary>
	/// Enforced mid-parse, aborting the read. A file can be small and still be pathological,
	/// so a byte cap alone does not bound the point count (§15.3).
	/// </summary>
	public int MaxPointsPerFile { get; set; } = 500_000;

	/// <summary>How many tracks are accepted from one file.</summary>
	public int MaxTracksPerFile { get; set; } = 20;
}

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
public static class TrackImportEndpoints
{
	/// <summary>Route name.</summary>
	public const string ImportRouteName = "ImportTrack";
}

/// <summary>Importing GPX (§15.3, §6.3).</summary>
[ApiController]
[Authorize]
public sealed class TrackImportController : ControllerBase
{
	[HttpPost("/api/v1/tracks/import", Name = TrackImportEndpoints.ImportRouteName)]
	[IgnoreAntiforgeryToken]
	[EndpointSummary("Reads a GPX file. ?dryRun=true previews without persisting.")]
	public async Task<IActionResult> ImportAsync(
		[FromServices] DlrDbContext database,
		[FromServices] TrackStore tracks,
		[FromServices] RequestThrottle throttle,
		[FromServices] IOptions<TrackImportOptions> importOptions,
		[FromServices] IOptions<RateLimitOptions> limits,
		[FromServices] IOptions<MarkerOptions> markerOptions,
		[FromServices] TimeProvider clock,
		[FromQuery] bool dryRun = false)
	{
		HttpRequest http = HttpContext.Request;

		if (User.UserId() is not { } ownerId)
		{
			return Unauthorized();
		}

		TrackImportOptions caps = importOptions.Value;

		// Per user rather than per address (§7.8): an import costs a parse and a blob write,
		// and the account is what those are charged to. Two windows, because twenty an hour and
		// a hundred a day answer different things — a burst, and a day spent uploading.
		bool withinLimits =
			throttle.TryAcquire(
				$"import-hour:{ownerId}",
				limits.Value.ImportPerHourPerUser,
				TimeSpan.FromHours(1))
			& throttle.TryAcquire(
				$"import-day:{ownerId}",
				limits.Value.ImportPerDayPerUser,
				TimeSpan.FromDays(1));

		if (!withinLimits)
		{
			return StatusCode(StatusCodes.Status429TooManyRequests);
		}

		// Checked before anything is read. Content-Length can lie, so the stream is capped as
		// well — but refusing a declared 900 MB upload without reading it is worth doing first.
		if (http.ContentLength > caps.MaxUploadBytes)
		{
			return TooLarge(caps);
		}

		if (!http.HasFormContentType)
		{
			return ProblemResult(
				StatusCodes.Status400BadRequest,
				"Not a file upload",
				"Send the file as multipart/form-data.");
		}

		IFormFile? file = (await http.ReadFormAsync()).Files.GetFile("file")
			?? (await http.ReadFormAsync()).Files.FirstOrDefault();

		if (file is null)
		{
			return ProblemResult(
				StatusCodes.Status400BadRequest,
				"No file",
				"The request carried no file to import.");
		}

		if (file.Length > caps.MaxUploadBytes)
		{
			return TooLarge(caps);
		}

		GpxDocument document;

		try
		{
			await using Stream content = file.OpenReadStream();

			document = GpxReader.Read(
				content,
				new GpxLimits
				{
					MaxPointsPerFile = caps.MaxPointsPerFile,
					MaxTracksPerFile = caps.MaxTracksPerFile,
				});
		}
		catch (GpxFormatException refused)
		{
			// The problem by name, with the position where the reader knows it. "Invalid file"
			// is useless to somebody whose exporter emits something slightly unusual, and this
			// is a feature people meet with files from a dozen different tools (§15.3).
			return new ObjectResult(new ProblemDetails
			{
				Status = refused.Problem == GpxProblem.TooManyPoints
					? StatusCodes.Status413PayloadTooLarge
					: StatusCodes.Status400BadRequest,
				Title = Title(refused.Problem),
				Detail = refused.Describe(),
				Extensions = { ["problem"] = refused.Problem.ToString() },
			})
			{
				StatusCode = refused.Problem == GpxProblem.TooManyPoints
					? StatusCodes.Status413PayloadTooLarge
					: StatusCodes.Status400BadRequest,
				ContentTypes = { "application/problem+json" },
			};
		}

		// The extension and the client's content type are hints, not facts — validation is by
		// parsing (§15.3). A file that parses but holds no track is not an error to shout
		// about; it is a preview that says "nothing here".
		List<GpxTrack> usable =
			[.. document.Tracks.Where(track => track.Geometry.Points.Count >= 2)];

		List<ImportedTrackResult> results = [];
		List<Track> staged = [];

		foreach (GpxTrack track in usable)
		{
			TrackStats stats = TrackStats.From(track.Geometry);

			byte[] hash = TrackBlobCodec.ContentHash(track.Geometry);

			Track? duplicate = await tracks.FindByContentAsync(ownerId, hash);

			Guid? id = null;

			if (!dryRun)
			{
				Track created = await tracks.StageAsync(
					ownerId,
					Guid.NewGuid(),
					track.Geometry,
					track.Name ?? Path.GetFileNameWithoutExtension(file.FileName),
					TrackSource.Imported,
					file.FileName);

				staged.Add(created);

				id = created.Id;
			}

			results.Add(new ImportedTrackResult(
				id,
				track.Name,
				track.Source == GpxTrackSource.Route ? ImportedFrom.Route : ImportedFrom.Track,
				stats.PointCount,
				stats.SegmentCount,
				stats.DistanceM,
				stats.DurationS,
				stats.AscentM,
				duplicate?.Id,
				duplicate?.CreatedUtc));
		}

		if (dryRun)
		{
			// Nothing was staged, so nothing to discard — the preview parses and answers, and
			// the client re-posts to commit (§15.3).
			return Ok(new TrackImportResult(
				DryRun: true,
				results,
				document.Waypoints.Count,
				document.TracksTruncated));
		}

		// Waypoints are file-level rather than per-track, so they land on the first usable track —
		// which is the ordinary case, a file holding one ride and the places along it (§16.6).
		if (staged.Count > 0)
		{
			WaypointImporter.Stage(
				database,
				document.Waypoints,
				staged[0].Id,
				ownerId,
				markerOptions.Value,
				clock.GetUtcNow());
		}

		try
		{
			await database.SaveChangesAsync();
		}
		catch
		{
			// All or nothing. A half-committed import leaves the rider counting which of their
			// tracks arrived, and the blobs of the ones that did not.
			await tracks.DiscardAsync(staged);

			throw;
		}

		return Ok(new TrackImportResult(
			DryRun: false,
			results,
			document.Waypoints.Count,
			document.TracksTruncated));
	}

	private static string Title(GpxProblem problem) => problem switch
	{
		GpxProblem.NotXml => "Not an XML file",
		GpxProblem.NotGpx => "Not a GPX file",
		GpxProblem.Truncated => "File is incomplete",
		GpxProblem.DtdNotAllowed => "Document type declarations are not accepted",
		GpxProblem.InvalidCoordinate => "A coordinate is not usable",
		GpxProblem.TooManyPoints => "Too many points",
		_ => "This file could not be read",
	};

	private static ObjectResult TooLarge(TrackImportOptions caps) => ProblemResult(
		StatusCodes.Status413PayloadTooLarge,
		"File too large",
		$"Files are limited to {caps.MaxUploadBytes / (1024 * 1024)} MB.");

	private static ObjectResult ProblemResult(int status, string title, string detail) =>
		new(new ProblemDetails { Status = status, Title = title, Detail = detail })
		{
			StatusCode = status,
			ContentTypes = { "application/problem+json" },
		};
}
