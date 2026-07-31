using System.Security.Claims;
using DLR.Core.Contracts.Identity;
using DLR.Server.Data;
using DLR.Server.Data.Identity;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Identity;

/// <summary>Signed-in devices, and ending one (§7.10, §7.14).</summary>
public static class SessionEndpoints
{
	/// <summary>Route name for the device list.</summary>
	public const string ListRouteName = "Sessions";

	/// <summary>Route name for revoking a device.</summary>
	public const string RevokeRouteName = "RevokeSession";

	/// <summary>Maps the two session endpoints.</summary>
	public static IEndpointRouteBuilder MapSessions(this IEndpointRouteBuilder endpoints)
	{
		endpoints
			.MapGet("/api/v1/auth/sessions", ListAsync)
			.RequireAuthorization()
			.WithName(ListRouteName)
			.WithSummary("The devices this account is signed in on.");

		endpoints
			.MapDelete("/api/v1/auth/sessions/{deviceId:guid}", RevokeAsync)
			.RequireAuthorization()
			.WithName(RevokeRouteName)
			.WithSummary("Ends the session on one device.");

		return endpoints;
	}

	private static async Task<IResult> ListAsync(ClaimsPrincipal caller, DlrDbContext database)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		Guid? current = caller.DeviceId();

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

		return Results.Ok(sessions);
	}

	private static async Task<IResult> RevokeAsync(
		Guid deviceId,
		ClaimsPrincipal caller,
		DlrDbContext database,
		RefreshTokenService refresh)
	{
		if (caller.UserId() is not { } userId)
		{
			return Results.Unauthorized();
		}

		// Scoped to the caller's own devices, and 404 rather than 403 for anything else. A
		// distinct answer would turn this into a way to ask whether a device id exists.
		bool isOurs = await database
			.Set<Device>()
			.AnyAsync(device => device.Id == deviceId && device.UserId == userId);

		if (!isOurs)
		{
			return Results.NotFound();
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
		return Results.NoContent();
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
