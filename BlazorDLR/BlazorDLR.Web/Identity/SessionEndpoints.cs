using System.Security.Claims;
using DLR.Core.Contracts.Identity;
using DLR.Server.Data;
using DLR.Server.Data.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Identity;

/// <summary>Route names, preserved from the previous minimal-API endpoint so callers keep working.</summary>
public static class SessionEndpoints
{
	/// <summary>Route name for the device list.</summary>
	public const string ListRouteName = "Sessions";

	/// <summary>Route name for revoking a device.</summary>
	public const string RevokeRouteName = "RevokeSession";
}

/// <summary>Signed-in devices, and ending one (§7.10, §7.14).</summary>
[ApiController]
[Authorize]
public sealed class SessionController : ControllerBase
{
	[HttpGet("/api/v1/auth/sessions", Name = SessionEndpoints.ListRouteName)]
	[EndpointSummary("The devices this account is signed in on.")]
	public async Task<IActionResult> ListAsync([FromServices] DlrDbContext database)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		Guid? current = User.DeviceId();

		List<DeviceSession> sessions = await database
			.Set<Device>()
			.Where(device => device.UserId == userId)
			.OrderByDescending(device => device.LastSeenUtc)
			.Select(device => new DeviceSession(
				device.Id,
				device.Name,
				device.LastSeenUtc,
				device.Id == current))
			.ToListAsync();

		return Ok(sessions);
	}

	[HttpDelete("/api/v1/auth/sessions/{deviceId:guid}", Name = SessionEndpoints.RevokeRouteName)]
	[EndpointSummary("Ends the session on one device.")]
	public async Task<IActionResult> RevokeAsync(
		[FromRoute] Guid deviceId,
		[FromServices] DlrDbContext database,
		[FromServices] RefreshTokenService refresh)
	{
		if (User.UserId() is not { } userId)
		{
			return Unauthorized();
		}

		// Scoped to the caller's own devices, and 404 rather than 403 for anything else. A
		// distinct answer would turn this into a way to ask whether a device id exists.
		bool isOurs = await database
			.Set<Device>()
			.AnyAsync(device => device.Id == deviceId && device.UserId == userId);

		if (!isOurs)
		{
			return NotFound();
		}

		// Every family on that device, not just the newest. A device that signed in twice has
		// two chains, and leaving the older one alive would make "revoke" mean "revoke one of
		// them" — which is not what somebody who has lost a phone is asking for.
		List<Guid> families = await database
			.Set<RefreshToken>()
			.Where(token => token.DeviceId == deviceId && token.RevokedUtc == null)
			.Select(token => token.FamilyId)
			.Distinct()
			.ToListAsync();

		foreach (Guid family in families)
		{
			await refresh.RevokeFamilyAsync(family, RevocationReasons.SessionRevoked);
		}

		// The device row stays. Deleting it would take the refresh_token rows with it by
		// cascade, and the revocation record is the only evidence of what happened.
		return NoContent();
	}
}

/// <summary>Reading the §7.4 claims off a caller.</summary>
public static class CallerClaims
{
	/// <summary>The account id from <c>sub</c>.</summary>
	public static Guid? UserId(this ClaimsPrincipal caller) =>
		Guid.TryParse(caller.FindFirstValue(DlrClaims.Subject), out Guid id) ? id : null;

	/// <summary>The device id from <c>dev</c>.</summary>
	public static Guid? DeviceId(this ClaimsPrincipal caller) =>
		Guid.TryParse(caller.FindFirstValue(DlrClaims.Device), out Guid id) ? id : null;
}
