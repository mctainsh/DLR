namespace DLR.Core.Contracts.Identity;

/// <summary>
/// <c>PUT /api/v1/me/avatar</c> — the photograph shown beside the caller's username (§7.3, §16.4).
/// <para>
/// <strong>Its own sub-resource rather than a field on <see cref="UpdateProfileRequest"/>, for the
/// same reason the home private area is one (§10.1).</strong> That endpoint takes a whole profile
/// and writes every field on it, so an avatar carried inside it would be cleared by any client
/// that had not been taught about it — a rider editing their phone number in an older build would
/// silently lose their photograph. <c>DELETE</c> on the same route is how it is removed, which
/// makes removing it something a rider does on purpose rather than something a save can do by
/// accident.
/// </para>
/// </summary>
/// <param name="PhotoId">
/// A photograph the caller uploaded to <c>POST /api/v1/photos</c>. Somebody else's identifier is
/// refused rather than silently ignored — otherwise a guessed identifier would put their face
/// beside the caller's name.
/// </param>
public sealed record SetAvatarRequest(Guid PhotoId);

/// <summary>
/// One rider's profile photograph, or the fact that they have none (§7.3).
/// <para>
/// Keyed on the username rather than the account id, because the username is what the screens
/// asking this question actually hold. A comment carries <c>AuthorUserName</c>, a marker carries
/// <c>CreatedByUserName</c>, a shared route carries <c>OwnerName</c> — several of them carry no
/// identifier at all, and the username is unique, immutable and already the label being drawn
/// (§7.2).
/// </para>
/// </summary>
/// <param name="UserName">Whose. Echoed back exactly as stored, whatever case was asked for.</param>
/// <param name="PhotoId">
/// The photograph, or null for a rider who has not added one. Null is a real answer and is worth
/// caching — it is what stops a screen full of names asking again on every render.
/// </param>
/// <remarks>
/// The <c>Dto</c> suffix is here to leave the plain name to the shared component that draws the
/// circle, which is what nearly every reader is looking for. Same reason <c>MarkerDto</c> and
/// <c>RiderPositionDto</c> carry one.
/// </remarks>
public sealed record RiderAvatarDto(string UserName, Guid? PhotoId);

/// <summary>
/// The shape of <c>GET /api/v1/users/avatars</c> (§7.3).
/// <para>
/// <strong>A batch, and it has to be.</strong> A ride thread, a member list and a browse page all
/// render dozens of names at once, and one request per name would turn opening a screen into forty
/// round trips over a phone connection. The names go up as one comma-separated <c>names</c>
/// parameter — a username cannot contain a comma (§7.2's allowed set is letters, digits and
/// <c>-._</c>), so the separator needs no escaping and cannot be smuggled.
/// </para>
/// </summary>
public static class AvatarLookup
{
	/// <summary>
	/// The most names one request may ask about.
	/// <para>
	/// Above the largest screen anybody renders — a ride is capped well below this and a browse
	/// page holds twenty — and low enough that the query stays a bounded <c>IN</c> list and the
	/// URL stays inside every proxy's line limit. Anything past this is ignored rather than
	/// refused: a client asking about too many names should get the first hundred avatars, not an
	/// error that leaves a screen with none.
	/// </para>
	/// </summary>
	public const int MaxNames = 100;

	/// <summary>The separator between names in the query string.</summary>
	public const char Separator = ',';
}
