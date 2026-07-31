using DLR.Server.Data.Identity;

namespace DLR.Server.Identity;

/// <summary>
/// Tells a rider when their account is signed in on a device it has not seen before (§7.10).
/// <para>
/// Silently impossible without an address, which is another line in §7.2's trade-off: an
/// account registered without an email cannot be warned that somebody else is now signed into
/// it, and cannot recover if they are. That is stated on the registration screen because it is
/// not something a person can work out for themselves.
/// </para>
/// </summary>
/// <param name="email">The transport.</param>
/// <param name="clock">The project's clock (§10.4).</param>
/// <param name="logger">Where a failed alert is recorded.</param>
public sealed class NewDeviceNotifier(
	IEmailSender email,
	TimeProvider clock,
	ILogger<NewDeviceNotifier> logger)
{
	/// <summary>Sends the alert, if there is anywhere to send it.</summary>
	/// <param name="user">Who signed in.</param>
	/// <param name="deviceName">What the client called itself, if anything.</param>
	/// <param name="cancellationToken">Cancellation.</param>
	public async Task NewDeviceSignedInAsync(
		AppUser user,
		string? deviceName,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(user.Email))
		{
			return;
		}

		string device = string.IsNullOrWhiteSpace(deviceName) ? "a new device" : deviceName;

		try
		{
			await email.SendAsync(
				new EmailMessage(
					user.Email,
					"New sign-in to your Dumb Luck Rides account",
					$"""
					Your account was signed in on {device} at {clock.GetUtcNow():u}.

					If that was you, there is nothing to do.

					If it was not, open Settings → Signed-in devices and remove it. That ends
					that session immediately. Change your password as well, because whoever
					signed in knows it.
					""".ReplaceLineEndings("\n")),
				cancellationToken);
		}
		catch (Exception exception)
		{
			// A sign-in that already succeeded is not undone by a mail server being down, and
			// failing the request here would turn an outage at the transport into an outage
			// at the login screen (§7.12). Logged loudly, because a security alert nobody
			// received is worth knowing about.
			logger.LogError(
				exception,
				"Could not send the new-device alert for {UserId}.",
				user.Id);
		}
	}
}
