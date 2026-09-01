using DLR.Core.Client;
using DLR.Core.Contracts.Announcements;
using DLR.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DLR.Server.Announcements;

/// <summary>Route names for the launch check (§20).</summary>
public static class StartupEndpoints
{
	/// <summary>Route name for the launch check.</summary>
	public const string CheckRouteName = "StartupCheck";
}

/// <summary>
/// <c>GET /api/v1/startup</c> - everything this server wants to say to an app that has just
/// opened: whether it is still a client this server serves, and any announcements that are live
/// (§20).
/// <para>
/// <strong>Anonymous, and that is the point of it.</strong> The wall exists for a build too old to
/// talk to this server correctly, which includes one too old to authenticate - a check behind
/// <c>[Authorize]</c> would be unreachable in exactly the case it was written for. Nothing here is
/// per-account: the verdict is about the binary and an announcement goes to everybody.
/// </para>
/// </summary>
[ApiController]
public sealed class StartupController : ControllerBase
{
	/// <summary>The launch check.</summary>
	/// <param name="database">The context.</param>
	/// <param name="clock">Decides which announcements are inside their window (§10.4).</param>
	/// <param name="client">
	/// The version the caller is, as an assembly version string. Absent or unparseable is answered
	/// <see cref="ClientSupport.Unsupported"/> - see <see cref="ClientRelease.Check"/>.
	/// </param>
	/// <param name="cancellationToken">Abandons the query.</param>
	[HttpGet("/api/v1/startup", Name = StartupEndpoints.CheckRouteName)]
	[AllowAnonymous]
	[EndpointSummary("Whether this client is still supported, and any live announcements.")]
	public async Task<ActionResult<StartupCheck>> Check(
		[FromServices] DlrDbContext database,
		[FromServices] TimeProvider clock,
		[FromQuery] string? client,
		CancellationToken cancellationToken)
	{
		// Projected before it is limited, so this is one flat SELECT ... LIMIT rather than a
		// subquery that reads every mapped column - body included - to throw most of them away.
		List<AnnouncementDto> live = await AnnouncementQueries
			.Live(database, clock.GetUtcNow())
			.Select(AnnouncementQueries.ToDto)
			.Take(AnnouncementLimits.MaxLive)
			.ToListAsync(cancellationToken);

		return Ok(new StartupCheck(
			Support: ClientRelease.Check(ClientRelease.Parse(client)),
			MinimumVersion: ClientRelease.MinimumText,
			RecommendedVersion: ClientRelease.RecommendedText,
			Live: live));
	}
}
