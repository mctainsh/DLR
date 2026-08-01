using System.Security.Claims;
using System.Text.Json;
using DLR.Core.Contracts.Moderation;
using DLR.Server.Data;
using DLR.Server.Data.Comments;
using DLR.Server.Data.Identity;
using DLR.Server.Data.Markers;
using DLR.Server.Data.Moderation;
using DLR.Server.Data.Rides;
using DLR.Server.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Moderation;

/// <summary>
/// Reporting content and blocking riders (§16.5, §17.7, §10.2).
/// <para>
/// This ships <strong>before the first store submission</strong>, not before the first comment
/// (§11's sequencing note). It is a review requirement rather than an optional nicety: Apple and
/// Play both check that a way to report objectionable content and block its author exists. A small,
/// organiser-admitted audience makes abuse unlikely and does not make the mechanism optional.
/// </para>
/// </summary>
public static class ModerationEndpoints
{
	/// <summary>Route name for reporting a comment.</summary>
	public const string ReportCommentRouteName = "ReportComment";

	/// <summary>Route name for reporting a marker.</summary>
	public const string ReportMarkerRouteName = "ReportMarker";

	/// <summary>Route name for blocking.</summary>
	public const string BlockRouteName = "BlockRider";

	/// <summary>Route name for unblocking.</summary>
	public const string UnblockRouteName = "UnblockRider";

	/// <summary>Route name for the block list.</summary>
	public const string BlockListRouteName = "BlockedRiders";

	/// <summary>Maps the moderation endpoints.</summary>
	public static IEndpointRouteBuilder MapModeration(this IEndpointRouteBuilder endpoints)
	{
		endpoints
			.MapPost("/api/v1/comments/{id:guid}/report", ReportCommentAsync)
			.RequireAuthorization()
			.WithName(ReportCommentRouteName)
			.WithSummary("Reports a post, snapshotting it.");

		endpoints
			.MapPost("/api/v1/markers/{id:guid}/report", ReportMarkerAsync)
			.RequireAuthorization()
			.WithName(ReportMarkerRouteName)
			.WithSummary("Reports a marker, snapshotting it.");

		endpoints
			.MapPost("/api/v1/blocks", BlockAsync)
			.RequireAuthorization()
			.WithName(BlockRouteName)
			.WithSummary("Hides a rider's content from the caller.");

		endpoints
			.MapDelete("/api/v1/blocks/{userId:guid}", UnblockAsync)
			.RequireAuthorization()
			.WithName(UnblockRouteName)
			.WithSummary("Stops hiding a rider.");

		endpoints
			.MapGet("/api/v1/blocks", BlockedAsync)
			.RequireAuthorization()
			.WithName(BlockListRouteName)
			.WithSummary("Who the caller has blocked.");

		return endpoints;
	}

	private static async Task<IResult> ReportCommentAsync(
		Guid id,
		ReportContentRequest request,
		ClaimsPrincipal caller,
		DlrDbContext database,
		TimeProvider clock,
		CancellationToken cancellationToken)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		if (Clean(request.Reason) is not { } reason)
		{
			return Problem(
				StatusCodes.Status400BadRequest,
				"A report needs a reason",
				"Say what is wrong with it — an empty report cannot be acted on.");
		}

