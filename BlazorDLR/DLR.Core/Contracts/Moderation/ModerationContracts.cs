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

/// <summary>
/// One report on the operator's queue (§17.7, §10.2).
/// <para>
/// Everything needed to judge it without going near the content it is about - which is the point,
/// because the content may already have been deleted and the snapshot is all that is left of it.
/// </para>
/// </summary>
/// <param name="ReportId">Which report.</param>
/// <param name="TargetKind">"Marker" or "Comment".</param>
/// <param name="TargetId">Which piece of content, so a restore knows what to release.</param>
/// <param name="AuthorUserName">Who wrote it, or null when the account has since gone.</param>
/// <param name="ReportedByUserName">Who reported it.</param>
/// <param name="Reason">What they said was wrong with it.</param>
/// <param name="ContentSnapshot">The content as it read when it was reported, as stored JSON.</param>
/// <param name="CreatedUtc">When it was reported.</param>
/// <param name="ReportsOnTarget">
/// How many people have reported this same piece of content. One report hides it, so this is the
/// difference between a lone complaint and something a whole adventure objected to.
/// </param>
public sealed record OpenReport(
	Guid ReportId,
	string TargetKind,
	Guid TargetId,
	string? AuthorUserName,
	string ReportedByUserName,
	string Reason,
	string ContentSnapshot,
	DateTimeOffset CreatedUtc,
	int ReportsOnTarget);

/// <summary>What an operator decided about a piece of reported content (§17.7).</summary>
/// <param name="ReportsResolved">
/// How many reports were closed. Every open report on the same content is resolved together -
/// leaving one open would keep the content hidden after it had been cleared.
/// </param>
/// <param name="ContentRemoved">Whether the content was deleted rather than restored.</param>
public sealed record ReportResolved(int ReportsResolved, bool ContentRemoved);
