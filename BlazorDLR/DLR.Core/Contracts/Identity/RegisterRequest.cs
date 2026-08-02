namespace DLR.Core.Contracts.Identity;

/// <summary>
/// <c>POST /api/v1/auth/register</c> (§7.14).
/// </summary>
/// <param name="UserName">
/// The login identifier and the map label, in one field. Permanent from the moment this
/// request succeeds — there is no endpoint that changes it (§7.2), which is why the client
/// confirms the spelling before sending.
/// </param>
/// <param name="Password">
/// At least 10 characters and no composition rules (§7.2). For an account registered without
/// an email address this is the only credential and there is no reset path, so it matters
/// more here than in most applications.
/// </param>
/// <param name="Email">
/// Optional, and the registration screen states what omitting it costs: no password reset, no
/// recovery from a lost device, and no warning before the 180-day deletion sweep (§7.11).
/// </param>
/// <param name="DeviceName">What this installation calls itself, for the session list (§7.10).</param>
/// <param name="DeviceId">
/// The device id this client was given last time, if it has one. Registration signs you in
/// (§7.2), so it starts a session and therefore needs somewhere to hang the token family.
/// </param>
public sealed record RegisterRequest(
	string UserName,
	string Password,
	string? Email = null,
	Guid? DeviceId = null,
	string? DeviceName = null);
