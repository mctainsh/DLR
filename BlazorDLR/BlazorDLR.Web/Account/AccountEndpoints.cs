using DLR.Core.Contracts.Account;
using DLR.Server.Data;
using DLR.Server.Data.Identity;
using DLR.Server.Data.Moderation;
using DLR.Server.Identity;
using DLR.Server.Tracks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Account;

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
public static class AccountEndpoints
{
	/// <summary>Route name for the export.</summary>
	public const string ExportRouteName = "ExportAccount";

	/// <summary>Route name for the deletion.</summary>
	public const string DeleteRouteName = "DeleteAccount";
}

/// <summary>
/// <c>GET /api/v1/me/export</c> and <c>DELETE /api/v1/me</c> (§6.3, §10.1, §16.6).
/// <para>
/// The two halves of the same obligation. An export that omits something and a deletion that leaves
/// something behind are the same failure seen from opposite ends, which is why they are one file
/// and share one list of what an account owns.
/// </para>
/// </summary>
[ApiController]
[Authorize]
public sealed class AccountController : ControllerBase
{
	[HttpGet("/api/v1/me/export", Name = AccountEndpoints.ExportRouteName)]
	[EndpointSummary("Everything this server holds about the caller, as a ZIP archive.")]
	public async Task<IActionResult> ExportAsync(
		[FromServices] UserManager<AppUser> users,
		[FromServices] DlrDbContext database,
		[FromServices] IBlobStore blobs,
		[FromServices] TimeProvider clock,
		CancellationToken cancellationToken)
	{
		if (await User.LoadAsync(users) is not { } user)
		{
			return Unauthorized();
		}

		// Buffered rather than streamed straight to the response. ZipArchive in Create mode needs a
		// seekable stream for its central directory, and the alternative — a streamed archive — buys
		// nothing here: an export is a handful of megabytes, taken once, by the one person entitled
		// to it. Making it complicated would be optimising the wrong thing.
		MemoryStream buffer = new();

		await AccountExportBuilder.WriteAsync(
			buffer,
			database,
			blobs,
			user,
			clock.GetUtcNow(),
			cancellationToken);

		buffer.Position = 0;

		return File(
			buffer,
			"application/zip",
			$"dumbluckrides-{user.UserName}-export.zip");
	}

	/// <summary>
	/// The one irreversible action in the API (§6.3).
	/// </summary>
	/// <remarks>
	/// <para>
	/// <strong>Deleting the account deletes the rides it organises, and everyone else's membership
	/// of them.</strong> That follows from `group_ride.owner_id ON DELETE CASCADE`, and it is
	/// stated here rather than discovered: there is no transfer-of-ownership feature, so the
	/// alternative would be refusing the deletion — and a hard block on erasure is not defensible
	/// under §10.2's applicable law. The organiser is told before it happens; the copy is a UI task.
	/// </para>
	/// </remarks>
	[HttpDelete("/api/v1/me", Name = AccountEndpoints.DeleteRouteName)]
	[EndpointSummary("Deletes the account, its content and its blobs. Irreversible.")]
	public async Task<IActionResult> DeleteAsync(
		// Explicit, because minimal APIs will not infer a body on DELETE — and the body is where
		// the password belongs. A query string would put it in Caddy's access log and the
		// browser's history, which is the exact hazard §7.6's query-string token lift is scoped
		// so narrowly to avoid.
		[FromBody] DeleteAccountRequest request,
		[FromServices] UserManager<AppUser> users,
		[FromServices] DlrDbContext database,
		[FromServices] IBlobStore blobs,
		[FromServices] ILoggerFactory loggers,
		CancellationToken cancellationToken)
	{
		if (await User.LoadAsync(users) is not { } user)
		{
			return Unauthorized();
		}

		// Re-entered, not inferred from the bearer token. A fifteen-minute access token lifted off
		// a shared machine should not be enough to end somebody's account, and every account has a
		// password — §7.2 makes username and password *the* account — so this excludes nobody.
		if (!await users.CheckPasswordAsync(user, request.CurrentPassword))
		{
			return Problem(
				statusCode: StatusCodes.Status403Forbidden,
				title: "Password required",
				detail: "Deleting an account needs the account's current password.");
		}

		// Gathered before the rows go. After the delete there is nothing left to say which files
		// were this account's, and ON DELETE CASCADE reaches rows and not a filesystem (§16.6).
		IReadOnlyList<string> owned = await AccountBlobs.OwnedByAsync(database, user.Id, cancellationToken);

		// user_block.blocked_id is NO ACTION, not a cascade — two cascade paths into asp_net_users
		// through one table is an error in PostgreSQL (§16.5). The nightly sweep does the same
		// thing for the same reason; both would fail the whole delete without it.
		await database
			.Set<UserBlock>()
			.Where(block => block.BlockedId == user.Id)
			.ExecuteDeleteAsync(cancellationToken);

		await database
			.Set<AppUser>()
			.Where(row => row.Id == user.Id)
			.ExecuteDeleteAsync(cancellationToken);

		ILogger logger = loggers.CreateLogger(typeof(AccountController));

		// Rows first, blobs second, and a failure here is logged rather than thrown. The account is
		// already gone; answering 500 would tell the rider their deletion failed when it did not,
		// and the §7.11 orphan sweep collects whatever is left as the backstop it is meant to be.
		foreach (string blobRef in owned)
		{
			try
			{
				await blobs.DeleteAsync(blobRef, cancellationToken);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				logger.LogError(
					exception,
					"Could not delete blob {BlobRef} for a deleted account; the nightly sweep will.",
					blobRef);
			}
		}

		return NoContent();
	}
}
