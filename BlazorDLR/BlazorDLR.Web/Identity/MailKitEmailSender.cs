using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DLR.Server.Identity;

/// <summary>SMTP settings (§7.12).</summary>
public sealed class EmailOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "Email";

	/// <summary>
	/// The SMTP host.
	/// <para>
	/// Zoho's is <em>region-specific</em> — an account in the Australian datacentre is
	/// <c>smtp.zoho.com.au</c>, and pointing at <c>smtp.zoho.com</c> surfaces as an
	/// <em>authentication</em> failure rather than a region mismatch. That sends you hunting
	/// for a credential bug that is not there, so it is written down here rather than
	/// discovered twice.
	/// </para>
	/// </summary>
	public string Host { get; set; } = string.Empty;

	/// <summary>587 for STARTTLS, 465 for implicit TLS.</summary>
	public int Port { get; set; } = 587;

	/// <summary>The mailbox or Mail Agent identity.</summary>
	public string UserName { get; set; } = string.Empty;

	/// <summary>
	/// An app-specific password once 2FA is on, which it should be. The normal mailbox
	/// password will simply not authenticate. Never in a file that ships with the code.
	/// </summary>
	public string Password { get; set; } = string.Empty;

	/// <summary>
	/// Must be the authenticated mailbox or a verified alias — Zoho rejects arbitrary senders,
	/// so <c>no-reply@…</c> has to exist as a real alias. It ends up in every template and in
	/// users' allow-lists, so it is worth deciding once.
	/// </summary>
	public string FromAddress { get; set; } = string.Empty;

	/// <summary>The display name on the From header.</summary>
	public string FromName { get; set; } = "Dumb Luck Routes";
}

/// <summary>
/// The real transport (§7.12).
/// <para>
/// MailKit rather than <c>System.Net.Mail.SmtpClient</c>, which is obsolete for new
/// development — and it is what makes the eventual move from Zoho Mail to ZeptoMail free:
/// ZeptoMail speaks SMTP too, so the cutover is a host and a credential in configuration with
/// no code change behind <see cref="IEmailSender"/>.
/// </para>
/// </summary>
/// <param name="options">Host, credentials and sender.</param>
public sealed class MailKitEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
	/// <inheritdoc />
	public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
	{
		EmailOptions settings = options.Value;

		MimeMessage mime = new();

		mime.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
		mime.To.Add(MailboxAddress.Parse(message.To));
		mime.Subject = message.Subject;

		BodyBuilder body = new() { TextBody = message.PlainTextBody };

		if (!string.IsNullOrWhiteSpace(message.HtmlBody))
		{
			body.HtmlBody = message.HtmlBody;
		}

		mime.Body = body.ToMessageBody();

		using SmtpClient client = new();

		await client.ConnectAsync(
			settings.Host,
			settings.Port,
			settings.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
			cancellationToken);

		await client.AuthenticateAsync(settings.UserName, settings.Password, cancellationToken);
		await client.SendAsync(mime, cancellationToken);
		await client.DisconnectAsync(quit: true, cancellationToken);
	}
}
