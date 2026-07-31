namespace DLR.Core.Contracts.Identity;

/// <summary>
/// One row of Settings → Signed-in devices (§7.10).
/// <para>
/// This screen carries more weight here than in most applications. Sessions are permanent, so
/// revoking one is the only thing that ever ends it — there is no expiry quietly cleaning up
/// after a phone that was sold, lost, or handed on.
/// </para>
/// </summary>
/// <param name="DeviceId">What to pass to the revoke endpoint.</param>
/// <param name="Name">
/// What the rider will recognise it as. Client-supplied and never verified — it exists so
/// somebody can pick the right row, not to prove anything.
/// </param>
/// <param name="LastSeenUtc">
/// When the server last heard from it, to the nearest hour (§7.10's throttle). The UI renders
/// this as "last seen 2 hours ago", which is well inside that resolution.
/// </param>
/// <param name="IsCurrent">
/// Whether this is the device asking. Marked so nobody signs themselves out while trying to
/// remove the phone they no longer have.
/// </param>
public sealed record DeviceSession(
	Guid DeviceId,
	string? Name,
	DateTimeOffset LastSeenUtc,
	bool IsCurrent);
