using DLR.Core.Client;
using DLR.Core.Contracts.Announcements;

namespace DLR.UI.Tests.Fakes;

/// <summary>
/// The two payloads every announcement test starts from (§20).
/// <para>
/// One factory rather than one per suite: the two copies this replaced had drifted into exposing
/// different optional parameters, so the same helper name meant different things depending on
/// which file you were reading.
/// </para>
/// </summary>
internal static class Notices
{
	/// <summary>The instant every announcement test is anchored to.</summary>
	internal static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	/// <summary>One announcement, live for four hours from <see cref="Now"/> unless told otherwise.</summary>
	/// <param name="title">Its heading, which is what tests assert on.</param>
	/// <param name="severity">How loudly it is drawn.</param>
	/// <param name="expiresIn">When it stops, measured from <see cref="Now"/>. Negative is already over.</param>
	internal static AnnouncementDto Announcement(
		string title,
		NoticeSeverity severity = NoticeSeverity.Warning,
		TimeSpan? expiresIn = null) =>
		new(Guid.NewGuid(), severity, title, "Something is happening.",
			Now.Add(expiresIn ?? TimeSpan.FromHours(4)));

	/// <summary>A server that is happy with this client, carrying the given announcements.</summary>
	/// <param name="live">What is inside its window right now.</param>
	internal static StartupCheck Supported(params AnnouncementDto[] live) =>
		new(ClientSupport.Supported,
			ClientRelease.MinimumText,
			ClientRelease.RecommendedText,
			live);
}
