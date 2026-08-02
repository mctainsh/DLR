using System.Security.Cryptography;
using DLR.Core.Contracts.Photos;
using DLR.Server.Data;
using DLR.Server.Data.Photos;
using DLR.Server.Identity;
using DLR.Server.Tracks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DLR.Server.Photos;

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
public static class PhotoEndpoints
{
	/// <summary>Route name for the upload.</summary>
	public const string UploadRouteName = "UploadPhoto";

	/// <summary>Route name for the full image.</summary>
	public const string ContentRouteName = "PhotoContent";

	/// <summary>Route name for the thumbnail.</summary>
	public const string ThumbnailRouteName = "PhotoThumbnail";
}

/// <summary>Uploading and serving photographs (§16.4).</summary>
[ApiController]
[Authorize]
public sealed class PhotoController : ControllerBase
{
	[HttpPost("/api/v1/photos", Name = PhotoEndpoints.UploadRouteName)]
	[Authorize(Policy = AuthorizationPolicies.NotRestricted)]
	[IgnoreAntiforgeryToken]
	[EndpointSummary("Re-encodes an image, strips its metadata and stores it.")]
	public async Task<IActionResult> UploadAsync(
		[FromServices] DlrDbContext database,
		[FromServices] IBlobStore blobs,
		[FromServices] ImageIngest ingest,
		[FromServices] RequestThrottle throttle,
		[FromServices] IOptions<PhotoOptions> options,
		[FromServices] TimeProvider clock,
		CancellationToken cancellationToken)
	{
		HttpRequest http = HttpContext.Request;

		if (User.UserId() is not { } ownerId)
		{
			return Unauthorized();
		}

		PhotoOptions caps = options.Value;

		// Per user rather than per address (§16.5): an ingest costs a decode, two encodes and two
		// blob writes on a 40 GB disk, and the account is what those are charged to.
		bool withinLimits =
			throttle.TryAcquire($"photo-hour:{ownerId}", caps.UploadsPerHourPerUser, TimeSpan.FromHours(1))
			& throttle.TryAcquire($"photo-day:{ownerId}", caps.UploadsPerDayPerUser, TimeSpan.FromDays(1));

		if (!withinLimits)
		{
			return StatusCode(StatusCodes.Status429TooManyRequests);
		}

		// Checked before anything is read. Content-Length can lie, so the file is checked again
		// below — but refusing a declared 900 MB upload without reading it is worth doing first.
		if (http.ContentLength > caps.MaxUploadBytes)
		{
			return TooLarge(caps);
		}

		if (!http.HasFormContentType)
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "Not a file upload",
				detail: "Send the image as multipart/form-data.");
		}

		IFormFileCollection files = (await http.ReadFormAsync(cancellationToken)).Files;
		IFormFile? file = files.GetFile("file") ?? files.FirstOrDefault();

		if (file is null)
		{
			return Problem(
				statusCode: StatusCodes.Status400BadRequest,
				title: "No file",
				detail: "The request carried no image.");
		}

		if (file.Length > caps.MaxUploadBytes)
		{
			return TooLarge(caps);
		}

		byte[] uploaded = new byte[file.Length];

		await using (Stream content = file.OpenReadStream())
		{
			await content.ReadExactlyAsync(uploaded, cancellationToken);
		}

		// The filename and the client's content type are hints and nothing more. Everything that
		// follows is decided by the bytes (§16.4).
		IngestOutcome outcome = ingest.Read(uploaded);

		if (!outcome.Accepted)
		{
			return Refused(outcome.Problem, caps);
		}

		IngestedImage image = outcome.Image!;

		string blobRef = await blobs.PutAsync(new MemoryStream(image.Full), cancellationToken);
		string thumbRef;

		try
		{
			thumbRef = await blobs.PutAsync(new MemoryStream(image.Thumbnail), cancellationToken);
		}
		catch
		{
			// A photo whose thumbnail never landed would draw a broken pin on every member's map
			// with no row to explain it. Take the orphan out rather than leaving it for the sweep.
			await blobs.DeleteAsync(blobRef, CancellationToken.None);

			throw;
		}

		Photo photo = new()
		{
			Id = Guid.NewGuid(),
			OwnerId = ownerId,
			BlobRef = blobRef,
			ThumbBlobRef = thumbRef,
			WidthPx = image.WidthPx,
			HeightPx = image.HeightPx,
			ByteSize = image.Full.Length,
			ContentHash = SHA256.HashData(image.Full),
			CreatedUtc = clock.GetUtcNow(),
		};

		database.Add(photo);

		try
		{
			await database.SaveChangesAsync(cancellationToken);
		}
		catch
		{
			await blobs.DeleteAsync(blobRef, CancellationToken.None);
			await blobs.DeleteAsync(thumbRef, CancellationToken.None);

			throw;
		}

		return Created(
			$"/api/v1/photos/{photo.Id}",
			new PhotoUploaded(photo.Id, photo.WidthPx, photo.HeightPx, photo.ByteSize));
	}

	[HttpGet("/api/v1/photos/{id:guid}", Name = PhotoEndpoints.ContentRouteName)]
	[EndpointSummary("The stored image.")]
	public Task<IActionResult> ContentAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] IBlobStore blobs,
		CancellationToken cancellationToken) =>
		ServeAsync(id, database, blobs, thumbnail: false, cancellationToken);

	[HttpGet("/api/v1/photos/{id:guid}/thumbnail", Name = PhotoEndpoints.ThumbnailRouteName)]
	[EndpointSummary("The callout thumbnail.")]
	public Task<IActionResult> ThumbnailAsync(
		[FromRoute] Guid id,
		[FromServices] DlrDbContext database,
		[FromServices] IBlobStore blobs,
		CancellationToken cancellationToken) =>
		ServeAsync(id, database, blobs, thumbnail: true, cancellationToken);

	private async Task<IActionResult> ServeAsync(
		Guid id,
		DlrDbContext database,
		IBlobStore blobs,
		bool thumbnail,
		CancellationToken cancellationToken)
	{
		if (User.UserId() is null)
		{
			return Unauthorized();
		}

		Photo? photo = await database
			.Set<Photo>()
			.AsNoTracking()
			.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);

		if (photo is null)
		{
			return NotFound();
		}

		Stream? content = await blobs.OpenAsync(
			thumbnail ? photo.ThumbBlobRef : photo.BlobRef,
			cancellationToken);

		if (content is null)
		{
			return NotFound();
		}

		// Always JPEG, because ingest re-encodes everything to it (§16.4).
		return File(content, "image/jpeg");
	}

	private static IActionResult Refused(PhotoProblem problem, PhotoOptions caps) => problem switch
	{
		PhotoProblem.TooManyPixels => new ObjectResult(new ProblemDetails
		{
			Status = StatusCodes.Status413PayloadTooLarge,
			Title = "Image is too large to decode",
			Detail =
				$"Images are limited to {caps.MaxDecodedPixels / 1_000_000} megapixels once decoded. " +
				"A small file can still declare an enormous canvas, so this is checked from the " +
				"header before the image is read.",
			Extensions = { ["problem"] = problem.ToString() },
		})
		{
			StatusCode = StatusCodes.Status413PayloadTooLarge,
			ContentTypes = { "application/problem+json" },
		},

		PhotoProblem.DecodeFailed => new ObjectResult(new ProblemDetails
		{
			Status = StatusCodes.Status400BadRequest,
			Title = "Image could not be read",
			Detail = "The header parsed but the image data did not — the file looks truncated or corrupt.",
			Extensions = { ["problem"] = problem.ToString() },
		})
		{
			StatusCode = StatusCodes.Status400BadRequest,
			ContentTypes = { "application/problem+json" },
		},

		_ => new ObjectResult(new ProblemDetails
		{
			Status = StatusCodes.Status400BadRequest,
			Title = "Not an image",
			Detail = "Send a JPEG, PNG, HEIC or WebP. The format is determined from the file's " +
				"content, not from its name or the content type sent with it.",
			Extensions = { ["problem"] = PhotoProblem.NotAnImage.ToString() },
		})
		{
			StatusCode = StatusCodes.Status400BadRequest,
			ContentTypes = { "application/problem+json" },
		},
	};

	private static ObjectResult TooLarge(PhotoOptions caps) =>
		new(new ProblemDetails
		{
			Status = StatusCodes.Status413PayloadTooLarge,
			Title = "File too large",
			Detail = $"Images are limited to {caps.MaxUploadBytes / (1024 * 1024)} MB.",
		})
		{
			StatusCode = StatusCodes.Status413PayloadTooLarge,
			ContentTypes = { "application/problem+json" },
		};
}
