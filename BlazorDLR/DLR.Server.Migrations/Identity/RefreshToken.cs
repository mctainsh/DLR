namespace DLR.Server.Data.Identity;

/// <summary>
/// One link in a session's chain (§7.4, §7.13).
/// <para>
/// Deliberately not a JWT. It has to be revocable, and it is only ever presented to one
/// endpoint, so there is nothing to gain from making it self-describing - and a real cost if
/// it ends up in a log.
/// </para>
/// </summary>
public sealed class RefreshToken
{
	/// <summary>Row identifier.</summary>
	public Guid Id { get; set; }

	/// <summary>Whose session this is.</summary>
	public Guid UserId { get; set; }

	/// <summary>Which installation holds it.</summary>
	public Guid DeviceId { get; set; }

	/// <summary>
	/// The chain this token belongs to. Every rotation keeps the family; detecting a reuse
	/// revokes all of it at once, which is what makes theft response a single statement
	/// rather than a walk along a linked list.
	/// </summary>
	public Guid FamilyId { get; set; }

	/// <summary>
	/// SHA-256 of the token as issued. The raw token is never stored: a database copy would
	/// make a backup, a dump, or a stray query into a set of working credentials.
	/// </summary>
	public byte[] TokenHash { get; set; } = [];

	/// <summary>
	/// What this token was rotated into. The idempotency window in §7.4 is keyed on it - a
	/// replay that arrives before the window closes gets this successor back rather than
	/// being read as theft.
	/// </summary>
	public Guid? SuccessorId { get; set; }

	/// <summary>When it was issued.</summary>
	public DateTimeOffset IssuedUtc { get; set; }

	/// <summary>
	/// Issued plus <c>Auth:RefreshTokenYears</c> (§7.4). Retained and set rather than made
	/// nullable so queries and indexes stay simple, and so a blanket expiry remains possible
	/// without a migration. Sessions do not expire in practice.
	/// </summary>
	public DateTimeOffset ExpiresUtc { get; set; }

	/// <summary>When it was redeemed, if it has been. Null means unused.</summary>
	public DateTimeOffset? UsedUtc { get; set; }

	/// <summary>When it was revoked, if it has been.</summary>
	public DateTimeOffset? RevokedUtc { get; set; }

	/// <summary>Why it was revoked, for the session list and for reading an incident back.</summary>
	public string? RevokedReason { get; set; }

	/// <summary>The device row, for cascade deletion.</summary>
	public Device? Device { get; set; }
}

/// <summary>Why a refresh token stopped working.</summary>
public static class RevocationReasons
{
	/// <summary>A token was presented twice outside the grace window (§7.4).</summary>
	public const string ReuseDetected = "reuse-detected";

	/// <summary>The user signed out on this device.</summary>
	public const string SignedOut = "signed-out";

	/// <summary>The session was revoked from the device list (§7.10).</summary>
	public const string SessionRevoked = "session-revoked";

	/// <summary>A password reset invalidated every session (§7.7).</summary>
	public const string PasswordReset = "password-reset";
}