		RideComment? comment = await database
			.Set<RideComment>()
			.AsNoTracking()
			.Include(row => row.Author)
			.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);

		if (comment is null || !await IsMemberAsync(database, comment.GroupRideId, userId, cancellationToken))
		{
			return Results.NotFound();
		}

		// The content as it reads right now. Taken here rather than resolved later from the row,
		// because the row is exactly what an organiser is expected to delete next.
		string snapshot = JsonSerializer.Serialize(new
		{
			kind = comment.Kind.ToString(),
			author = comment.Author?.UserName,
			body = comment.Body,
			photoId = comment.PhotoId,
			postedUtc = comment.PostedUtc,
		});

		return await FileAsync(
			database,
			clock,
			ReportTargetKind.Comment,
			id,
			comment.GroupRideId,
			comment.AuthorId,
			userId,
			reason,
			snapshot,
			cancellationToken);
	}

	private static async Task<IResult> ReportMarkerAsync(
		Guid id,
		ReportContentRequest request,
		ClaimsPrincipal caller,
		DlrDbContext database,
		TimeProvider clock,
		CancellationToken cancellationToken)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		if (Clean(request.Reason) is not { } reason)
		{
			return Problem(
				StatusCodes.Status400BadRequest,
				"A report needs a reason",
				"Say what is wrong with it — an empty report cannot be acted on.");
		}

		Marker? marker = await database
			.Set<Marker>()
			.AsNoTracking()
			.Include(row => row.CreatedBy)
			.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);

		// A track marker is the owner's own, so there is nobody to report it to; only ride markers
		// are visible to anybody else in the first place (§16.5).
		if (marker?.GroupRideId is not { } rideId
			|| !await IsMemberAsync(database, rideId, userId, cancellationToken))
		{
			return Results.NotFound();
		}

		string snapshot = JsonSerializer.Serialize(new
		{
			author = marker.CreatedBy?.UserName,
			title = marker.Title,
			note = marker.Note,
			icon = marker.Icon,
			photoId = marker.PhotoId,
			marker.Lat,
			marker.Lon,
		});

		return await FileAsync(
			database,
			clock,
			ReportTargetKind.Marker,
			id,
			rideId,
			marker.CreatedByUserId,
			userId,
			reason,
			snapshot,
			cancellationToken);
	}

	private static async Task<IResult> FileAsync(
		DlrDbContext database,
		TimeProvider clock,
		ReportTargetKind kind,
		Guid targetId,
		Guid? rideId,
		Guid authorId,
		Guid reporterId,
		string reason,
		string snapshot,
		CancellationToken cancellationToken)
	{
		ContentReport? existing = await database
			.Set<ContentReport>()
			.SingleOrDefaultAsync(
				report => report.TargetKind == kind
					&& report.TargetId == targetId
					&& report.ReportedByUserId == reporterId,
				cancellationToken);

		// Reporting twice is not two problems. Answered as success rather than a conflict, because
		// a rider who taps report again wants to know it was heard, not to be told off.
		if (existing is not null)
		{
			return Results.Ok(new ContentReported(existing.Id, existing.CreatedUtc));
		}

		ContentReport report = new()
		{
			Id = Guid.NewGuid(),
			TargetKind = kind,
			TargetId = targetId,
			GroupRideId = rideId,
			AuthorId = authorId,
			ReportedByUserId = reporterId,
			Reason = reason,
			ContentSnapshot = snapshot,
			CreatedUtc = clock.GetUtcNow(),
		};

		database.Add(report);

		await database.SaveChangesAsync(cancellationToken);

		return Results.Ok(new ContentReported(report.Id, report.CreatedUtc));
	}

	private static async Task<IResult> BlockAsync(
		BlockUserRequest request,
		ClaimsPrincipal caller,
		DlrDbContext database,
		TimeProvider clock,
		CancellationToken cancellationToken)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		if (request.UserId == userId)
		{
			return Problem(
				StatusCodes.Status400BadRequest,
				"You cannot block yourself",
				"That would hide your own posts from you.");
		}

		bool exists = await database
			.Set<AppUser>()
			.AnyAsync(user => user.Id == request.UserId, cancellationToken);

		if (!exists)
		{
			return Results.NotFound();
		}

		bool already = await database
			.Set<UserBlock>()
			.AnyAsync(
				block => block.BlockerId == userId && block.BlockedId == request.UserId,
				cancellationToken);

		if (!already)
		{
			database.Add(new UserBlock
			{
				BlockerId = userId,
				BlockedId = request.UserId,
				CreatedUtc = clock.GetUtcNow(),
			});

			await database.SaveChangesAsync(cancellationToken);
		}

		// No content, and nothing is sent to the person blocked. A block that announced itself
		// would turn a quiet decision into the confrontation it exists to avoid (§16.5).
		return Results.NoContent();
	}

	private static async Task<IResult> UnblockAsync(
		Guid userId,
		ClaimsPrincipal caller,
		DlrDbContext database,
		CancellationToken cancellationToken)
	{
		if (caller.UserId() is not { } blockerId)
		{
			return Results.Unauthorized();
		}

		UserBlock? block = await database
			.Set<UserBlock>()
			.SingleOrDefaultAsync(
				row => row.BlockerId == blockerId && row.BlockedId == userId,
				cancellationToken);

		if (block is not null)
		{
			database.Remove(block);

			await database.SaveChangesAsync(cancellationToken);
		}

		return Results.NoContent();
	}

	private static async Task<IResult> BlockedAsync(
		ClaimsPrincipal caller,
		DlrDbContext database,
		CancellationToken cancellationToken)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		List<BlockedRider> blocked = await database
			.Set<UserBlock>()
			.AsNoTracking()
			.Where(block => block.BlockerId == userId)
			.OrderByDescending(block => block.CreatedUtc)
			.Select(block => new BlockedRider(
				block.BlockedId,
				block.Blocked!.UserName!,
				block.CreatedUtc))
			.ToListAsync(cancellationToken);

		return Results.Ok(blocked);
	}

	private static Task<bool> IsMemberAsync(
		DlrDbContext database,
		Guid rideId,
		Guid userId,
		CancellationToken cancellationToken) =>
		database
			.Set<GroupRideMember>()
			.AnyAsync(member => member.GroupRideId == rideId && member.UserId == userId, cancellationToken);

	private static string? Clean(string? reason)
	{
		string? trimmed = reason?.Trim();

		return string.IsNullOrEmpty(trimmed) ? null : trimmed;
	}

	private static IResult Problem(int status, string title, string detail) =>
		Results.Problem(new ProblemDetails { Status = status, Title = title, Detail = detail });
}
