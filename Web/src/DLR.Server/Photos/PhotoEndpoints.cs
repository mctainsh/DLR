using System.Security.Claims;
using System.Security.Cryptography;
using DLR.Core.Contracts.Photos;
using DLR.Server.Data;
using DLR.Server.Data.Photos;
using DLR.Server.Identity;
using DLR.Server.Tracks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DLR.Server.Photos;

/// <summary>Uploading and serving photographs (§16.4).</summary>
public static class PhotoEndpoints
{
	/// <summary>Route name for the upload.</summary>
	public const string UploadRouteName = "UploadPhoto";

	/// <summary>Route name for the full image.</summary>
	public const string ContentRouteName = "PhotoContent";

	/// <summary>Route name for the thumbnail.</summary>
	public const string ThumbnailRouteName = "PhotoThumbnail";

	/// <summary>Maps the photo endpoints.</summary>
	public static IEndpointRouteBuilder MapPhotos(this IEndpointRouteBuilder endpoints)
	{
		endpoints
			.MapPost("/api/v1/photos", UploadAsync)
			.RequireAuthorization(AuthorizationPolicies.NotRestricted)
			.DisableAntiforgery()
			.WithName(UploadRouteName)
			.WithSummary("Re-encodes an image, strips its metadata and stores it.");

		endpoints
			.MapGet("/api/v1/photos/{id:guid}", ContentAsync)
			.RequireAuthorization()
			.WithName(ContentRouteName)
			.WithSummary("The stored image.");

		endpoints
			.MapGet("/api/v1/photos/{id:guid}/thumbnail", ThumbnailAsync)
			.RequireAuthorization()
			.WithName(ThumbnailRouteName)
			.WithSummary("The callout thumbnail.");

		return endpoints;
	}

	private static async Task<IResult> UploadAsync(
		HttpRequest http,
		ClaimsPrincipal caller,
		DlrDbContext database,
		IBlobStore blobs,
		ImageIngest ingest,
		RequestThrottle throttle,
		IOptions<PhotoOptions> options,
		TimeProvider clock,
		CancellationToken cancellationToken)
	{
		if (caller.UserId() is not { } ownerId)
		{
			return Results.Unauthorized();
		}

		PhotoOptions caps = options.Value;

		// Per user rather than per address (§16.5): an ingest costs a decode, two encodes and two
		// blob writes on a 40 GB disk, and the account is what those are charged to.
		bool withinLimits =
			throttle.TryAcquire($"photo-hour:{ownerId}", caps.UploadsPerHourPerUser, TimeSpan.FromHours(1))
			& throttle.TryAcquire($"photo-day:{ownerId}", caps.UploadsPerDayPerUser, TimeSpan.FromDays(1));

		if (!withinLimits)
		{
			return Results.StatusCode(StatusCodes.Status429TooManyRequests);
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
				StatusCodes.Status400BadRequest,
				"Not a file upload",
				"Send the image as multipart/form-data.");
		}

		IFormFileCollection files = (await http.ReadFormAsync(cancellationToken)).Files;
		IFormFile? file = files.GetFile("file") ?? files.FirstOrDefault();

		if (file is null)
		{
			return Problem(
				StatusCodes.Status400BadRequest,
				"No file",
				"The request carried no image.");
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

		return Results.Created(
			$"/api/v1/photos/{photo.Id}",
			new PhotoUploaded(photo.Id, photo.WidthPx, photo.HeightPx, photo.ByteSize));
	}

	private static Task<IResult> ContentAsync(
		Guid id,
		ClaimsPrincipal caller,
		DlrDbContext database,
		IBlobStore blobs,
		CancellationToken cancellationToken) =>
		ServeAsync(id, caller, database, blobs, thumbnail: false, cancellationToken);

	private static Task<IResult> ThumbnailAsync(
		Guid id,
		ClaimsPrincipal caller,
		DlrDbContext database,
		IBlobStore blobs,
		CancellationToken cancellationToken) =>
		ServeAsync(id, caller, database, blobs, thumbnail: true, cancellationToken);

	private static async Task<IResult> ServeAsync(
		Guid id,
		ClaimsPrincipal caller,
		DlrDbContext database,
		IBlobStore blobs,
		bool thumbnail,
		CancellationToken cancellationToken)
	{
		if (caller.UserId() is null)
		{
			return Results.Unauthorized();
		}

		Photo? photo = await database
			.Set<Photo>()
			.AsNoTracking()
			.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);

		if (photo is null)
		{
			return Results.NotFound();
		}

		Stream? content = await blobs.OpenAsync(
			thumbnail ? photo.ThumbBlobRef : photo.BlobRef,
			cancellationToken);

		if (content is null)
		{
			return Results.NotFound();
		}

		// Always JPEG, because ingest re-encodes everything to it (§16.4).
		return Results.Stream(content, "image/jpeg");
	}

	private static IResult Refused(PhotoProblem problem, PhotoOptions caps) => problem switch
	{
		PhotoProblem.TooManyPixels => Results.Problem(new ProblemDetails
		{
			Status = StatusCodes.Status413PayloadTooLarge,
			Title = "Image is too large to decode",
			Detail =
				$"Images are limited to {caps.MaxDecodedPixels / 1_000_000} megapixels once decoded. " +
				"A small file can still declare an enormous canvas, so this is checked from the " +
				"header before the image is read.",
			Extensions = { ["problem"] = problem.ToString() },
		}),

		PhotoProblem.DecodeFailed => Results.Problem(new ProblemDetails
		{
			Status = StatusCodes.Status400BadRequest,
			Title = "Image could not be read",
			Detail = "The header parsed but the image data did not — the file looks truncated or corrupt.",
			Extensions = { ["problem"] = problem.ToString() },
		}),

		_ => Results.Problem(new ProblemDetails
		{
			Status = StatusCodes.Status400BadRequest,
			Title = "Not an image",
			Detail = "Send a JPEG, PNG, HEIC or WebP. The format is determined from the file's " +
				"content, not from its name or the content type sent with it.",
			Extensions = { ["problem"] = PhotoProblem.NotAnImage.ToString() },
		}),
	};

	private static IResult TooLarge(PhotoOptions caps) => Problem(
		StatusCodes.Status413PayloadTooLarge,
		"File too large",
		$"Images are limited to {caps.MaxUploadBytes / (1024 * 1024)} MB.");

	private static IResult Problem(int status, string title, string detail) =>
		Results.Problem(new ProblemDetails { Status = status, Title = title, Detail = detail });
}
