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
	/// When the 150-day inactivity warning was sent, or null if it has not been (§7.11).
	/// <para>
	/// <strong>Not derivable from the other two columns, which is why it is a column.</strong> The
	/// warning window is thirty days wide and the job runs nightly, so "warn when idle ≥ 150 days"
	/// with nothing recorded emails the same person on thirty consecutive mornings — which reads as
	/// a broken service rather than as a courtesy, and is exactly the shape of thing that gets a
	/// sending domain blocked.
	/// </para>
	/// <para>
	/// Cleared whenever the account is heard from again, so a rider who comes back and then goes
	/// quiet a year later is warned a second time rather than deleted in silence.
	/// </para>
	/// </summary>
	public DateTimeOffset? InactivityWarnedUtc { get; set; }

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

	/// <summary>
	/// The background this rider's marker is drawn in on a live ride map, as <c>#rrggbb</c>, or
	/// null for <c>MarkerColours.Default</c> (§16.3).
	/// <para>
	/// <strong>No sharing switch, unlike the three fields above it.</strong> The other optional
	/// fields are facts about a person and default to private; this one is only meaningful to the
	/// riders already looking at the map it is drawn on, and a colour nobody else could see would
	/// be a setting with no effect.
	/// </para>
	/// <para>
	/// Null rather than a stored default, so an account created before the column existed and an
	/// account that never chose are the same row. Every render path goes through
	/// <c>MarkerColours.Or</c>, which is where the default lives.
	/// </para>
	/// </summary>
	public string? MarkerColour { get; set; }
}
