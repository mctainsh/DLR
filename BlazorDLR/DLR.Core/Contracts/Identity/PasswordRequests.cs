namespace DLR.Core.Contracts.Identity;

/// <summary>
/// <c>POST /api/v1/auth/email</c> - records a recovery address and sends a 24-hour
/// confirmation link (§7.7, §7.14).
/// </summary>
/// <param name="Email">Where to send the link.</param>
public sealed record SetEmailRequest(string Email);

/// <summary>
/// <c>POST /api/v1/auth/confirm-email</c> (§7.14).
/// <para>
/// Unauthenticated, because the link is followed in whatever browser the mail was opened in.
/// The token is the proof.
/// </para>
/// </summary>
/// <param name="UserId">Whose address.</param>
/// <param name="Token">From the emailed link.</param>
/// <param name="DeviceId">The device to attach the returned session to, if the client has one.</param>
/// <param name="DeviceName">What that installation calls itself (§7.10).</param>
public sealed record ConfirmEmailRequest(
	Guid UserId,
	string Token,
	Guid? DeviceId = null,
	string? DeviceName = null);

/// <summary>
/// <c>POST /api/v1/auth/forgot-password</c> (§7.7).
/// <para>
/// Always answered <c>202</c>, whether or not the address exists. An address is a private
/// identifier rather than a public handle, so unlike a username it does not get to be
/// enumerable (§7.8).
/// </para>
/// </summary>
/// <param name="Email">The address to send a reset link to, if it belongs to anyone.</param>
public sealed record ForgotPasswordRequest(string Email);

/// <summary>
/// <c>POST /api/v1/auth/reset-password</c> (§7.7).
/// </summary>
/// <param name="UserId">Whose password.</param>
/// <param name="Token">From the emailed link; valid one hour.</param>
/// <param name="NewPassword">Subject to the same §7.2 policy as registration.</param>
public sealed record ResetPasswordRequest(Guid UserId, string Token, string NewPassword);

/// <summary>
/// <c>POST /api/v1/auth/change-password</c> - authed, and requires the current password (§7.7).
/// </summary>
/// <param name="CurrentPassword">Proof this is the account's owner and not a borrowed phone.</param>
/// <param name="NewPassword">Subject to the same §7.2 policy as registration.</param>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
