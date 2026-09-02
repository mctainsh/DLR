using DLR.Server.Data.Identity;
using Microsoft.Extensions.Options;

namespace DLR.Server.Identity;

/// <summary>Where the links in account emails point.</summary>
public sealed class AccountLinkOptions
{
	/// <summary>Configuration section name.</summary>
	public const string Section = "Links";

	/// <summary>
	/// The site the links resolve against.
	/// <para>
	/// <c>https://</c> universal links with a web fallback page, so reset works whether or not
	/// the app is installed (§7.7). An account that cannot be recovered from a browser is an
	/// account that cannot be recovered from a phone that has been lost - which is the case
	/// the whole feature exists for.
	/// </para>
	/// </summary>
	public string BaseUrl { get; set; } = "https://dumbluckrides.example";
}

/// <summary>The two links this project emails, and the words around them (§7.7, §7.12).</summary>
/// <param name="email">The transport.</param>
/// <param name="links">Where the links point.</param>
public sealed class AccountEmails(IEmailSender email, IOptions<AccountLinkOptions> links)
{
	/// <summary>Sends the 24-hour confirmation link.</summary>
	/// <param name="user">Who is confirming.</param>
	/// <param name="address">Where to send it - the new address, not necessarily the stored one.</param>
	/// <param name="token">The confirmation token.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public Task SendConfirmationAsync(
		AppUser user,
		string address,
		string token,
		CancellationToken cancellationToken = default)
	{
		string link = Link("confirm-email", user.Id, token);

		return email.SendAsync(
			new EmailMessage(
				address,
				"Confirm your email address",
				$"""
				Hello {user.UserName},

				Confirm this address so your Dumb Luck Routes account can be recovered if you
				forget your password or lose your phone:

				{link}

				The link works for 24 hours. Until you use it, this address does nothing -
				including letting you reset your password.

				If you did not ask for this, ignore it. Nothing has changed on your account.
				""".ReplaceLineEndings("\n")),
			cancellationToken);
	}

	/// <summary>Sends the one-hour reset link.</summary>
	/// <param name="user">Whose password.</param>
	/// <param name="token">The reset token.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public Task SendPasswordResetAsync(
		AppUser user,
		string token,
		CancellationToken cancellationToken = default)
	{
		string link = Link("reset-password", user.Id, token);

		return email.SendAsync(
			new EmailMessage(
				user.Email!,
				"Reset your Dumb Luck Routes password",
				$"""
				Hello {user.UserName},

				Choose a new password here:

				{link}

				The link works for one hour. Using it signs you out on every device, which is
				deliberate - if somebody else prompted this, they are signed out too.

				If you did not ask for this, ignore it. Your password has not changed and
				nobody has been given access to your account.
				""".ReplaceLineEndings("\n")),
			cancellationToken);
	}

	/// <summary>
	/// Tells a dormant account it is about to be deleted (§7.11).
	/// <para>
	/// The one email in this project that asks for nothing. It carries no link and no token, because
	/// it does not need to: signing in is what saves the account, and an unsolicited "click here to
	/// keep your account" is indistinguishable from the phishing message somebody will eventually
	/// send in our name.
	/// </para>
	/// </summary>
	/// <param name="user">Whose account is dormant.</param>
	/// <param name="deleteAfterUtc">When the sweep will take it.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public Task SendInactivityWarningAsync(
		AppUser user,
		DateTimeOffset deleteAfterUtc,
		CancellationToken cancellationToken = default) =>
		email.SendAsync(
			new EmailMessage(
				user.Email!,
				"Your Dumb Luck Routes account will be deleted",
				$"""
				Hello {user.UserName},

				We have not heard from this account since it was last used, and it holds no
				rides, no tracks and no group memberships. Accounts like that are deleted
				automatically rather than kept forever.

				Yours will be removed on or after {deleteAfterUtc:D}.

				To keep it, just open the app and sign in. That is all - there is nothing to
				click here, and we will never email you a link that keeps an account alive.

				If you would rather it went, do nothing.
				""".ReplaceLineEndings("\n")),
			cancellationToken);

	private string Link(string page, Guid userId, string token) =>
		$"{links.Value.BaseUrl.TrimEnd('/')}/{page}" +
		$"?userId={userId}&token={Uri.EscapeDataString(token)}";
}
