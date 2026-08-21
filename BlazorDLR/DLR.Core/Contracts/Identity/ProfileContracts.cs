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
/// <param name="MarkerColour">
/// The background of this rider's marker on a live ride map, as <c>#rrggbb</c>, or null for
/// <c>MarkerColours.Default</c> (§16.3).
/// <para>
/// No sharing switch, unlike the three fields above it. It is not a fact about the rider — it is
/// how they appear on a map co-members are already looking at, and a colour nobody else could see
/// would be a setting with no effect.
/// </para>
/// </param>
/// <param name="AvatarPhotoId">
/// The photograph shown beside this rider's username wherever it is read, or null (§7.3, §16.4).
/// <para>
/// No sharing switch, on <paramref name="MarkerColour"/>'s reasoning rather than the three above
/// it: a profile photograph has no private use, so adding one is the consent and removing it is
/// how that is withdrawn. It is read-only here — <c>PUT /me/avatar</c> sets it, so that a client
/// which has not been taught about it cannot clear it by saving the rest of the form. See
/// <see cref="SetAvatarRequest"/>.
/// </para>
/// </param>
public sealed record OwnProfile(
	string? DisplayName,
	string? PhoneNumber,
	string? Email,
	bool EmailConfirmed,
	bool ShareDisplayName,
	bool SharePhoneNumber,
	bool ShareEmail,
	string? MarkerColour = null,
	Guid? AvatarPhotoId = null);

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
/// <param name="MarkerColour">
/// <c>#rrggbb</c>, or null to go back to the default marker colour (§16.3). Anything else is a
/// 400 rather than a silent fallback — see <c>MarkerColours.TryNormalise</c>.
/// </param>
public sealed record UpdateProfileRequest(
	string? DisplayName = null,
	string? PhoneNumber = null,
	bool ShareDisplayName = false,
	bool SharePhoneNumber = false,
	bool ShareEmail = false,
	string? MarkerColour = null);
