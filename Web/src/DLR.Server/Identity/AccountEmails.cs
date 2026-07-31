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
	/// account that cannot be recovered from a phone that has been lost — which is the case
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
	/// <param name="address">Where to send it — the new address, not necessarily the stored one.</param>
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

				Confirm this address so your Dumb Luck Rides account can be recovered if you
				forget your password or lose your phone:

				{link}

				The link works for 24 hours. Until you use it, this address does nothing —
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
				"Reset your Dumb Luck Rides password",
				$"""
				Hello {user.UserName},

				Choose a new password here:

				{link}

				The link works for one hour. Using it signs you out on every device, which is
				deliberate — if somebody else prompted this, they are signed out too.

				If you did not ask for this, ignore it. Your password has not changed and
				nobody has been given access to your account.
				""".ReplaceLineEndings("\n")),
			cancellationToken);
	}

	/// <summary>
	/// Tells the holder of an address that somebody tried to register with it (§7.8).
	/// <para>
	/// The counterpart to registration answering identically whether or not the address is
	/// taken: the person who cannot be told is the one asking, and the person who can be told
	/// is the one it actually concerns.
	/// </para>
	/// </summary>
	/// <param name="owner">Whoever holds the address.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public Task SendRegistrationAttemptAsync(AppUser owner, CancellationToken cancellationToken = default) =>
		email.SendAsync(
			new EmailMessage(
				owner.Email!,
				"Someone tried to register with your email address",
				$"""
				Hello {owner.UserName},

				Somebody just tried to create a Dumb Luck Rides account using this email
				address. Your account has not changed and nobody has been given access to it.

				If that was you, you already have an account — sign in as {owner.UserName}, or
				reset your password if you have forgotten it.

				If it was not you, there is nothing to do. We did not tell them whether this
				address is registered.
				""".ReplaceLineEndings("\n")),
			cancellationToken);

	private string Link(string page, Guid userId, string token) =>
		$"{links.Value.BaseUrl.TrimEnd('/')}/{page}" +
		$"?userId={userId}&token={Uri.EscapeDataString(token)}";
}
