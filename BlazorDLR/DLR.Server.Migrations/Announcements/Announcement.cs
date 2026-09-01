using DLR.Core.Contracts.Announcements;
using DLR.Server.Data.Identity;

namespace DLR.Server.Data.Announcements;

/// <summary>
/// One message from whoever runs this server to everybody using it (§20.2).
/// <para>
/// <strong>Its window is evaluated on read, never swept.</strong> A job that flipped an
/// <c>IsLive</c> flag would leave a gap as wide as its own interval, widest exactly when it is
/// behind - the reasoning <see cref="Comments.Poll.ClosesUtc"/> already carries. Every query
/// compares against the clock instead, so there is no job to fail.
/// </para>
/// </summary>
public sealed class Announcement
{
	/// <summary>Row identifier, and what a device records when the rider clears the message.</summary>
	public Guid Id { get; set; }

	/// <summary>How loudly it is drawn.</summary>
	public NoticeSeverity Severity { get; set; }

	/// <summary>The heading.</summary>
	public string Title { get; set; } = string.Empty;

	/// <summary>The message. Plain text, like every other body in the product (§17.2).</summary>
	public string Body { get; set; } = string.Empty;

	/// <summary>When it starts appearing. Also what the broadcast sweep watches for (§20.3).</summary>
	public DateTimeOffset PublishFromUtc { get; set; }

	/// <summary>When it stops appearing, cleared or not.</summary>
	public DateTimeOffset ExpiresUtc { get; set; }

	/// <summary>When an administrator wrote it.</summary>
	public DateTimeOffset CreatedUtc { get; set; }

	/// <summary>Which administrator, or null once that account is gone.</summary>
	public Guid? CreatedByUserId { get; set; }

	/// <summary>The author.</summary>
	public AppUser? CreatedBy { get; set; }
}
