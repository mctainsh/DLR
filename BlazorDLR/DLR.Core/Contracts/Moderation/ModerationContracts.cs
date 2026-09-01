namespace DLR.Core.Contracts.Moderation;

/// <summary>Reporting a marker or a post (§17.7).</summary>
/// <param name="Reason">
/// What the reporter says is wrong with it. Free text, because the useful report is usually a
/// sentence - a fixed category list can be added later without changing what is stored.
/// </param>
public sealed record ReportContentRequest(string Reason);

/// <summary>A filed report, as the reporter sees it back (§17.7).</summary>
/// <param name="ReportId">Which report.</param>
/// <param name="CreatedUtc">When it was filed.</param>
public sealed record ContentReported(Guid ReportId, DateTimeOffset CreatedUtc);

/// <summary>Blocking a rider (§16.5).</summary>
/// <param name="UserId">Who to hide.</param>
public sealed record BlockUserRequest(Guid UserId);

/// <summary>One row of the caller's block list (§16.5).</summary>
/// <param name="UserId">Who is hidden.</param>
/// <param name="UserName">Their handle (§7.2).</param>
/// <param name="CreatedUtc">When they were blocked.</param>
public sealed record BlockedRider(Guid UserId, string UserName, DateTimeOffset CreatedUtc);
