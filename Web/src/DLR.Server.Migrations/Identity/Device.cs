namespace DLR.Server.Data.Identity;

/// <summary>
/// One installation of the app, and the thing a refresh-token family belongs to (§7.10).
/// <para>
/// Session management is a feature rather than an afterthought here, and it matters more than
/// it would elsewhere: sessions are permanent (§7.4), so revoking one is the <em>only</em>
/// thing that ends it.
/// </para>
/// <para>
/// The identifier is assigned by the server, never accepted from the client. A client sends
/// back the id it was given last time, and one belonging to somebody else is not an error to
/// report — it simply does not match, and a new device is created. That way guessing another
/// rider's device id gains an attacker a row of their own rather than a foothold in someone
/// else's session list.
/// </para>
/// </summary>
public sealed class Device
{
	/// <summary>Server-assigned identifier; the access token's <c>dev</c> claim.</summary>
	public Guid Id { get; set; }

	/// <summary>Who this installation belongs to.</summary>
	public Guid UserId { get; set; }

	/// <summary>
	/// What the rider will recognise it as — "iPhone 15" (§7.10). Supplied by the client and
	/// never verified, because the only thing it is used for is helping somebody pick the
	/// right row to revoke. Null when a client did not send one.
	/// </summary>
	public string? Name { get; set; }

	/// <summary>When this installation first signed in.</summary>
	public DateTimeOffset CreatedUtc { get; set; }

	/// <summary>
	/// When the server last heard from it. Written on the refresh that already happens at app
	/// start, throttled to one write an hour (§7.10).
	/// </summary>
	public DateTimeOffset LastSeenUtc { get; set; }

	/// <summary>The account, for cascade deletion.</summary>
	public AppUser? User { get; set; }
}
