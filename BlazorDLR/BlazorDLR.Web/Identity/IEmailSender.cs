namespace DLR.Server.Identity;

/// <summary>
/// Outbound email (§7.12). An interface rather than a concrete sender because every
/// email the server sends is asserted on in a test, and because the transport is
/// expected to change - the design's cutover from Zoho SMTP to ZeptoMail is five
/// configuration values and no code.
/// </summary>
public interface IEmailSender
{
	/// <summary>Sends one message, or throws if it cannot be handed to the transport.</summary>
	Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>One outbound message.</summary>
/// <param name="To">The recipient address.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="PlainTextBody">
/// The body. Plain text is the required part: an account-recovery link that only renders
/// in an HTML client is an account-recovery link some people do not have.
/// </param>
/// <param name="HtmlBody">Optional HTML alternative.</param>
public sealed record EmailMessage(
	string To,
	string Subject,
	string PlainTextBody,
	string? HtmlBody = null);
