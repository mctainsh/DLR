using System.Net;
using DLR.Core.Contracts.Identity;
using Microsoft.AspNetCore.Identity;

namespace DLR.Server.Data.Identity;

/// <summary>
/// A rider's account (§7.2).
/// <para>
/// <c>UserName</c> is the whole of the identity: it is the login identifier <em>and</em> the
/// name other riders read off a map pin. There is no separate display name to default, to
/// keep in sync, or to choose between at render time — and because it is chosen once and can
/// never be changed, every client is free to denormalise it onto a cached member row, a stored
/// ride summary or an exported GPX with no invalidation logic anywhere.
/// </para>
/// <para>
/// The §7.13 columns arrive with the tasks that first need them, each in its own migration,
/// so the schema history reads as a record of what was built rather than one large guess.
/// </para>
/// </summary>
public sealed class AppUser : IdentityUser<Guid>, IProfileOwner
{
	/// <summary>
	/// The last time the server heard from this account (§7.10).
	/// <para>
	/// Written by the refresh that a client already makes at app start — no extra endpoint, no
	/// extra round trip — and throttled to one write an hour, so opening the app five times in
	/// a morning is one <c>UPDATE</c> rather than five.
	/// </para>
	/// <para>
	/// "Heard from" rather than "used the app" is the honest reading, and it happens to be
	/// exactly the semantics the 180-day inactivity sweep needs (§7.11). A rider who is active
	/// but permanently offline is bounded by the fact that their tracks eventually sync, and a
	/// single track makes the account ineligible for deletion anyway.
	/// </para>
	/// </summary>
	public DateTimeOffset LastActiveUtc { get; set; }

	/// <summary>When the account was created. The §7.8 ladder counts rows by this.</summary>
	public DateTimeOffset CreatedUtc { get; set; }

	/// <summary>
	/// The address this account was registered from (§7.8).
	/// <para>
	/// Personal data, and treated as such: the nightly job nulls it after 30 days (§7.11) —
	/// long enough to be useful for throttling, short enough not to be a standing record of
	/// where people signed up. Null therefore means "not recorded any more", not "unknown".
	/// </para>
	/// </summary>
	public IPAddress? CreatedByIp { get; set; }

	/// <summary>
	/// Set when the account was created past the ladder's threshold (§7.8).
	/// <para>
	/// The restriction is on the <em>social</em> surface, which is what abuse would be after:
	/// a restricted account can still record its own rides. It lifts by confirming an address,
	/// which costs an abuser N working mailboxes and costs a real person one click.
	/// </para>
	/// </summary>
	public bool RequiresEmailConfirmation { get; set; }

	/// <summary>Whether §7.8's restriction currently applies.</summary>
	public bool IsRestricted => RequiresEmailConfirmation && !EmailConfirmed;

	/// <summary>
	/// A name to show beside the username in a ride's member list (§7.3). Optional, editable,
	/// and never the map label — pins carry the username, which is the one that cannot change.
	/// </summary>
	public string? DisplayName { get; set; }

	/// <inheritdoc />
	public bool ShareDisplayName { get; set; }

	/// <inheritdoc />
	public bool SharePhoneNumber { get; set; }

	/// <inheritdoc />
	public bool ShareEmail { get; set; }
}
