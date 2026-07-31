namespace DLR.Core.Contracts.Identity;

/// <summary>
/// <c>GET /api/v1/me/profile</c> — the owner's own view (§7.14).
/// <para>
/// Not a <see cref="SharedProfile"/>: this is the rider looking at their own settings, so it
/// carries the values <em>and</em> the switches, including values that are recorded but not
/// shared. <see cref="SharedProfile"/> is what anybody else gets, and it is a different type on
/// purpose — one of them can be handed to a stranger and the other cannot.
/// </para>
/// </summary>
/// <param name="DisplayName">Recorded value, shared or not.</param>
/// <param name="PhoneNumber">Recorded value, shared or not.</param>
/// <param name="Email">Recorded value, shared or not.</param>
/// <param name="EmailConfirmed">Whether it works as a recovery address (§7.7).</param>
/// <param name="ShareDisplayName">Whether co-members see the display name.</param>
/// <param name="SharePhoneNumber">Whether co-members see the phone number.</param>
/// <param name="ShareEmail">Whether co-members see the email address.</param>
public sealed record OwnProfile(
	string? DisplayName,
	string? PhoneNumber,
	string? Email,
	bool EmailConfirmed,
	bool ShareDisplayName,
	bool SharePhoneNumber,
	bool ShareEmail);

/// <summary>
/// <c>PUT /api/v1/me/profile</c> (§7.14).
/// <para>
/// The email <em>address</em> is deliberately absent. Setting one requires confirmation and
/// stays on <c>POST /auth/email</c>; this endpoint only controls whether a confirmed address is
/// shared (§7.3).
/// </para>
/// </summary>
/// <param name="DisplayName">Null clears it.</param>
/// <param name="PhoneNumber">Null clears it. Never verified (§7.3).</param>
/// <param name="ShareDisplayName">Off unless said otherwise.</param>
/// <param name="SharePhoneNumber">Off unless said otherwise.</param>
/// <param name="ShareEmail">Off unless said otherwise.</param>
public sealed record UpdateProfileRequest(
	string? DisplayName = null,
	string? PhoneNumber = null,
	bool ShareDisplayName = false,
	bool SharePhoneNumber = false,
	bool ShareEmail = false);
