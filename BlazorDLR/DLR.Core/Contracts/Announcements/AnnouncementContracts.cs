namespace DLR.Core.Contracts.Announcements;

/// <summary>Whether the calling client is one this server will still talk to (§20.1).</summary>
public enum ClientSupport
{
	/// <summary>Current enough. Nothing is said.</summary>
	Supported = 0,

	/// <summary>Works, but behind. The rider is offered an update once and may dismiss it.</summary>
	UpdateAvailable = 1,

	/// <summary>Too old to serve. The app is walled off at its next launch.</summary>
	Unsupported = 2,
}

/// <summary>How loudly an announcement is drawn (§20.2).</summary>
public enum NoticeSeverity
{
	/// <summary>A quiet note.</summary>
	Information = 0,

	/// <summary>Something the rider should act on - a maintenance window, say.</summary>
	Warning = 1,

	/// <summary>Something happening now.</summary>
	Urgent = 2,
}

/// <summary>
/// One message from whoever runs this server, as a rider receives it (§20.2).
/// <para>
/// The same payload on both delivery paths: the launch check returns a list of these, and the hub
/// pushes one. A separate shape for the pushed copy would be two things to keep in step.
/// </para>
/// </summary>
/// <param name="Id">Identifies the message. What a device records when the rider clears it.</param>
/// <param name="Severity">How it is drawn.</param>
/// <param name="Title">One line, shown as the heading.</param>
/// <param name="Body">The message. Plain text, like every other body in the product (§17.2).</param>
/// <param name="ExpiresUtc">
/// After this instant it is never shown again, cleared or not. Carried to the client so a device
/// can drop its dismissal record for a message that can no longer appear.
/// </param>
public sealed record AnnouncementDto(
	Guid Id,
	NoticeSeverity Severity,
	string Title,
	string Body,
	DateTimeOffset ExpiresUtc);

/// <summary>
/// What <c>GET /api/v1/startup</c> answers: everything the server wants to say to an app that has
/// just opened (§20).
/// </summary>
/// <param name="Support">Whether this client is still one this server will serve.</param>
/// <param name="MinimumVersion">The floor, for the message the wall shows.</param>
/// <param name="RecommendedVersion">What the rider would be updating to.</param>
/// <param name="Live">Announcements inside their publish window right now, worst severity first.</param>
public sealed record StartupCheck(
	ClientSupport Support,
	string MinimumVersion,
	string RecommendedVersion,
	IReadOnlyList<AnnouncementDto> Live);

/// <summary>One announcement as the administration screen lists it, window and author included.</summary>
/// <param name="Id">The message.</param>
/// <param name="Severity">How riders see it drawn.</param>
/// <param name="Title">The heading.</param>
/// <param name="Body">The message.</param>
/// <param name="PublishFromUtc">When it starts appearing.</param>
/// <param name="ExpiresUtc">When it stops.</param>
/// <param name="CreatedUtc">When it was written.</param>
/// <param name="CreatedBy">Who wrote it, or null once that account is gone.</param>
public sealed record AdminAnnouncement(
	Guid Id,
	NoticeSeverity Severity,
	string Title,
	string Body,
	DateTimeOffset PublishFromUtc,
	DateTimeOffset ExpiresUtc,
	DateTimeOffset CreatedUtc,
	string? CreatedBy);

/// <summary>What an administrator sends to write or amend an announcement.</summary>
/// <param name="Severity">How it should be drawn.</param>
/// <param name="Title">The heading. Required.</param>
/// <param name="Body">The message. Required.</param>
/// <param name="PublishFromUtc">When it starts appearing. In the past means "now".</param>
/// <param name="ExpiresUtc">When it stops. Must be after <paramref name="PublishFromUtc"/>.</param>
public sealed record AdminAnnouncementRequest(
	NoticeSeverity Severity,
	string Title,
	string Body,
	DateTimeOffset PublishFromUtc,
	DateTimeOffset ExpiresUtc);

/// <summary>The caps the server enforces on an announcement, spelled once for both sides.</summary>
public static class AnnouncementLimits
{
	/// <summary>Longest title. A heading that wraps three lines on a phone is not a heading.</summary>
	public const int MaxTitleChars = 80;

	/// <summary>Longest body.</summary>
	public const int MaxBodyChars = 1000;

	/// <summary>
	/// The most announcements one launch may carry. A launch that opened six dialogs is a launch
	/// nobody finishes.
	/// </summary>
	public const int MaxLive = 5;
}
